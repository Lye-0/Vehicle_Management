import { and, asc, eq } from 'drizzle-orm'
import { customers, maintenanceDocuments, maintenanceItems, paymentRecords, salesDocumentItems, salesDocuments, vehicles } from '@vehicle-management/database'
import { requireAdminOrganizationContext } from '../auth/organization'
import { UnauthorizedError } from '../auth/firebase'
import { createDatabase } from '../db/client'
import { HttpError, corsHeaders, jsonResponse } from '../http'

const importResources = ['customers', 'vehicles', 'sales', 'maintenance', 'payments'] as const
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
  sales: ['書類番号', '書類種別', '顧客名', '発行日'],
  maintenance: ['書類番号', '書類種別', '入庫区分', '顧客名', '発行日'],
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
    if (resource === 'payments' && !value(row, '請求書ID')) messages.push('請求書IDがありません。')
    return messages.length ? [{ row: index + 2, message: messages.join('') }] : []
  })
}

async function importRows(database: ReturnType<typeof createDatabase>, organizationId: string, resource: ImportResource, rows: CsvRow[]) {
  const result = { imported: 0, updated: 0, skipped: 0, errors: [] as Array<{ row: number; message: string }> }
  for (let index = 0; index < rows.length; index += 1) {
    try {
      const outcome = resource === 'customers'
        ? await importCustomer(database, organizationId, rows[index])
        : resource === 'vehicles'
          ? await importVehicle(database, organizationId, rows[index])
          : resource === 'sales'
            ? await importSales(database, organizationId, rows[index])
            : resource === 'maintenance'
              ? await importMaintenance(database, organizationId, rows[index])
              : await importPayment(database, organizationId, rows[index])
      result[outcome] += 1
    } catch (error) {
      if (result.errors.length < 100) result.errors.push({ row: index + 2, message: error instanceof Error ? error.message : '取込できない行です。' })
      result.skipped += 1
    }
  }
  return result
}

async function importCustomer(database: ReturnType<typeof createDatabase>, organizationId: string, row: CsvRow): Promise<ImportOutcome> {
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
    await database.update(customers).set(data).where(and(eq(customers.organizationId, organizationId), eq(customers.id, existing.id))).run()
    return 'updated'
  }
  await database.insert(customers).values({ id: id || crypto.randomUUID(), ...data }).run()
  return 'imported'
}

async function importVehicle(database: ReturnType<typeof createDatabase>, organizationId: string, row: CsvRow): Promise<ImportOutcome> {
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
    await database.update(vehicles).set(data).where(and(eq(vehicles.organizationId, organizationId), eq(vehicles.id, existing.id))).run()
    return 'updated'
  }
  await database.insert(vehicles).values({ id: id || crypto.randomUUID(), ...data }).run()
  return 'imported'
}

async function importSales(database: ReturnType<typeof createDatabase>, organizationId: string, row: CsvRow): Promise<ImportOutcome> {
  const customer = await findCustomer(database, organizationId, row)
  if (!customer) throw new Error('紐づく顧客が見つかりません。')
  const vehicle = await findVehicle(database, organizationId, row, customer.id)
  const number = requiredText(row, '書類番号')
  const id = value(row, '書類ID')
  const existing = await findSalesDocument(database, organizationId, id, number)
  const items = parseSalesItems(value(row, '明細'))
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
    updatedAt: new Date().toISOString(),
  }
  const documentId = existing?.id ?? id ?? crypto.randomUUID()
  if (existing) await database.update(salesDocuments).set(data).where(and(eq(salesDocuments.organizationId, organizationId), eq(salesDocuments.id, existing.id))).run()
  else await database.insert(salesDocuments).values({ id: documentId, ...data }).run()
  await replaceSalesItems(database, organizationId, documentId, items)
  return existing ? 'updated' : 'imported'
}

async function importMaintenance(database: ReturnType<typeof createDatabase>, organizationId: string, row: CsvRow): Promise<ImportOutcome> {
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
  if (existing) await database.update(maintenanceDocuments).set(data).where(and(eq(maintenanceDocuments.organizationId, organizationId), eq(maintenanceDocuments.id, existing.id))).run()
  else await database.insert(maintenanceDocuments).values({ id: documentId, ...data }).run()
  await replaceMaintenanceItems(database, organizationId, documentId, items)
  return existing ? 'updated' : 'imported'
}

async function importPayment(database: ReturnType<typeof createDatabase>, organizationId: string, row: CsvRow): Promise<ImportOutcome> {
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
    method: nullableText(row, '入金方法'),
    note: nullableText(row, 'メモ'),
    updatedAt: new Date().toISOString(),
  }
  if (existing) await database.update(paymentRecords).set(data).where(and(eq(paymentRecords.organizationId, organizationId), eq(paymentRecords.id, existing.id))).run()
  else await database.insert(paymentRecords).values({ id: crypto.randomUUID(), ...data }).run()
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

async function replaceSalesItems(database: ReturnType<typeof createDatabase>, organizationId: string, documentId: string, items: ImportItem[]) {
  await database.delete(salesDocumentItems).where(and(eq(salesDocumentItems.organizationId, organizationId), eq(salesDocumentItems.documentId, documentId))).run()
  for (const [index, item] of items.entries()) await database.insert(salesDocumentItems).values({ id: crypto.randomUUID(), organizationId, documentId, description: item.description, quantity: item.quantity, unit: item.unit, unitPrice: item.unitPrice, amount: item.amount, sortOrder: index }).run()
}

async function replaceMaintenanceItems(database: ReturnType<typeof createDatabase>, organizationId: string, documentId: string, items: ImportItem[]) {
  await database.delete(maintenanceItems).where(and(eq(maintenanceItems.organizationId, organizationId), eq(maintenanceItems.documentId, documentId))).run()
  for (const [index, item] of items.entries()) await database.insert(maintenanceItems).values({ id: crypto.randomUUID(), organizationId, documentId, itemType: item.itemType ?? '作業', description: item.description, quantity: item.quantity, unit: item.unit, unitPrice: item.unitPrice, amount: item.amount, sortOrder: index }).run()
}

function parseSalesItems(text: string): ImportItem[] {
  return text ? text.split(/\s+\/\s+/).map((item) => parseItem(item)) : []
}

function parseMaintenanceItems(text: string): ImportItem[] {
  return text ? text.split(/\s+\/\s+/).map((item) => { const match = item.match(/^(作業|部品):(.*)$/); const parsed = parseItem(match?.[2] ?? item); return { ...parsed, itemType: match?.[1] ?? '作業' } }) : []
}

function parseItem(text: string): ImportItem {
  const match = text.trim().match(/^(.*?)\s+x(-?[0-9]+(?:\.[0-9]+)?)\s+(\S+)\s+¥?(-?[0-9,]+)$/)
  if (!match) return { description: text.trim().slice(0, 500), quantity: 1, unit: '式', unitPrice: 0, amount: 0 }
  const quantity = Number(match[2]) || 1
  const amount = integerText(match[4])
  return { description: match[1].trim().slice(0, 500), quantity, unit: match[3].trim().slice(0, 20), unitPrice: quantity ? Math.round(amount / quantity) : 0, amount }
}

function documentTotals(row: CsvRow, items: ImportItem[]) {
  const subtotal = integerValue(row, '小計') || items.reduce((sum, item) => sum + item.amount, 0)
  const tax = integerValue(row, '消費税')
  const total = integerValue(row, '合計') || subtotal + tax
  return { taxRate: integerValue(row, '税率') || 10, subtotal, tax, total }
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
function nullableInteger(row: CsvRow, key: string) { const number = integerValue(row, key); return number || null }
function integerValue(row: CsvRow, key: string) { return integerText(value(row, key)) }
function integerText(text: string) { const normalized = text.replace(/[,%¥円\s]/g, ''); const number = Number(normalized); return Number.isFinite(number) ? Math.round(number) : 0 }
function parseDate(text: string) { return /^\d{4}[-/]\d{1,2}[-/]\d{1,2}$/.test(text) ? text.replaceAll('/', '-').replace(/-(\d)(?=-|$)/g, '-0$1') : null }
function value(row: CsvRow, key: string) { return typeof row[key] === 'string' ? row[key].trim() : '' }

type CsvRow = Record<string, string>
type ImportItem = { itemType?: string; description: string; quantity: number; unit: string; unitPrice: number; amount: number }
