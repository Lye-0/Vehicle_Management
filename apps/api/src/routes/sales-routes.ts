import { and, asc, desc, eq } from 'drizzle-orm'
import { customers, salesDocumentItems, salesDocuments, vehicleFiles, vehicles } from '@vehicle-management/database'
import { UnauthorizedError } from '../auth/firebase'
import { requireOrganizationContext } from '../auth/organization'
import { createDatabase } from '../db/client'
import { nextDocumentNumber } from '../document-number'
import { HttpError, jsonResponse, readJson } from '../http'

const salesDocumentTypes = new Set(['見積書', '請求書'])
const salesStatuses = new Set(['下書き', '入金待ち', '完了', 'アーカイブ済み'])
const salesItemTypes = new Set(['車両本体価格', '付属品・特別仕様', '取付工賃', '車両販売工賃', '値引き', '法定費用', '手続代行費用', '実費・預託金', '自動車税', '重量税', '自賠責保険', '環境性能割', '車庫証明費用', '登録費用', '納車費用', '下取車', 'リサイクル料金', '頭金', '残金', 'その他'])
const salesTaxCategories = new Set(['課税', '非課税', '対象外'])
const salesItemInsertBatchSize = 7

export async function handleSalesRoutes(request: Request, env: Env): Promise<Response | null> {
  const pathname = new URL(request.url).pathname.replace(/\/$/, '') || '/'
  const isCollection = pathname === '/api/sales-documents'
  const restoreMatch = pathname.match(/^\/api\/sales-documents\/([^/]+)\/restore$/)
  const documentMatch = pathname.match(/^\/api\/sales-documents\/([^/]+)$/)
  if (!isCollection && !documentMatch && !restoreMatch) return null

  try {
    const database = createDatabase(env.DB)
    const context = await requireOrganizationContext(request, env, database)
    const organizationId = context.organization.organizationId

    if (isCollection) {
      if (request.method === 'GET') return await listSalesDocuments(request, env, database, organizationId)
      if (request.method === 'POST') return await createSalesDocument(request, env, database, organizationId)
      throw new HttpError(405, 'この操作には対応していません。')
    }

    if (restoreMatch) {
      if (request.method !== 'POST') throw new HttpError(405, 'この操作には対応していません。')
      return await restoreSalesDocument(env, database, restoreMatch[1], organizationId)
    }

    if (!documentMatch) throw new HttpError(404, '販売書類のAPIが見つかりません。')
    if (request.method === 'PATCH') return await updateSalesDocument(request, env, database, documentMatch[1], organizationId)
    if (request.method === 'DELETE') return await archiveSalesDocument(env, database, documentMatch[1], organizationId)
    throw new HttpError(405, 'この操作には対応していません。')
  } catch (error) {
    if (error instanceof UnauthorizedError) return jsonResponse({ error: error.message }, 401, env)
    if (error instanceof HttpError) return jsonResponse({ error: error.message }, error.status, env)
    console.error(error)
    return jsonResponse({ error: '販売書類の処理に失敗しました。' }, 500, env)
  }
}

async function listSalesDocuments(request: Request, env: Env, database: ReturnType<typeof createDatabase>, organizationId: string) {
  const url = new URL(request.url)
  const query = url.searchParams.get('q')?.trim().toLocaleLowerCase() ?? ''
  const type = url.searchParams.get('type')?.trim() ?? ''
  const documents = await loadSalesDocuments(database, organizationId, url.searchParams.get('includeArchived') === 'true')
  const filtered = documents.filter((document) => {
    const matchesType = !type || type === 'すべて' || document.type === type
    const searchable = `${document.number} ${document.customerName} ${document.vehicle} ${document.plate}`.toLocaleLowerCase()
    return matchesType && (!query || searchable.includes(query))
  })
  return jsonResponse({ documents: filtered }, 200, env)
}

async function createSalesDocument(request: Request, env: Env, database: ReturnType<typeof createDatabase>, organizationId: string) {
  const body = await readJson(request)
  const input = await parseSalesDocumentInput(body, database, organizationId)
  const id = crypto.randomUUID()
  const number = await nextDocumentNumber(env.DB, organizationId, 'S')
  await ensureSalesDocumentNumberAvailable(database, number, organizationId)
  const totals = calculateSalesTotals(input.items, input.taxRate, input.rounding, input.details)

  await database.insert(salesDocuments).values({
    id,
    organizationId,
    number,
    type: input.type,
    status: input.status,
    customerId: input.customerId,
    vehicleId: input.vehicleId,
    issuedAt: input.issuedAt,
    dueDate: input.dueDate,
    taxRate: input.taxRate,
    taxRounding: input.rounding,
    subtotal: totals.subtotal,
    tax: totals.tax,
    total: totals.total,
    note: input.note,
    detailsJson: JSON.stringify(input.details),
  }).run()
  await insertSalesItems(database, id, input.items, organizationId)

  return jsonResponse({ document: await findSalesDocument(database, id, organizationId) }, 201, env)
}

async function updateSalesDocument(request: Request, env: Env, database: ReturnType<typeof createDatabase>, documentId: string, organizationId: string) {
  const current = await database.select().from(salesDocuments).where(and(eq(salesDocuments.id, documentId), eq(salesDocuments.organizationId, organizationId))).get()
  if (!current) throw new HttpError(404, '販売書類が見つかりません。')

  const body = await readJson(request)
  const input = await parseSalesDocumentInput({
    ...body,
    type: body.type ?? current.type,
    status: body.status ?? current.status,
    customerId: body.customerId ?? current.customerId,
    vehicleId: body.vehicleId === undefined ? current.vehicleId : body.vehicleId,
    issuedAt: body.issuedAt ?? current.issuedAt,
    dueDate: body.dueDate === undefined ? current.dueDate : body.dueDate,
    number: current.number,
    taxRate: body.taxRate ?? current.taxRate,
    rounding: body.rounding ?? current.taxRounding,
    note: body.note === undefined ? current.note : body.note,
    details: body.details === undefined ? parseSalesDetails(current.detailsJson) : body.details,
    items: body.items === undefined ? await loadSalesItems(database, documentId, organizationId) : body.items,
  }, database, organizationId)
  const totals = calculateSalesTotals(input.items, input.taxRate, input.rounding, input.details)

  await database.update(salesDocuments).set({
    type: input.type,
    number: current.number,
    status: input.status,
    customerId: input.customerId,
    vehicleId: input.vehicleId,
    issuedAt: input.issuedAt,
    dueDate: input.dueDate,
    taxRate: input.taxRate,
    taxRounding: input.rounding,
    subtotal: totals.subtotal,
    tax: totals.tax,
    total: totals.total,
    note: input.note,
    detailsJson: JSON.stringify(input.details),
    updatedAt: new Date().toISOString(),
  }).where(and(eq(salesDocuments.id, documentId), eq(salesDocuments.organizationId, organizationId))).run()

  await database.delete(salesDocumentItems).where(and(eq(salesDocumentItems.documentId, documentId), eq(salesDocumentItems.organizationId, organizationId))).run()
  await insertSalesItems(database, documentId, input.items, organizationId)

  return jsonResponse({ document: await findSalesDocument(database, documentId, organizationId) }, 200, env)
}

async function loadSalesDocuments(database: ReturnType<typeof createDatabase>, organizationId: string, includeArchived = false) {
  const [documentRows, itemRows, customerRows, vehicleRows] = await Promise.all([
    database.select().from(salesDocuments).where(eq(salesDocuments.organizationId, organizationId)).orderBy(desc(salesDocuments.issuedAt), desc(salesDocuments.number)).all(),
    database.select().from(salesDocumentItems).where(eq(salesDocumentItems.organizationId, organizationId)).orderBy(asc(salesDocumentItems.sortOrder)).all(),
    database.select().from(customers).where(eq(customers.organizationId, organizationId)).all(),
    database.select().from(vehicles).where(eq(vehicles.organizationId, organizationId)).all(),
  ])
  const itemsByDocument = groupBy(itemRows, (item) => item.documentId)
  const customersById = new Map(customerRows.map((customer) => [customer.id, customer]))
  const vehiclesById = new Map(vehicleRows.map((vehicle) => [vehicle.id, vehicle]))

  return documentRows.filter((document) => includeArchived || !document.archivedAt).map((document) => serializeSalesDocument(
    document,
    customersById.get(document.customerId),
    document.vehicleId ? vehiclesById.get(document.vehicleId) : undefined,
    itemsByDocument.get(document.id) ?? [],
  ))
}

async function findSalesDocument(database: ReturnType<typeof createDatabase>, documentId: string, organizationId: string) {
  const documents = await loadSalesDocuments(database, organizationId)
  return documents.find((document) => document.id === documentId) ?? null
}

async function loadSalesItems(database: ReturnType<typeof createDatabase>, documentId: string, organizationId: string) {
  return database.select().from(salesDocumentItems).where(and(eq(salesDocumentItems.documentId, documentId), eq(salesDocumentItems.organizationId, organizationId))).orderBy(asc(salesDocumentItems.sortOrder)).all()
}

async function insertSalesItems(database: ReturnType<typeof createDatabase>, documentId: string, items: SalesItemInput[], organizationId: string) {
  if (!items.length) return
  for (let start = 0; start < items.length; start += salesItemInsertBatchSize) {
    const batch = items.slice(start, start + salesItemInsertBatchSize)
    await database.insert(salesDocumentItems).values(batch.map((item, index) => ({
      id: crypto.randomUUID(),
      organizationId,
      documentId,
      itemType: item.itemType,
      description: item.description,
      quantity: item.quantity,
      unit: item.unit,
      unitPrice: item.unitPrice,
      taxCategory: item.taxCategory,
      otherAmount: item.otherAmount,
      summary: item.summary,
      amount: item.amount,
      sortOrder: start + index,
    }))).run()
  }
}

async function parseSalesDocumentInput(body: Record<string, unknown>, database: ReturnType<typeof createDatabase>, organizationId: string): Promise<SalesDocumentInput> {
  const type = stringValue(body, 'type')
  if (!salesDocumentTypes.has(type)) throw new HttpError(400, '書類種別が不正です。')

  const status = normalizeSalesStatus(stringValue(body, 'status') || '下書き')
  if (!salesStatuses.has(status)) throw new HttpError(400, '書類ステータスが不正です。')

  const customerId = stringValue(body, 'customerId')
  const customer = customerId ? await database.select({ id: customers.id }).from(customers).where(and(eq(customers.id, customerId), eq(customers.organizationId, organizationId))).get() : null
  if (!customer) throw new HttpError(400, '顧客を選択してください。')

  const vehicleId = nullableString(body, 'vehicleId')
  if (vehicleId) {
    const vehicle = await database.select({ id: vehicles.id, customerId: vehicles.customerId }).from(vehicles).where(and(eq(vehicles.id, vehicleId), eq(vehicles.organizationId, organizationId))).get()
    if (!vehicle || vehicle.customerId !== customerId) throw new HttpError(400, '選択した車両が顧客と一致しません。')
  }

  const taxRate = parseTaxRate(body.taxRate)
  const rounding = body.rounding === '四捨五入' ? '四捨五入' : '切り捨て'
  const issuedAt = dateValue(body.issuedAt) || today()
  const dueDate = nullableDate(body.dueDate)
  const number = nullableString(body, 'number')
  const details = parseSalesDetails(body.details)
  if (details.selectedImageAttachmentId) {
    if (!vehicleId) throw new HttpError(400, '画像を選択するには対象車両が必要です。')
    const image = await database.select({ id: vehicleFiles.id }).from(vehicleFiles).where(and(eq(vehicleFiles.id, details.selectedImageAttachmentId), eq(vehicleFiles.vehicleId, vehicleId), eq(vehicleFiles.organizationId, organizationId), eq(vehicleFiles.fileKind, 'image'))).get()
    if (!image) throw new HttpError(400, '選択した画像が対象車両に紐づいていません。')
  }
  const items = parseItems(body.items)
  return { number, type, status, customerId, vehicleId, issuedAt, dueDate, taxRate, rounding, note: nullableString(body, 'note'), details, items }
}

function serializeSalesDocument(
  document: typeof salesDocuments.$inferSelect,
  customer: typeof customers.$inferSelect | undefined,
  vehicle: typeof vehicles.$inferSelect | undefined,
  items: Array<typeof salesDocumentItems.$inferSelect>,
) {
  return {
    id: document.id,
    number: document.number,
    type: document.type,
    status: normalizeSalesStatus(document.status),
    customerId: document.customerId,
    customerName: customer?.name ?? '',
    phone: customer?.phone ?? '',
    customerDetails: {
      name: customer?.name ?? '',
      kana: customer?.nameKana ?? '',
      phone: customer?.phone ?? '',
      postalCode: customer?.postalCode ?? '',
      address: customer?.address ?? '',
      birthDate: '',
      employer: '',
      contactPhone: '',
    },
    vehicleId: document.vehicleId,
    vehicle: vehicle ? [vehicle.maker, vehicle.name].filter(Boolean).join(' ') : '',
    plate: vehicle?.registrationNumber ?? '',
    vehicleDetails: vehicle ? {
      maker: vehicle.maker ?? '',
      name: vehicle.name,
      modelType: vehicle.model ?? '',
      plate: vehicle.registrationNumber ?? '',
      vin: vehicle.chassisNumber ?? '',
      year: vehicle.modelYear ? `${vehicle.modelYear}年` : '',
      inspectionDate: vehicle.inspectionDate ?? '',
      mileage: vehicle.mileage === null ? '' : `${vehicle.mileage.toLocaleString('ja-JP')} km`,
      color: vehicle.bodyColor ?? '',
      displacement: vehicle.displacement === null ? '' : `${vehicle.displacement.toLocaleString('ja-JP')} cc`,
      transmission: vehicle.transmission ?? '',
      inspectionRecordAvailable: Boolean(vehicle.inspectionRecordAvailable),
    } : null,
    details: parseSalesDetails(document.detailsJson),
    issuedAt: document.issuedAt,
    dueDate: document.dueDate,
    taxRate: document.taxRate,
    taxRounding: normalizeTaxRounding(document.taxRounding),
    subtotal: document.subtotal,
    tax: document.tax,
    total: document.total,
    note: document.note ?? '',
    archivedAt: document.archivedAt,
    items: items.map((item) => ({
      id: item.id,
      itemType: item.itemType,
      description: item.description,
      quantity: item.quantity,
      unit: item.unit,
      unitPrice: item.unitPrice,
      taxCategory: item.taxCategory,
      otherAmount: item.otherAmount,
      summary: item.summary,
      amount: item.amount,
    })),
  }
}

function normalizeSalesStatus(status: string) {
  return status === '発行済み' ? '完了' : status
}

function normalizeTaxRounding(rounding: string | null | undefined): '切り捨て' | '四捨五入' {
  return rounding === '四捨五入' ? '四捨五入' : '切り捨て'
}

export function calculateSalesTotals(items: SalesItemInput[], taxRate: number, rounding: '切り捨て' | '四捨五入', details: SalesDocumentDetails) {
  const buckets = { vehicleBase: 0, discount: 0, accessories: 0, vehicleSideLabor: 0, legalNonTaxable: 0, taxableFees: 0, nonTaxableFees: 0, outOfScope: 0, tradeIn: 0, payments: 0, recycle: 0 }
  let hasRecycleLine = false
  items.forEach((item) => {
    const bucket = classifySalesItem(item)
    buckets[bucket] += item.amount
    if (bucket === 'recycle') hasRecycleLine = true
  })
  const recycleFee = hasRecycleLine ? buckets.recycle : Math.max(0, details.recycleFee)
  const vehicleSalesTotal = buckets.vehicleBase + buckets.discount + buckets.accessories + buckets.vehicleSideLabor
  const taxableSubtotal = vehicleSalesTotal + buckets.taxableFees
  const nonTaxableSubtotal = buckets.legalNonTaxable + buckets.nonTaxableFees + recycleFee
  const subtotal = taxableSubtotal + nonTaxableSubtotal + buckets.outOfScope
  const taxValue = Math.max(0, taxableSubtotal) * taxRate / 100
  const tax = rounding === '四捨五入' ? Math.round(taxValue) : Math.floor(taxValue)
  return { subtotal, tax, total: subtotal + tax }
}

type SalesItemBucket = keyof ReturnType<typeof emptySalesCalculationBuckets>

function emptySalesCalculationBuckets() {
  return { vehicleBase: 0, discount: 0, accessories: 0, vehicleSideLabor: 0, legalNonTaxable: 0, taxableFees: 0, nonTaxableFees: 0, outOfScope: 0, tradeIn: 0, payments: 0, recycle: 0 }
}

function classifySalesItem(item: SalesItemInput): SalesItemBucket {
  const label = `${item.itemType} ${item.description}`
  if (item.itemType === '法定費用') return 'legalNonTaxable'
  if (item.itemType === '手続代行費用') return 'taxableFees'
  if (item.itemType === '実費・預託金') return label.includes('リサイクル') ? 'recycle' : 'nonTaxableFees'
  if (item.itemType === '車両本体価格' || label.includes('車両本体価格')) return 'vehicleBase'
  if (item.itemType === '値引き' || label.includes('値引')) return 'discount'
  if (item.itemType === '付属品・特別仕様' || item.itemType === '取付工賃' || label.includes('付属品') || label.includes('特別仕様')) return 'accessories'
  if (item.itemType === '車両販売工賃' || label.includes('車両販売側工賃')) return 'vehicleSideLabor'
  if (item.itemType === '下取車') return 'tradeIn'
  if (item.itemType === '頭金' || item.itemType === '残金' || label.includes('頭金') || label.includes('残金')) return 'payments'
  if (label.includes('リサイクル')) return 'recycle'
  if (['自動車税', '取得税', '環境性能割', '重量税', '自賠責', '印紙代'].some((keyword) => label.includes(keyword))) return 'legalNonTaxable'
  if (['証紙', '預託金'].some((keyword) => label.includes(keyword))) return 'nonTaxableFees'
  if (['車庫証明', '登録費用', '登録代行', '登録手続', '検査', '納車', '手数料', '査定料'].some((keyword) => label.includes(keyword))) return 'taxableFees'
  if (label.includes('下取')) return 'tradeIn'
  if (item.taxCategory === '非課税') return 'nonTaxableFees'
  if (item.taxCategory === '対象外') return 'outOfScope'
  return 'taxableFees'
}

function parseItems(value: unknown): SalesItemInput[] {
  if (!Array.isArray(value)) return []
  return value.filter((item): item is Record<string, unknown> => Boolean(item) && typeof item === 'object' && !Array.isArray(item)).map((item) => {
    const quantity = nonNegativeNumber(item.quantity, 1)
    const unitPrice = integerNumber(item.unitPrice, 0)
    const otherAmount = integerNumber(item.otherAmount, 0)
    const itemTypeValue = stringValue(item, 'itemType')
    const taxCategoryValue = stringValue(item, 'taxCategory')
    return {
      itemType: salesItemTypes.has(itemTypeValue) ? itemTypeValue : 'その他',
      description: stringValue(item, 'description'),
      quantity,
      unit: stringValue(item, 'unit') || '式',
      unitPrice,
      taxCategory: salesTaxCategories.has(taxCategoryValue) ? taxCategoryValue : '課税',
      otherAmount,
      summary: stringValue(item, 'summary'),
      amount: Math.round(quantity * unitPrice) + otherAmount,
    }
  })
}

export function parseSalesDetails(value: unknown): SalesDocumentDetails {
  const record = parseRecord(value)
  const tradeIn = parseRecord(record.tradeIn)
  const credit = parseRecord(record.credit)
  const requiredDocuments = parseRecord(record.requiredDocuments)
  const customerOverride = isRecord(record.customerOverride) ? record.customerOverride : null
  const vehicleOverride = isRecord(record.vehicleOverride) ? record.vehicleOverride : null
  return {
    salesCategory: limitedString(record.salesCategory, '中古車', 100),
    staffName: limitedString(record.staffName, '', 100),
    customerHonorific: limitedString(record.customerHonorific, '様', 20),
    customerBirthDate: dateValue(record.customerBirthDate),
    customerEmployer: limitedString(record.customerEmployer, '', 200),
    customerContactPhone: limitedString(record.customerContactPhone, '', 50),
    selectedImageAttachmentId: limitedString(record.selectedImageAttachmentId, '', 128),
    customerOverride: customerOverride ? {
      name: limitedString(customerOverride.name, '', 200),
      kana: limitedString(customerOverride.kana, '', 200),
      phone: limitedString(customerOverride.phone, '', 50),
      postalCode: limitedString(customerOverride.postalCode, '', 20),
      address: limitedString(customerOverride.address, '', 500),
    } : null,
    vehicleOverride: vehicleOverride ? {
      maker: limitedString(vehicleOverride.maker, '', 100),
      name: limitedString(vehicleOverride.name, '', 200),
      modelType: limitedString(vehicleOverride.modelType, '', 100),
      plate: limitedString(vehicleOverride.plate, '', 100),
      vin: limitedString(vehicleOverride.vin, '', 100),
      year: limitedString(vehicleOverride.year, '', 50),
      inspectionDate: dateValue(vehicleOverride.inspectionDate),
      mileage: limitedString(vehicleOverride.mileage, '', 50),
      color: limitedString(vehicleOverride.color, '', 100),
      displacement: limitedString(vehicleOverride.displacement, '', 50),
      transmission: limitedString(vehicleOverride.transmission, '', 100),
      inspectionRecordAvailable: booleanValue(vehicleOverride.inspectionRecordAvailable),
    } : null,
    tradeIn: {
      name: limitedString(tradeIn.name, '', 200),
      modelYear: limitedString(tradeIn.modelYear, '', 50),
      inspectionDate: dateValue(tradeIn.inspectionDate),
      mileage: limitedString(tradeIn.mileage, '', 50),
      color: limitedString(tradeIn.color, '', 100),
    },
    recycleFee: nonNegativeInteger(record.recycleFee, 0),
    downPayment: nonNegativeInteger(record.downPayment, 0),
    remainingPayment: nonNegativeInteger(record.remainingPayment, 0),
    credit: {
      enabled: record.creditEnabled === true || credit.enabled === true,
      paymentCount: limitedString(credit.paymentCount, '', 50),
      fee: integerNumber(credit.fee, 0),
      monthlyPayment: nonNegativeInteger(credit.monthlyPayment, 0),
      initialPayment: nonNegativeInteger(credit.initialPayment, 0),
      bonusMonths: limitedString(credit.bonusMonths, '', 50),
      bonusPayment: nonNegativeInteger(credit.bonusPayment, 0),
    },
    requiredDocuments: {
      sealCertificate: booleanValue(requiredDocuments.sealCertificate),
      selfDeclaration: booleanValue(requiredDocuments.selfDeclaration) || booleanValue(requiredDocuments.warrantyCertificate),
      residentCard: booleanValue(requiredDocuments.residentCard),
      powerOfAttorney: booleanValue(requiredDocuments.powerOfAttorney),
      lightVehicleCertificate: booleanValue(requiredDocuments.lightVehicleCertificate),
      transferCertificate: booleanValue(requiredDocuments.transferCertificate),
      taxPaymentCertificate: booleanValue(requiredDocuments.taxPaymentCertificate),
      guarantorSealCertificate: booleanValue(requiredDocuments.guarantorSealCertificate),
      warrantyCertificate: booleanValue(requiredDocuments.warrantyCertificate),
      other: limitedString(requiredDocuments.other, '', 200),
    },
  }
}

function parseRecord(value: unknown): Record<string, unknown> {
  if (typeof value === 'string') {
    try { return parseRecord(JSON.parse(value)) } catch { return {} }
  }
  return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : {}
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return Boolean(value) && typeof value === 'object' && !Array.isArray(value)
}

function limitedString(value: unknown, fallback: string, maxLength: number) {
  return typeof value === 'string' ? value.trim().slice(0, maxLength) : fallback
}

function nonNegativeInteger(value: unknown, fallback: number) {
  return Math.max(0, integerNumber(value, fallback))
}

function booleanValue(value: unknown) {
  return value === true || value === 'true' || value === 1
}

async function ensureSalesDocumentNumberAvailable(database: ReturnType<typeof createDatabase>, number: string, organizationId: string, exceptId?: string) {
  const duplicate = await database.select({ id: salesDocuments.id }).from(salesDocuments).where(and(eq(salesDocuments.organizationId, organizationId), eq(salesDocuments.number, number))).get()
  if (duplicate && duplicate.id !== exceptId) throw new HttpError(409, '同じ書類番号の販売書類がすでに存在します。')
}

async function archiveSalesDocument(env: Env, database: ReturnType<typeof createDatabase>, documentId: string, organizationId: string) {
  const current = await database.select({ id: salesDocuments.id }).from(salesDocuments).where(and(eq(salesDocuments.id, documentId), eq(salesDocuments.organizationId, organizationId))).get()
  if (!current) throw new HttpError(404, '販売書類が見つかりません。')
  await database.update(salesDocuments).set({ status: 'アーカイブ済み', archivedAt: new Date().toISOString(), updatedAt: new Date().toISOString() }).where(and(eq(salesDocuments.id, documentId), eq(salesDocuments.organizationId, organizationId))).run()
  return jsonResponse({ archived: true }, 200, env)
}

async function restoreSalesDocument(env: Env, database: ReturnType<typeof createDatabase>, documentId: string, organizationId: string) {
  const current = await database.select().from(salesDocuments).where(and(eq(salesDocuments.id, documentId), eq(salesDocuments.organizationId, organizationId))).get()
  if (!current) throw new HttpError(404, '販売書類が見つかりません。')
  await database.update(salesDocuments).set({ status: '下書き', archivedAt: null, updatedAt: new Date().toISOString() }).where(and(eq(salesDocuments.id, documentId), eq(salesDocuments.organizationId, organizationId))).run()
  return jsonResponse({ restored: true }, 200, env)
}

function groupBy<T>(items: T[], getKey: (item: T) => string) {
  const grouped = new Map<string, T[]>()
  for (const item of items) {
    const key = getKey(item)
    const current = grouped.get(key) ?? []
    current.push(item)
    grouped.set(key, current)
  }
  return grouped
}

function stringValue(body: Record<string, unknown>, key: string) {
  return typeof body[key] === 'string' ? body[key].trim() : ''
}

function nullableString(body: Record<string, unknown>, key: string) {
  const value = stringValue(body, key)
  return value || null
}

function dateValue(value: unknown) {
  return typeof value === 'string' && /^\d{4}[-/]\d{2}[-/]\d{2}$/.test(value.trim()) ? value.trim().replaceAll('/', '-') : ''
}

function nullableDate(value: unknown) {
  return dateValue(value) || null
}

function parseTaxRate(value: unknown) {
  const number = typeof value === 'number' ? value : Number(value)
  const normalized = number > 0 && number < 1 ? number * 100 : number
  if (!Number.isFinite(normalized) || normalized < 0 || normalized > 100) return 10
  return Math.round(normalized)
}

function nonNegativeNumber(value: unknown, fallback: number) {
  const number = typeof value === 'number' ? value : Number(value)
  return Number.isFinite(number) && number >= 0 ? number : fallback
}

function integerNumber(value: unknown, fallback: number) {
  const number = typeof value === 'number' ? value : Number(value)
  return Number.isFinite(number) ? Math.round(number) : fallback
}

function today() {
  return new Date().toISOString().slice(0, 10)
}

type SalesItemInput = {
  itemType: string
  description: string
  quantity: number
  unit: string
  unitPrice: number
  taxCategory: string
  otherAmount: number
  summary: string
  amount: number
}

type SalesDocumentInput = {
  number: string | null
  type: string
  status: string
  customerId: string
  vehicleId: string | null
  issuedAt: string
  dueDate: string | null
  taxRate: number
  rounding: '切り捨て' | '四捨五入'
  note: string | null
  details: SalesDocumentDetails
  items: SalesItemInput[]
}

type SalesDocumentDetails = {
  salesCategory: string
  staffName: string
  customerHonorific: string
  customerBirthDate: string
  customerEmployer: string
  customerContactPhone: string
  selectedImageAttachmentId: string
  customerOverride: { name: string; kana: string; phone: string; postalCode: string; address: string } | null
  vehicleOverride: { maker: string; name: string; modelType: string; plate: string; vin: string; year: string; inspectionDate: string; mileage: string; color: string; displacement: string; transmission: string; inspectionRecordAvailable: boolean } | null
  tradeIn: { name: string; modelYear: string; inspectionDate: string; mileage: string; color: string }
  recycleFee: number
  downPayment: number
  remainingPayment: number
  credit: { enabled: boolean; paymentCount: string; fee: number; monthlyPayment: number; initialPayment: number; bonusMonths: string; bonusPayment: number }
  requiredDocuments: { sealCertificate: boolean; selfDeclaration: boolean; residentCard: boolean; powerOfAttorney: boolean; lightVehicleCertificate: boolean; transferCertificate: boolean; taxPaymentCertificate: boolean; guarantorSealCertificate: boolean; warrantyCertificate: boolean; other: string }
}
