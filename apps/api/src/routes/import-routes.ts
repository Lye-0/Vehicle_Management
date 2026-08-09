import { and, asc, eq } from 'drizzle-orm'
import type { BatchItem } from 'drizzle-orm/batch'
import { customers, maintenanceDocuments, maintenanceItems, paymentEntries, paymentRecords, salesDocumentItems, salesDocuments, vehicles } from '@vehicle-management/database'
import { requireAdminOrganizationContext } from '../auth/organization'
import { UnauthorizedError } from '../auth/firebase'
import { createDatabase } from '../db/client'
import { assertRequestContentLength, HttpError, corsHeaders, jsonResponse } from '../http'
import { normalizeCalendarDate } from '../lib/date-utils'

const importResources = ['customers', 'vehicles', 'sales', 'maintenance', 'payments'] as const
const paymentMethods = new Set(['現金', '銀行振込', 'クレジットカード', 'その他'])
type ImportResource = typeof importResources[number]
type ImportOutcome = 'imported' | 'updated'

const expectedHeaders: Record<ImportResource, string[]> = {
  customers: ['顧客ID', '顧客番号', '顧客名', 'ふりがな', '電話番号', 'メールアドレス', '郵便番号', '住所', 'メモ', '車両台数'],
  vehicles: ['車両ID', '顧客ID', '顧客名', 'メーカー', '車名', '型式', '登録番号', '車台番号', '年式', '車検満了日', '走行距離', '車体色', '排気量', 'ミッション', '記録簿', '備考'],
  sales: ['書類ID', '書類番号', '書類種別', 'ステータス', '顧客名', '車名', '登録番号', '発行日', '支払期限', '税率', '小計', '消費税', '合計', '明細'],
  maintenance: ['書類ID', '書類番号', '書類種別', '入庫区分', 'ステータス', '顧客名', '車名', '登録番号', '入庫日', '出庫予定日', '支払期限', '税率', '小計', '消費税', '合計', '明細'],
  payments: ['請求書ID', '請求書種別', '請求書番号', '顧客名', '車名', '登録番号', '発行日', '支払期限', '請求金額', '入金済み', '未入金', '入金日', '入金方法', 'メモ'],
}

const requiredHeaders: Record<ImportResource, string[]> = {
  customers: ['顧客名'],
  vehicles: ['車名'],
  sales: ['書類番号', '書類種別', '顧客名', '発行日', '消費税'],
  maintenance: ['書類番号', '書類種別', '入庫区分', '顧客名', '発行日', '消費税'],
  payments: ['請求書ID', '請求書種別'],
}

export async function handleImportRoutes(request: Request, env: Env): Promise<Response | null> {
  const pathname = new URL(request.url).pathname.replace(/\/$/, '') || '/'
  const match = pathname.match(/^\/api\/import\/(customers|vehicles|sales|maintenance|payments)\/(preview|commit)$/)
  if (!match) return null

  try {
    if (request.method !== 'POST') throw new HttpError(405, 'この操作には対応していません。')
    const resource = match[1] as ImportResource
    const mode = match[2]
    const database = createDatabase(env.DB)
    const context = await requireAdminOrganizationContext(request, env, database)
    const parsed = await readCsvUpload(request, resource)
    const headerError = validateHeaders(resource, parsed.headers)
    if (headerError) throw new HttpError(400, headerError)
    if (mode === 'preview') return jsonResponse({ resource, totalRows: parsed.rows.length, previewRows: parsed.rows.slice(0, 10), errors: validateRows(resource, parsed.rows).slice(0, 100) }, 200, env)
    return jsonResponse({ resource, ...(await importRows(database, context.organization.organizationId, resource, parsed.rows)) }, 200, env)
  } catch (error) {
    if (error instanceof UnauthorizedError) return jsonResponse({ error: error.message }, 401, env)
    if (error instanceof HttpError) return jsonResponse({ error: error.message }, error.status, env)
    console.error(error)
    return jsonResponse({ error: 'CSVの取込に失敗しました。' }, 500, env)
  }
}

async function readCsvUpload(request: Request, resource: ImportResource) {
  assertRequestContentLength(request, 6 * 1024 * 1024, { required: true })
  const formData = await request.formData().catch(() => null)
  const file = formData?.get('file')
  if (!(file instanceof File)) throw new HttpError(400, 'CSVファイルを選択してください。')
  if (file.size === 0) throw new HttpError(400, 'CSVファイルが空です。')
  if (file.size > 5 * 1024 * 1024) throw new HttpError(413, 'CSVファイルは5MB以内にしてください。')
  const parsed = parseCsv(await file.text())
  if (parsed.rows.length > 5000) throw new HttpError(413, 'CSVは5,000行以内にしてください。')
  if (parsed.rows.length === 0) throw new HttpError(400, 'CSVにデータ行がありません。')
  return { ...parsed, resource }
}

function parseCsv(input: string) {
  const text = input.replace(/^\uFEFF/, '')
  const table: string[][] = []
  let row: string[] = []
  let field = ''
  let quoted = false
  for (let index = 0; index < text.length; index += 1) {
    const character = text[index]
    if (character === '"') {
      if (quoted && text[index + 1] === '"') {
        field += '"'
        index += 1
      } else {
        quoted = !quoted
      }
    } else if (character === ',' && !quoted) {
      row.push(field)
      field = ''
    } else if ((character === '\n' || character === '\r') && !quoted) {
      if (character === '\r' && text[index + 1] === '\n') index += 1
      row.push(field)
      if (row.some((value) => value.trim())) table.push(row)
      row = []
      field = ''
    } else {
      field += character
    }
  }
  if (quoted) throw new HttpError(400, 'CSVの引用符が閉じられていません。')
  if (field || row.length) {
    row.push(field)
    if (row.some((value) => value.trim())) table.push(row)
  }
  if (table.length < 2) throw new HttpError(400, 'CSVには見出し行とデータ行が必要です。')
  const headers = table[0].map((header) => header.trim())
  if (headers.some((header) => !header) || new Set(headers).size !== headers.length) throw new HttpError(400, 'CSVの見出し行が不正です。')
  const rows = table.slice(1).map((values) => Object.fromEntries(headers.map((header, index) => [header, (values[index] ?? '').trim()])))
  return { headers, rows }
}

function validateHeaders(resource: ImportResource, headers: string[]) {
  const missing = requiredHeaders[resource].filter((header) => !headers.includes(header))
  return missing.length ? `CSVに必要な列がありません: ${missing.join('、')}` : ''
}

function validateRows(resource: ImportResource, rows: CsvRow[]) {
  return rows.flatMap((row, index) => {
    const messages: string[] = []
    if (resource === 'customers' && !value(row, '顧客名')) messages.push('顧客名がありません。')
    if (resource === 'vehicles' && !value(row, '車名')) messages.push('車名がありません。')
    if ((resource === 'sales' || resource === 'maintenance') && !value(row, '顧客名')) messages.push('顧客名がありません。')
    if ((resource === 'sales' || resource === 'maintenance') && !parseDate(value(row, '発行日'))) messages.push('発行日が不正です。')
    if ((resource === 'sales' || resource === 'maintenance') && !isNonNegativeIntegerText(value(row, '消費税'))) messages.push('消費税は0以上の整数で入力してください。')
    if (resource === 'payments' && !value(row, '請求書ID')) messages.push('請求書IDがありません。')
    return messages.length ? [{ row: index + 2, message: messages.join('') }] : []
  })
}

async function importRows(database: ReturnType<typeof createDatabase>, organizationId: string, resource: ImportResource, rows: CsvRow[]) {
  const validationErrors = [...validateRows(resource, rows), ...findDuplicateImportKeys(resource, rows)]
  if (validationErrors.length) {
    const details = validationErrors.slice(0, 10).map((error) => `${error.row}行目: ${error.message}`).join(' ')
    throw new HttpError(400, `CSVに不正な行があります。変更は反映されていません。${details}`)
  }
  const result = { imported: 0, updated: 0, skipped: 0, errors: [] as Array<{ row: number; message: string }> }
  const writes: BatchItem<'sqlite'>[] = []
  for (let index = 0; index < rows.length; index += 1) {
    try {
      const outcome = resource === 'customers'
        ? await importCustomer(database, organizationId, rows[index], writes)
        : resource === 'vehicles'
          ? await importVehicle(database, organizationId, rows[index], writes)
          : resource === 'sales'
            ? await importSales(database, organizationId, rows[index], writes)
            : resource === 'maintenance'
              ? await importMaintenance(database, organizationId, rows[index], writes)
              : await importPayment(database, organizationId, rows[index], writes)
      result[outcome] += 1
    } catch (error) {
      const message = error instanceof Error ? error.message : '取込できない行です。'
      throw new HttpError(400, `CSV ${index + 2}行目の取込に失敗しました。変更は反映されていません。${message}`)
    }
  }
  if (!writes.length) throw new HttpError(400, 'CSVに反映できる行がありません。')
  try {
    await database.batch(writes as [BatchItem<'sqlite'>, ...BatchItem<'sqlite'>[]])
  } catch (error) {
    const message = error instanceof Error ? error.message : 'データベースへの反映に失敗しました。'
    throw new HttpError(400, `CSVの取込に失敗しました。変更は反映されていません。${message}`)
  }
  return result
}

function findDuplicateImportKeys(resource: ImportResource, rows: CsvRow[]) {
  const seen = new Map<string, number>()
  const errors: Array<{ row: number; message: string }> = []
  rows.forEach((row, index) => {
    const key = resource === 'customers'
      ? value(row, '顧客ID') || value(row, '顧客番号')
      : resource === 'vehicles'
        ? value(row, '車両ID') || (value(row, '登録番号') ? `${value(row, '顧客ID')}:${value(row, '顧客番号')}:${value(row, '登録番号')}` : '')
        : resource === 'payments'
          ? `${value(row, '請求書種別')}:${value(row, '請求書ID')}`
          : value(row, '書類ID') || value(row, '書類番号')
    if (!key || !seen.has(key)) {
      if (key) seen.set(key, index + 2)
      return
    }
    errors.push({ row: index + 2, message: `同じ識別子が${seen.get(key)}行目にもあります。` })
  })
  return errors
}

async function importCustomer(database: ReturnType<typeof createDatabase>, organizationId: string, row: CsvRow, writes: BatchItem<'sqlite'>[]): Promise<ImportOutcome> {
  const name = requiredText(row, '顧客名')
  const customerNumber = value(row, '顧客番号') || `IMP-${crypto.randomUUID().slice(0, 8)}`
  const id = value(row, '顧客ID')
  const existing = id
    ? await database.select().from(customers).where(and(eq(customers.organizationId, organizationId), eq(customers.id, id))).get()
    : await database.select().from(customers).where(and(eq(customers.organizationId, organizationId), eq(customers.customerNumber, customerNumber))).get()
  const data = {
    organizationId,
    customerNumber,
    name,
    nameKana: nullableText(row, 'ふりがな'),
    phone: nullableText(row, '電話番号'),
    email: nullableText(row, 'メールアドレス'),
    postalCode: nullableText(row, '郵便番号'),
    address: nullableText(row, '住所'),
    memo: nullableText(row, 'メモ'),
    updatedAt: new Date().toISOString(),
  }
  if (existing) {
    writes.push(database.update(customers).set(data).where(and(eq(customers.organizationId, organizationId), eq(customers.id, existing.id))))
    return 'updated'
  }
  writes.push(database.insert(customers).values({ id: id || crypto.randomUUID(), ...data }))
  return 'imported'
}

async function importVehicle(database: ReturnType<typeof createDatabase>, organizationId: string, row: CsvRow, writes: BatchItem<'sqlite'>[]): Promise<ImportOutcome> {
  const customer = await findCustomer(database, organizationId, row)
  if (!customer) throw new Error('紐づく顧客が見つかりません。顧客ID、顧客番号、顧客名のいずれかを指定してください。')
  const name = requiredText(row, '車名')
  const id = value(row, '車両ID')
  const registrationNumber = nullableText(row, '登録番号')
  const existing = id
    ? await database.select().from(vehicles).where(and(eq(vehicles.organizationId, organizationId), eq(vehicles.id, id))).get()
    : registrationNumber
      ? await database.select().from(vehicles).where(and(eq(vehicles.organizationId, organizationId), eq(vehicles.customerId, customer.id), eq(vehicles.registrationNumber, registrationNumber))).get()
      : undefined
  const data = {
    organizationId,
    customerId: customer.id,
    maker: nullableText(row, 'メーカー'),
    name,
    model: nullableText(row, '型式'),
    chassisNumber: nullableText(row, '車台番号'),
    registrationNumber,
    modelYear: nullableInteger(row, '年式'),
    inspectionDate: nullableDate(row, '車検満了日'),
    mileage: nullableInteger(row, '走行距離'),
    bodyColor: nullableText(row, '車体色'),
    displacement: nullableInteger(row, '排気量'),
    transmission: nullableText(row, 'ミッション'),
    inspectionRecordAvailable: value(row, '記録簿') === 'あり' || value(row, '記録簿').toLowerCase() === 'true',
    memo: nullableText(row, '備考'),
    updatedAt: new Date().toISOString(),
  }
  if (existing) {
    writes.push(database.update(vehicles).set(data).where(and(eq(vehicles.organizationId, organizationId), eq(vehicles.id, existing.id))))
    return 'updated'
  }
  writes.push(database.insert(vehicles).values({ id: id || crypto.randomUUID(), ...data }))
  return 'imported'
}

async function importSales(database: ReturnType<typeof createDatabase>, organizationId: string, row: CsvRow, writes: BatchItem<'sqlite'>[]): Promise<ImportOutcome> {
  const customer = await findCustomer(database, organizationId, row)
  if (!customer) throw new Error('紐づく顧客が見つかりません。')
  const vehicle = await findVehicle(database, organizationId, row, customer.id)
  const number = requiredText(row, '書類番号')
  const id = value(row, '書類ID')
  const existing = await findSalesDocument(database, organizationId, id, number)
  const items = parseSalesItems(value(row, '明細'), value(row, '明細詳細'))
  const totals = documentTotals(row, items)
  const data = {
    organizationId,
    number,
    type: requiredText(row, '書類種別'),
    status: value(row, 'ステータス') || '下書き',
    customerId: customer.id,
    vehicleId: vehicle?.id ?? null,
    issuedAt: requiredDate(row, '発行日'),
    dueDate: nullableDate(row, '支払期限'),
    taxRate: totals.taxRate,
    subtotal: totals.subtotal,
    tax: totals.tax,
    total: totals.total,
    note: nullableText(row, '備考'),
    detailsJson: parseDetailsJson(value(row, '帳票詳細')),
    updatedAt: new Date().toISOString(),
  }
  const documentId = existing?.id ?? id ?? crypto.randomUUID()
  if (existing) writes.push(database.update(salesDocuments).set(data).where(and(eq(salesDocuments.organizationId, organizationId), eq(salesDocuments.id, existing.id))))
  else writes.push(database.insert(salesDocuments).values({ id: documentId, ...data }))
  replaceSalesItems(database, organizationId, documentId, items, writes)
  return existing ? 'updated' : 'imported'
}

async function importMaintenance(database: ReturnType<typeof createDatabase>, organizationId: string, row: CsvRow, writes: BatchItem<'sqlite'>[]): Promise<ImportOutcome> {
  const customer = await findCustomer(database, organizationId, row)
  if (!customer) throw new Error('紐づく顧客が見つかりません。')
  const vehicle = await findVehicle(database, organizationId, row, customer.id)
  if (!vehicle) throw new Error('紐づく車両が見つかりません。')
  const number = requiredText(row, '書類番号')
  const id = value(row, '書類ID')
  const existing = await findMaintenanceDocument(database, organizationId, id, number)
  const items = parseMaintenanceItems(value(row, '明細'))
  const totals = documentTotals(row, items)
  const data = {
    organizationId,
    number,
    type: value(row, '書類種別') || '整備請求書',
    category: requiredText(row, '入庫区分'),
    status: value(row, 'ステータス') || '下書き',
    customerId: customer.id,
    vehicleId: vehicle.id,
    intakeDate: nullableDate(row, '入庫日'),
    completionDate: nullableDate(row, '出庫予定日'),
    issuedAt: requiredDate(row, '発行日'),
    dueDate: nullableDate(row, '支払期限'),
    taxRate: totals.taxRate,
    subtotal: totals.subtotal,
    tax: totals.tax,
    total: totals.total,
    note: nullableText(row, '備考'),
    updatedAt: new Date().toISOString(),
  }
  const documentId = existing?.id ?? id ?? crypto.randomUUID()
  if (existing) writes.push(database.update(maintenanceDocuments).set(data).where(and(eq(maintenanceDocuments.organizationId, organizationId), eq(maintenanceDocuments.id, existing.id))))
  else writes.push(database.insert(maintenanceDocuments).values({ id: documentId, ...data }))
  replaceMaintenanceItems(database, organizationId, documentId, items, writes)
  return existing ? 'updated' : 'imported'
}

async function importPayment(database: ReturnType<typeof createDatabase>, organizationId: string, row: CsvRow, writes: BatchItem<'sqlite'>[]): Promise<ImportOutcome> {
  const documentId = requiredText(row, '請求書ID')
  const documentType = requiredText(row, '請求書種別')
  const invoice = documentType === '販売請求書'
    ? await database.select().from(salesDocuments).where(and(eq(salesDocuments.organizationId, organizationId), eq(salesDocuments.id, documentId), eq(salesDocuments.type, '請求書'))).get()
    : documentType === '整備請求書'
      ? await database.select().from(maintenanceDocuments).where(and(eq(maintenanceDocuments.organizationId, organizationId), eq(maintenanceDocuments.id, documentId), eq(maintenanceDocuments.type, '整備請求書'))).get()
      : undefined
  if (!invoice) throw new Error('対象の請求書が見つかりません。')
  const existing = await database.select().from(paymentRecords).where(and(eq(paymentRecords.organizationId, organizationId), eq(paymentRecords.documentType, documentType), eq(paymentRecords.documentId, documentId))).get()
  const data = {
    organizationId,
    documentType,
    documentId,
    invoiceAmount: invoice.total,
    paidAmount: Math.min(invoice.total, Math.max(0, integerValue(row, '入金済み'))),
    paymentDate: nullableDate(row, '入金日'),
    method: nullablePaymentMethod(row),
    note: nullableText(row, 'メモ'),
    updatedAt: new Date().toISOString(),
  }
  if (existing) writes.push(database.update(paymentRecords).set(data).where(and(eq(paymentRecords.organizationId, organizationId), eq(paymentRecords.id, existing.id))))
  else writes.push(database.insert(paymentRecords).values({ id: crypto.randomUUID(), ...data }))
  writes.push(database.delete(paymentEntries).where(and(eq(paymentEntries.organizationId, organizationId), eq(paymentEntries.documentType, documentType), eq(paymentEntries.documentId, documentId))))
  if (data.paidAmount > 0 || data.paymentDate || data.method || data.note) {
    const now = new Date().toISOString()
    writes.push(database.insert(paymentEntries).values({ id: crypto.randomUUID(), organizationId, documentType, documentId, amount: data.paidAmount, paymentDate: data.paymentDate, method: data.method, note: data.note ?? '', createdAt: now, updatedAt: now }))
  }
  return existing ? 'updated' : 'imported'
}

async function findCustomer(database: ReturnType<typeof createDatabase>, organizationId: string, row: CsvRow) {
  const id = value(row, '顧客ID')
  if (id) return database.select().from(customers).where(and(eq(customers.organizationId, organizationId), eq(customers.id, id))).get()
  const number = value(row, '顧客番号')
  if (number) return database.select().from(customers).where(and(eq(customers.organizationId, organizationId), eq(customers.customerNumber, number))).get()
  const name = value(row, '顧客名')
  if (!name) return undefined
  const matches = await database.select().from(customers).where(and(eq(customers.organizationId, organizationId), eq(customers.name, name))).orderBy(asc(customers.createdAt)).all()
  if (matches.length > 1) throw new Error('同名の顧客が複数あるため、顧客IDまたは顧客番号を指定してください。')
  return matches[0]
}

async function findVehicle(database: ReturnType<typeof createDatabase>, organizationId: string, row: CsvRow, customerId: string) {
  const id = value(row, '車両ID')
  if (id) return database.select().from(vehicles).where(and(eq(vehicles.organizationId, organizationId), eq(vehicles.id, id), eq(vehicles.customerId, customerId))).get()
  const plate = value(row, '登録番号')
  if (plate) return database.select().from(vehicles).where(and(eq(vehicles.organizationId, organizationId), eq(vehicles.registrationNumber, plate), eq(vehicles.customerId, customerId))).get()
  const name = value(row, '車名')
  if (!name) return undefined
  const matches = await database.select().from(vehicles).where(and(eq(vehicles.organizationId, organizationId), eq(vehicles.name, name), eq(vehicles.customerId, customerId))).orderBy(asc(vehicles.createdAt)).all()
  if (matches.length > 1) throw new Error('同名の車両が複数あるため、車両IDまたは登録番号を指定してください。')
  return matches[0]
}

async function findSalesDocument(database: ReturnType<typeof createDatabase>, organizationId: string, id: string, number: string) {
  if (id) {
    const byId = await database.select().from(salesDocuments).where(and(eq(salesDocuments.organizationId, organizationId), eq(salesDocuments.id, id))).get()
    if (byId) return byId
  }
  return database.select().from(salesDocuments).where(and(eq(salesDocuments.organizationId, organizationId), eq(salesDocuments.number, number))).get()
}

async function findMaintenanceDocument(database: ReturnType<typeof createDatabase>, organizationId: string, id: string, number: string) {
  if (id) {
    const byId = await database.select().from(maintenanceDocuments).where(and(eq(maintenanceDocuments.organizationId, organizationId), eq(maintenanceDocuments.id, id))).get()
    if (byId) return byId
  }
  return database.select().from(maintenanceDocuments).where(and(eq(maintenanceDocuments.organizationId, organizationId), eq(maintenanceDocuments.number, number))).get()
}

function replaceSalesItems(database: ReturnType<typeof createDatabase>, organizationId: string, documentId: string, items: ImportItem[], writes: BatchItem<'sqlite'>[]) {
  writes.push(database.delete(salesDocumentItems).where(and(eq(salesDocumentItems.organizationId, organizationId), eq(salesDocumentItems.documentId, documentId))))
  for (const [index, item] of items.entries()) writes.push(database.insert(salesDocumentItems).values({ id: crypto.randomUUID(), organizationId, documentId, itemType: item.itemType ?? 'その他', description: item.description, quantity: item.quantity, unit: item.unit, unitPrice: item.unitPrice, taxCategory: item.taxCategory ?? '課税', otherAmount: item.otherAmount ?? 0, summary: item.summary ?? '', amount: item.amount, sortOrder: index }))
}

function replaceMaintenanceItems(database: ReturnType<typeof createDatabase>, organizationId: string, documentId: string, items: ImportItem[], writes: BatchItem<'sqlite'>[]) {
  writes.push(database.delete(maintenanceItems).where(and(eq(maintenanceItems.organizationId, organizationId), eq(maintenanceItems.documentId, documentId))))
  for (const [index, item] of items.entries()) writes.push(database.insert(maintenanceItems).values({ id: crypto.randomUUID(), organizationId, documentId, itemType: item.itemType ?? '作業', description: item.description, quantity: item.quantity, unit: item.unit, unitPrice: item.unitPrice, amount: item.amount, sortOrder: index }))
}

function parseSalesItems(text: string, detailText: string): ImportItem[] {
  if (detailText) {
    try {
      const details = JSON.parse(detailText)
      if (Array.isArray(details)) return details.filter((item): item is Record<string, unknown> => Boolean(item) && typeof item === 'object' && !Array.isArray(item)).map((item) => {
        const quantity = numberValue(item.quantity, 1)
        const unitPrice = integerValueObject(item.unitPrice, 0)
        const otherAmount = integerValueObject(item.otherAmount, 0)
        return { itemType: stringObject(item.itemType) || 'その他', description: stringObject(item.description), quantity, unit: stringObject(item.unit) || '式', unitPrice, taxCategory: ['課税', '非課税', '対象外'].includes(stringObject(item.taxCategory)) ? stringObject(item.taxCategory) : '課税', otherAmount, summary: stringObject(item.summary), amount: Math.round(quantity * unitPrice) + otherAmount }
      })
    } catch { /* fall back to the legacy readable column */ }
  }
  return text ? text.split(/\s+\/\s+/).map((item) => parseItem(item)) : []
}

function parseMaintenanceItems(text: string): ImportItem[] {
  return text ? text.split(/\s+\/\s+/).map((item) => { const match = item.match(/^(作業|部品):(.*)$/); const parsed = parseItem(match?.[2] ?? item); return { ...parsed, itemType: match?.[1] ?? '作業' } }) : []
}

function parseItem(text: string): ImportItem {
  const match = text.trim().match(/^(.*?)\s+x(-?[0-9]+(?:\.[0-9]+)?)\s+(\S+)\s+¥?(-?[0-9,]+)$/)
  if (!match) return { itemType: 'その他', description: text.trim().slice(0, 500), quantity: 1, unit: '式', unitPrice: 0, taxCategory: '課税', otherAmount: 0, summary: '', amount: 0 }
  const quantity = Number(match[2]) || 1
  const amount = integerText(match[4])
  return { itemType: 'その他', description: match[1].trim().slice(0, 500), quantity, unit: match[3].trim().slice(0, 20), unitPrice: quantity ? Math.round(amount / quantity) : 0, taxCategory: '課税', otherAmount: 0, summary: '', amount }
}

function parseDetailsJson(value: string) {
  if (!value) return '{}'
  try { return typeof JSON.parse(value) === 'object' ? value : '{}' } catch { return '{}' }
}

function documentTotals(row: CsvRow, items: ImportItem[]) {
  const subtotal = optionalIntegerValue(row, '小計') ?? items.reduce((sum, item) => sum + item.amount, 0)
  const tax = requiredNonNegativeInteger(row, '消費税')
  const total = optionalIntegerValue(row, '合計') ?? subtotal + tax
  return { taxRate: optionalIntegerValue(row, '税率') ?? 10, subtotal, tax, total }
}

function requiredText(row: CsvRow, key: string) {
  const text = value(row, key)
  if (!text) throw new Error(`${key}を入力してください。`)
  return text.slice(0, 500)
}

function requiredDate(row: CsvRow, key: string) {
  const date = parseDate(value(row, key))
  if (!date) throw new Error(`${key}が不正です。`)
  return date
}

function nullableDate(row: CsvRow, key: string) { return parseDate(value(row, key)) }
function nullableText(row: CsvRow, key: string) { const text = value(row, key); return text ? text.slice(0, 500) : null }
function nullablePaymentMethod(row: CsvRow) {
  const method = nullableText(row, '入金方法')
  if (method && !paymentMethods.has(method)) throw new Error('入金方法が不正です。')
  return method
}
function nullableInteger(row: CsvRow, key: string) {
  const text = value(row, key)
  if (!text) return null
  const normalized = text.replace(/[,%¥円\s]/g, '')
  const number = Number(normalized)
  if (!Number.isFinite(number) || number < 0) throw new Error(`${key}は0以上の整数で入力してください。`)
  return Math.round(number)
}
function integerValue(row: CsvRow, key: string) { return integerText(value(row, key)) }
function optionalIntegerValue(row: CsvRow, key: string) { return value(row, key) ? integerValue(row, key) : null }
function integerText(text: string) { const normalized = text.replace(/[,%¥円\s]/g, ''); const number = Number(normalized); return Number.isFinite(number) ? Math.round(number) : 0 }
export function isNonNegativeIntegerText(text: string) {
  if (!text) return false
  const normalized = text.replace(/[,%¥円\s]/g, '')
  return /^\d+$/u.test(normalized) && Number.isSafeInteger(Number(normalized))
}
function requiredNonNegativeInteger(row: CsvRow, key: string) {
  const text = value(row, key)
  if (!isNonNegativeIntegerText(text)) throw new Error(`${key}は0以上の整数で入力してください。`)
  return Number(text.replace(/[,%¥円\s]/g, ''))
}
function parseDate(text: string) { return normalizeCalendarDate(text) }
function value(row: CsvRow, key: string) { return typeof row[key] === 'string' ? row[key].trim() : '' }

type CsvRow = Record<string, string>
type ImportItem = { itemType?: string; description: string; quantity: number; unit: string; unitPrice: number; taxCategory?: string; otherAmount?: number; summary?: string; amount: number }

function stringObject(value: unknown) { return typeof value === 'string' ? value.trim().slice(0, 500) : '' }
function numberValue(value: unknown, fallback: number) { const number = typeof value === 'number' ? value : Number(value); return Number.isFinite(number) && number >= 0 ? number : fallback }
function integerValueObject(value: unknown, fallback: number) { const number = typeof value === 'number' ? value : Number(value); return Number.isFinite(number) ? Math.round(number) : fallback }
