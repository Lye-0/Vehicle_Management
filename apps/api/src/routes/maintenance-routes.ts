import { and, asc, desc, eq } from 'drizzle-orm'
import { customers, maintenanceItems, maintenanceDocuments, vehicles } from '@vehicle-management/database'
import { UnauthorizedError } from '../auth/firebase'
import { requireOrganizationContext } from '../auth/organization'
import { createDatabase } from '../db/client'
import { nextDocumentNumber } from '../document-number'
import { HttpError, jsonResponse, readJson } from '../http'

const maintenanceDocumentTypes = new Set(['整備見積書', '整備請求書'])
const maintenanceStatuses = new Set(['受付中', '作業中', '完了', '下書き', '入金待ち', 'アーカイブ済み'])
const maintenanceCategories = new Set(['車検', '板金', '一般整備'])
const feeNames = ['自賠責', '重量税', '印紙代', 'リサイクル料金'] as const
type FeeName = typeof feeNames[number]

export async function handleMaintenanceRoutes(request: Request, env: Env): Promise<Response | null> {
  const pathname = new URL(request.url).pathname.replace(/\/$/, '') || '/'
  const isCollection = pathname === '/api/maintenance-documents'
  const restoreMatch = pathname.match(/^\/api\/maintenance-documents\/([^/]+)\/restore$/)
  const documentMatch = pathname.match(/^\/api\/maintenance-documents\/([^/]+)$/)
  if (!isCollection && !documentMatch && !restoreMatch) return null

  try {
    const database = createDatabase(env.DB)
    const context = await requireOrganizationContext(request, env, database)
    const organizationId = context.organization.organizationId

    if (isCollection) {
      if (request.method === 'GET') return await listMaintenanceDocuments(request, env, database, organizationId)
      if (request.method === 'POST') return await createMaintenanceDocument(request, env, database, organizationId)
      throw new HttpError(405, 'この操作には対応していません。')
    }

    if (restoreMatch) {
      if (request.method !== 'POST') throw new HttpError(405, 'この操作には対応していません。')
      return await restoreMaintenanceDocument(env, database, restoreMatch[1], organizationId)
    }

    if (request.method === 'PATCH') return await updateMaintenanceDocument(request, env, database, documentMatch![1], organizationId)
    if (request.method === 'DELETE') return await archiveMaintenanceDocument(env, database, documentMatch![1], organizationId)
    throw new HttpError(405, 'この操作には対応していません。')
  } catch (error) {
    if (error instanceof UnauthorizedError) return jsonResponse({ error: error.message }, 401, env)
    if (error instanceof HttpError) return jsonResponse({ error: error.message }, error.status, env)
    console.error(error)
    return jsonResponse({ error: '整備書類の処理に失敗しました。' }, 500, env)
  }
}

async function listMaintenanceDocuments(request: Request, env: Env, database: ReturnType<typeof createDatabase>, organizationId: string) {
  return jsonResponse({ documents: await loadMaintenanceDocuments(database, organizationId, new URL(request.url).searchParams.get('includeArchived') === 'true') }, 200, env)
}

async function createMaintenanceDocument(request: Request, env: Env, database: ReturnType<typeof createDatabase>, organizationId: string) {
  const input = await parseMaintenanceInput(await readJson(request), database, organizationId)
  const id = crypto.randomUUID()
  const number = await nextDocumentNumber(env.DB, organizationId, 'M')
  await ensureMaintenanceDocumentNumberAvailable(database, number, organizationId)
  const totals = calculateMaintenanceTotals(input.items, input.fees, input.adjustment, input.taxRate, input.rounding)

  await database.insert(maintenanceDocuments).values({
    id,
    organizationId,
    number,
    type: input.type,
    category: input.category,
    status: input.status,
    customerId: input.customerId,
    vehicleId: input.vehicleId,
    intakeDate: input.intakeDate,
    plannedReleaseDate: input.plannedReleaseDate,
    completionDate: input.completionDate,
    issuedAt: input.issuedAt,
    dueDate: input.dueDate,
    taxRate: input.taxRate,
    subtotal: totals.subtotal,
    tax: totals.tax,
    total: totals.total,
    note: input.note,
    detailsJson: JSON.stringify(input.details),
  }).run()
  await insertMaintenanceItems(database, id, input.items, input.fees, input.adjustment, organizationId)

  return jsonResponse({ document: await findMaintenanceDocument(database, id, organizationId) }, 201, env)
}

async function updateMaintenanceDocument(request: Request, env: Env, database: ReturnType<typeof createDatabase>, documentId: string, organizationId: string) {
  const current = await database.select().from(maintenanceDocuments).where(and(eq(maintenanceDocuments.id, documentId), eq(maintenanceDocuments.organizationId, organizationId))).get()
  if (!current) throw new HttpError(404, '整備書類が見つかりません。')

  const currentItems = await loadMaintenanceItems(database, documentId, organizationId)
  const body = await readJson(request)
  const input = await parseMaintenanceInput({
    ...body,
    type: body.type ?? current.type,
    status: body.status ?? current.status,
    category: body.category ?? current.category,
    number: body.number === undefined ? current.number : body.number,
    customerId: body.customerId ?? current.customerId,
    vehicleId: body.vehicleId ?? current.vehicleId,
    intakeDate: body.intakeDate === undefined ? current.intakeDate : body.intakeDate,
    plannedReleaseDate: body.plannedReleaseDate === undefined ? current.plannedReleaseDate ?? current.completionDate : body.plannedReleaseDate,
    completionDate: body.completionDate === undefined ? current.completionDate : body.completionDate,
    issuedAt: body.issuedAt ?? current.issuedAt,
    dueDate: body.dueDate === undefined ? current.dueDate : body.dueDate,
    taxRate: body.taxRate ?? current.taxRate,
    note: body.note === undefined ? current.note : body.note,
    details: body.details === undefined ? parseDetailsJson(current.detailsJson) : body.details,
    items: body.items === undefined ? currentItems.filter((item) => item.itemType === '作業' || item.itemType === '部品').map(toInputItem) : body.items,
    fees: body.fees === undefined ? extractFees(currentItems) : body.fees,
    adjustment: body.adjustment === undefined ? extractAdjustment(currentItems) : body.adjustment,
  }, database, organizationId)
  const totals = calculateMaintenanceTotals(input.items, input.fees, input.adjustment, input.taxRate, input.rounding)
  const number = input.number || current.number
  await ensureMaintenanceDocumentNumberAvailable(database, number, organizationId, documentId)

  await database.update(maintenanceDocuments).set({
    number,
    type: input.type,
    category: input.category,
    status: input.status,
    customerId: input.customerId,
    vehicleId: input.vehicleId,
    intakeDate: input.intakeDate,
    plannedReleaseDate: input.plannedReleaseDate,
    completionDate: input.completionDate,
    issuedAt: input.issuedAt,
    dueDate: input.dueDate,
    taxRate: input.taxRate,
    subtotal: totals.subtotal,
    tax: totals.tax,
    total: totals.total,
    note: input.note,
    detailsJson: JSON.stringify(input.details),
    updatedAt: new Date().toISOString(),
  }).where(and(eq(maintenanceDocuments.id, documentId), eq(maintenanceDocuments.organizationId, organizationId))).run()
  await database.delete(maintenanceItems).where(and(eq(maintenanceItems.documentId, documentId), eq(maintenanceItems.organizationId, organizationId))).run()
  await insertMaintenanceItems(database, documentId, input.items, input.fees, input.adjustment, organizationId)

  return jsonResponse({ document: await findMaintenanceDocument(database, documentId, organizationId) }, 200, env)
}

async function loadMaintenanceDocuments(database: ReturnType<typeof createDatabase>, organizationId: string, includeArchived = false) {
  const [documentRows, itemRows, customerRows, vehicleRows] = await Promise.all([
    database.select().from(maintenanceDocuments).where(eq(maintenanceDocuments.organizationId, organizationId)).orderBy(desc(maintenanceDocuments.issuedAt), desc(maintenanceDocuments.number)).all(),
    database.select().from(maintenanceItems).where(eq(maintenanceItems.organizationId, organizationId)).orderBy(asc(maintenanceItems.sortOrder)).all(),
    database.select().from(customers).where(eq(customers.organizationId, organizationId)).all(),
    database.select().from(vehicles).where(eq(vehicles.organizationId, organizationId)).all(),
  ])
  const itemsByDocument = groupBy(itemRows, (item) => item.documentId)
  const customersById = new Map(customerRows.map((customer) => [customer.id, customer]))
  const vehiclesById = new Map(vehicleRows.map((vehicle) => [vehicle.id, vehicle]))

  return documentRows.filter((document) => includeArchived || !document.archivedAt).map((document) => serializeMaintenanceDocument(
    document,
    customersById.get(document.customerId),
    vehiclesById.get(document.vehicleId),
    itemsByDocument.get(document.id) ?? [],
  ))
}

async function findMaintenanceDocument(database: ReturnType<typeof createDatabase>, documentId: string, organizationId: string) {
  const documents = await loadMaintenanceDocuments(database, organizationId)
  return documents.find((document) => document.id === documentId) ?? null
}

async function loadMaintenanceItems(database: ReturnType<typeof createDatabase>, documentId: string, organizationId: string) {
  return database.select().from(maintenanceItems).where(and(eq(maintenanceItems.documentId, documentId), eq(maintenanceItems.organizationId, organizationId))).orderBy(asc(maintenanceItems.sortOrder)).all()
}

async function insertMaintenanceItems(database: ReturnType<typeof createDatabase>, documentId: string, items: MaintenanceItemInput[], fees: Record<FeeName, number>, adjustment: number, organizationId: string) {
  const rows = [
    ...items.map((item) => ({ itemType: item.kind, description: item.description, quantity: item.quantity, unit: item.unit, unitPrice: item.unitPrice, technicalFee: item.technicalFee, summary: item.summary, amount: item.amount })),
    ...feeNames.map((name) => ({ itemType: '法定費用', description: name, quantity: 1, unit: '式', unitPrice: fees[name], technicalFee: 0, summary: '', amount: fees[name] })),
    ...(adjustment === 0 ? [] : [{ itemType: '調整', description: '調整額', quantity: 1, unit: '式', unitPrice: adjustment, technicalFee: 0, summary: '', amount: adjustment }]),
  ]
  if (!rows.length) return
  await database.insert(maintenanceItems).values(rows.map((item, index) => ({ id: crypto.randomUUID(), organizationId, documentId, ...item, sortOrder: index }))).run()
}

function parseMaintenanceInput(body: Record<string, unknown>, database: ReturnType<typeof createDatabase>, organizationId: string): Promise<MaintenanceInput> {
  return parseMaintenanceInputAsync(body, database, organizationId)
}

async function parseMaintenanceInputAsync(body: Record<string, unknown>, database: ReturnType<typeof createDatabase>, organizationId: string): Promise<MaintenanceInput> {
  const type = stringValue(body, 'type') || '整備請求書'
  if (!maintenanceDocumentTypes.has(type)) throw new HttpError(400, '書類種別が不正です。')
  const status = stringValue(body, 'status') || '受付中'
  if (!maintenanceStatuses.has(status)) throw new HttpError(400, '整備書類ステータスが不正です。')
  const category = stringValue(body, 'category')
  if (!maintenanceCategories.has(category)) throw new HttpError(400, '入庫区分が不正です。')

  const customerId = stringValue(body, 'customerId')
  const customer = customerId ? await database.select({ id: customers.id }).from(customers).where(and(eq(customers.id, customerId), eq(customers.organizationId, organizationId))).get() : null
  if (!customer) throw new HttpError(400, '顧客を選択してください。')
  const vehicleId = stringValue(body, 'vehicleId')
  const vehicle = vehicleId ? await database.select({ id: vehicles.id, customerId: vehicles.customerId }).from(vehicles).where(and(eq(vehicles.id, vehicleId), eq(vehicles.organizationId, organizationId))).get() : null
  if (!vehicle || vehicle.customerId !== customerId) throw new HttpError(400, '選択した車両が顧客と一致しません。')

  const items = parseItems(body.items)
  const fees = parseFees(body.fees)
  const adjustment = integerNumber(body.adjustment, 0)
  const taxRate = parseTaxRate(body.taxRate)
  const rounding = body.rounding === '四捨五入' ? '四捨五入' : '切り捨て'
  return {
    number: nullableString(body, 'number'),
    type,
    status,
    category,
    customerId,
    vehicleId,
    intakeDate: nullableDate(body.intakeDate),
    plannedReleaseDate: nullableDate(body.plannedReleaseDate),
    completionDate: nullableDate(body.completionDate),
    issuedAt: dateValue(body.issuedAt) || today(),
    dueDate: nullableDate(body.dueDate),
    taxRate,
    rounding,
    note: nullableString(body, 'note'),
    details: parseMaintenanceDetails(body.details),
    items,
    fees,
    adjustment,
  }
}

function serializeMaintenanceDocument(document: typeof maintenanceDocuments.$inferSelect, customer: typeof customers.$inferSelect | undefined, vehicle: typeof vehicles.$inferSelect | undefined, rows: Array<typeof maintenanceItems.$inferSelect>) {
  const fees = extractFees(rows)
  const adjustment = extractAdjustment(rows)
  const items = rows.filter((item) => item.itemType === '作業' || item.itemType === '部品')
  return {
    id: document.id,
    number: document.number,
    type: document.type,
    status: document.status,
    category: document.category,
    customerId: document.customerId,
    customerName: customer?.name ?? '',
    phone: customer?.phone ?? '',
    customerDetails: {
      name: customer?.name ?? '',
      kana: customer?.nameKana ?? '',
      phone: customer?.phone ?? '',
      postalCode: customer?.postalCode ?? '',
      address: customer?.address ?? '',
    },
    vehicleId: document.vehicleId,
    vehicle: vehicle ? [vehicle.maker, vehicle.name].filter(Boolean).join(' ') : '',
    plate: vehicle?.registrationNumber ?? '',
    mileage: vehicle?.mileage === null || vehicle?.mileage === undefined ? '' : `${vehicle.mileage.toLocaleString('ja-JP')} km`,
    vehicleDetails: {
      maker: vehicle?.maker ?? '',
      name: vehicle?.name ?? '',
      modelType: vehicle?.model ?? '',
      plate: vehicle?.registrationNumber ?? '',
      vin: vehicle?.chassisNumber ?? '',
      year: vehicle?.modelYear === null || vehicle?.modelYear === undefined ? '' : String(vehicle.modelYear),
      inspectionDate: vehicle?.inspectionDate ?? '',
      mileage: vehicle?.mileage === null || vehicle?.mileage === undefined ? '' : `${vehicle.mileage.toLocaleString('ja-JP')}km`,
      color: vehicle?.bodyColor ?? '',
      displacement: vehicle?.displacement === null || vehicle?.displacement === undefined ? '' : String(vehicle.displacement),
      transmission: vehicle?.transmission ?? '',
      inspectionRecordAvailable: vehicle?.inspectionRecordAvailable ?? false,
    },
    intakeDate: document.intakeDate,
    plannedReleaseDate: document.plannedReleaseDate ?? document.completionDate,
    completionDate: document.completionDate,
    issuedAt: document.issuedAt,
    dueDate: document.dueDate,
    taxRate: document.taxRate,
    subtotal: document.subtotal,
    tax: document.tax,
    total: document.total,
    fees,
    adjustment,
    note: document.note ?? '',
    details: parseMaintenanceDetails(parseDetailsJson(document.detailsJson)),
    archivedAt: document.archivedAt,
    items: items.map((item) => ({ id: item.id, kind: item.itemType === '部品' ? '部品' : '作業', description: item.description, quantity: item.quantity, unit: item.unit, unitPrice: item.unitPrice, technicalFee: item.technicalFee, summary: item.summary })),
  }
}

export function calculateMaintenanceTotals(items: MaintenanceItemInput[], fees: Record<FeeName, number>, adjustment: number, taxRate: number, rounding: '切り捨て' | '四捨五入') {
  const subtotal = items.reduce((sum, item) => sum + item.amount, 0)
  const taxableAmount = Math.max(0, subtotal)
  const taxValue = taxableAmount * taxRate / 100
  const tax = rounding === '四捨五入' ? Math.round(taxValue) : Math.floor(taxValue)
  const feesTotal = Object.values(fees).reduce((sum, fee) => sum + fee, 0) + adjustment
  return { subtotal, tax, total: subtotal + feesTotal + tax }
}

async function ensureMaintenanceDocumentNumberAvailable(database: ReturnType<typeof createDatabase>, number: string, organizationId: string, exceptId?: string) {
  const duplicate = await database.select({ id: maintenanceDocuments.id }).from(maintenanceDocuments).where(and(eq(maintenanceDocuments.organizationId, organizationId), eq(maintenanceDocuments.number, number))).get()
  if (duplicate && duplicate.id !== exceptId) throw new HttpError(409, '同じ書類番号の整備書類がすでに存在します。')
}

async function archiveMaintenanceDocument(env: Env, database: ReturnType<typeof createDatabase>, documentId: string, organizationId: string) {
  const current = await database.select({ id: maintenanceDocuments.id }).from(maintenanceDocuments).where(and(eq(maintenanceDocuments.id, documentId), eq(maintenanceDocuments.organizationId, organizationId))).get()
  if (!current) throw new HttpError(404, '整備書類が見つかりません。')
  await database.update(maintenanceDocuments).set({ status: 'アーカイブ済み', archivedAt: new Date().toISOString(), updatedAt: new Date().toISOString() }).where(and(eq(maintenanceDocuments.id, documentId), eq(maintenanceDocuments.organizationId, organizationId))).run()
  return jsonResponse({ archived: true }, 200, env)
}

async function restoreMaintenanceDocument(env: Env, database: ReturnType<typeof createDatabase>, documentId: string, organizationId: string) {
  const current = await database.select().from(maintenanceDocuments).where(and(eq(maintenanceDocuments.id, documentId), eq(maintenanceDocuments.organizationId, organizationId))).get()
  if (!current) throw new HttpError(404, '整備書類が見つかりません。')
  await database.update(maintenanceDocuments).set({ status: '受付中', archivedAt: null, updatedAt: new Date().toISOString() }).where(and(eq(maintenanceDocuments.id, documentId), eq(maintenanceDocuments.organizationId, organizationId))).run()
  return jsonResponse({ restored: true }, 200, env)
}

function parseItems(value: unknown): MaintenanceItemInput[] {
  if (!Array.isArray(value)) return []
  return value.filter((item): item is Record<string, unknown> => Boolean(item) && typeof item === 'object' && !Array.isArray(item)).map((item) => {
    const quantity = nonNegativeNumber(item.quantity, 1)
    const unitPrice = integerNumber(item.unitPrice, 0)
    const technicalFee = integerNumber(item.technicalFee, 0)
    return { kind: item.kind === '部品' ? '部品' : '作業', description: stringValue(item, 'description'), quantity, unit: stringValue(item, 'unit') || '式', unitPrice, technicalFee, summary: stringValue(item, 'summary'), amount: Math.round(quantity * unitPrice) + technicalFee }
  })
}

function parseFees(value: unknown): Record<FeeName, number> {
  const source = value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : {}
  return { 自賠責: nonNegativeInteger(source.自賠責, 0), 重量税: nonNegativeInteger(source.重量税, 0), 印紙代: nonNegativeInteger(source.印紙代, 0), リサイクル料金: nonNegativeInteger(source.リサイクル料金, 0) }
}

function extractFees(rows: Array<{ itemType: string; description: string; amount: number }>): Record<FeeName, number> {
  const fees = { 自賠責: 0, 重量税: 0, 印紙代: 0, リサイクル料金: 0 }
  for (const row of rows) if (row.itemType === '法定費用' && feeNames.includes(row.description as FeeName)) fees[row.description as FeeName] = row.amount
  return fees
}

function extractAdjustment(rows: Array<{ itemType: string; amount: number }>) {
  return rows.find((row) => row.itemType === '調整')?.amount ?? 0
}

function toInputItem(item: typeof maintenanceItems.$inferSelect): Record<string, unknown> {
  return { kind: item.itemType, description: item.description, quantity: item.quantity, unit: item.unit, unitPrice: item.unitPrice, technicalFee: item.technicalFee, summary: item.summary }
}

function groupBy<T>(items: T[], getKey: (item: T) => string) {
  const grouped = new Map<string, T[]>()
  for (const item of items) grouped.set(getKey(item), [...(grouped.get(getKey(item)) ?? []), item])
  return grouped
}

function stringValue(body: Record<string, unknown>, key: string) { return typeof body[key] === 'string' ? body[key].trim() : '' }
function nullableString(body: Record<string, unknown>, key: string) { const value = stringValue(body, key); return value || null }
function dateValue(value: unknown) { return typeof value === 'string' && /^\d{4}[-/]\d{2}[-/]\d{2}$/.test(value.trim()) ? value.trim().replaceAll('/', '-') : '' }
function nullableDate(value: unknown) { return dateValue(value) || null }
function parseTaxRate(value: unknown) { const number = typeof value === 'number' ? value : Number(value); const normalized = number > 0 && number < 1 ? number * 100 : number; return Number.isFinite(normalized) && normalized >= 0 && normalized <= 100 ? Math.round(normalized) : 10 }
function nonNegativeNumber(value: unknown, fallback: number) { const number = typeof value === 'number' ? value : Number(value); return Number.isFinite(number) && number >= 0 ? number : fallback }
function integerNumber(value: unknown, fallback: number) { const number = typeof value === 'number' ? value : Number(value); return Number.isFinite(number) ? Math.round(number) : fallback }
function nonNegativeInteger(value: unknown, fallback: number) { return Math.max(0, integerNumber(value, fallback)) }
function today() { return new Date().toISOString().slice(0, 10) }

function parseDetailsJson(value: string | null) {
  if (!value) return {}
  try {
    const parsed = JSON.parse(value)
    return parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? parsed : {}
  } catch {
    return {}
  }
}

function parseMaintenanceDetails(value: unknown): MaintenanceDetails {
  const source = recordValue(value)
  const customerOverride = recordValue(source.customerOverride)
  const vehicleOverride = recordValue(source.vehicleOverride)
  const labels = recordValue(source.labels)
  const normalizedCustomerOverride = {
    name: stringValue(customerOverride, 'name'),
    kana: stringValue(customerOverride, 'kana'),
    phone: stringValue(customerOverride, 'phone'),
    postalCode: stringValue(customerOverride, 'postalCode'),
    address: stringValue(customerOverride, 'address'),
  }
  const normalizedVehicleOverride = {
    maker: stringValue(vehicleOverride, 'maker'),
    name: stringValue(vehicleOverride, 'name'),
    modelType: stringValue(vehicleOverride, 'modelType'),
    plate: stringValue(vehicleOverride, 'plate'),
    vin: stringValue(vehicleOverride, 'vin'),
    year: stringValue(vehicleOverride, 'year'),
    inspectionDate: stringValue(vehicleOverride, 'inspectionDate'),
    mileage: stringValue(vehicleOverride, 'mileage'),
    color: stringValue(vehicleOverride, 'color'),
    displacement: stringValue(vehicleOverride, 'displacement'),
    transmission: stringValue(vehicleOverride, 'transmission'),
    inspectionRecordAvailable: typeof vehicleOverride.inspectionRecordAvailable === 'boolean' ? vehicleOverride.inspectionRecordAvailable : false,
  }
  return {
    staffName: stringValue(source, 'staffName'),
    customerHonorific: stringValue(source, 'customerHonorific') || '様',
    customerBirthDate: stringValue(source, 'customerBirthDate'),
    customerEmployer: stringValue(source, 'customerEmployer'),
    customerContactPhone: stringValue(source, 'customerContactPhone'),
    bankName: stringValue(source, 'bankName'),
    bankAccount: stringValue(source, 'bankAccount'),
    customerOverride: hasOverrideValue(normalizedCustomerOverride) ? normalizedCustomerOverride : null,
    vehicleOverride: hasOverrideValue(normalizedVehicleOverride) ? normalizedVehicleOverride : null,
    labels: {
      documentTitle: '',
      amountTitle: 'お見積金額（税込）',
      vehicleSectionTitle: stringValue(labels, 'vehicleSectionTitle') || '車両情報',
      workSectionTitle: '作業内容／部品名等',
      bankTitle: stringValue(labels, 'bankTitle') || 'お振込先',
      otherFee: stringValue(labels, 'otherFee') || 'その他',
    },
  }
}

function hasOverrideValue(value: Record<string, string | boolean>) {
  return Object.values(value).some((field) => typeof field === 'string' ? field.length > 0 : field)
}

function recordValue(value: unknown): Record<string, unknown> {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : {}
}

export type MaintenanceItemInput = { kind: '作業' | '部品'; description: string; quantity: number; unit: string; unitPrice: number; technicalFee: number; summary: string; amount: number }
type MaintenanceDetails = {
  staffName: string
  customerHonorific: string
  customerBirthDate: string
  customerEmployer: string
  customerContactPhone: string
  bankName: string
  bankAccount: string
  customerOverride: Record<string, string> | null
  vehicleOverride: Record<string, string | boolean> | null
  labels: Record<string, string>
}
type MaintenanceInput = { number: string | null; type: string; status: string; category: string; customerId: string; vehicleId: string; intakeDate: string | null; plannedReleaseDate: string | null; completionDate: string | null; issuedAt: string; dueDate: string | null; taxRate: number; rounding: '切り捨て' | '四捨五入'; note: string | null; details: MaintenanceDetails; items: MaintenanceItemInput[]; fees: Record<FeeName, number>; adjustment: number }
