import { asc, desc, eq } from 'drizzle-orm'
import { customers, maintenanceItems, maintenanceDocuments, vehicles } from '@vehicle-management/database'
import { requireAuthenticatedUser, UnauthorizedError } from '../auth/firebase'
import { createDatabase } from '../db/client'
import { HttpError, jsonResponse, readJson } from '../http'

const maintenanceDocumentTypes = new Set(['整備見積書', '納品書', '整備請求書'])
const maintenanceStatuses = new Set(['受付中', '作業中', '完了', '下書き', '入金待ち'])
const maintenanceCategories = new Set(['車検', '法定点検', '一般整備'])
const feeNames = ['自賠責', '重量税', '印紙代', 'リサイクル料金'] as const
type FeeName = typeof feeNames[number]

export async function handleMaintenanceRoutes(request: Request, env: Env): Promise<Response | null> {
  const pathname = new URL(request.url).pathname.replace(/\/$/, '') || '/'
  const isCollection = pathname === '/api/maintenance-documents'
  const documentMatch = pathname.match(/^\/api\/maintenance-documents\/([^/]+)$/)
  if (!isCollection && !documentMatch) return null

  try {
    await requireAuthenticatedUser(request, env)
    const database = createDatabase(env.DB)

    if (isCollection) {
      if (request.method === 'GET') return listMaintenanceDocuments(env, database)
      if (request.method === 'POST') return createMaintenanceDocument(request, env, database)
      throw new HttpError(405, 'この操作には対応していません。')
    }

    if (request.method === 'PATCH') return updateMaintenanceDocument(request, env, database, documentMatch![1])
    throw new HttpError(405, 'この操作には対応していません。')
  } catch (error) {
    if (error instanceof UnauthorizedError) return jsonResponse({ error: error.message }, 401, env)
    if (error instanceof HttpError) return jsonResponse({ error: error.message }, error.status, env)
    console.error(error)
    return jsonResponse({ error: '整備書類の処理に失敗しました。' }, 500, env)
  }
}

async function listMaintenanceDocuments(env: Env, database: ReturnType<typeof createDatabase>) {
  return jsonResponse({ documents: await loadMaintenanceDocuments(database) }, 200, env)
}

async function createMaintenanceDocument(request: Request, env: Env, database: ReturnType<typeof createDatabase>) {
  const input = await parseMaintenanceInput(await readJson(request), database)
  const id = crypto.randomUUID()
  const number = await nextMaintenanceDocumentNumber(database, input.issuedAt)
  const totals = calculateTotals(input.items, input.fees, input.adjustment, input.taxRate, input.rounding)

  await database.insert(maintenanceDocuments).values({
    id,
    number,
    type: input.type,
    category: input.category,
    status: input.status,
    customerId: input.customerId,
    vehicleId: input.vehicleId,
    intakeDate: input.intakeDate,
    completionDate: input.completionDate,
    issuedAt: input.issuedAt,
    dueDate: input.dueDate,
    taxRate: input.taxRate,
    subtotal: totals.subtotal,
    tax: totals.tax,
    total: totals.total,
    note: input.note,
  }).run()
  await insertMaintenanceItems(database, id, input.items, input.fees, input.adjustment)

  return jsonResponse({ document: await findMaintenanceDocument(database, id) }, 201, env)
}

async function updateMaintenanceDocument(request: Request, env: Env, database: ReturnType<typeof createDatabase>, documentId: string) {
  const current = await database.select().from(maintenanceDocuments).where(eq(maintenanceDocuments.id, documentId)).get()
  if (!current) throw new HttpError(404, '整備書類が見つかりません。')

  const currentItems = await loadMaintenanceItems(database, documentId)
  const body = await readJson(request)
  const input = await parseMaintenanceInput({
    ...body,
    type: body.type ?? current.type,
    status: body.status ?? current.status,
    category: body.category ?? current.category,
    customerId: body.customerId ?? current.customerId,
    vehicleId: body.vehicleId ?? current.vehicleId,
    intakeDate: body.intakeDate === undefined ? current.intakeDate : body.intakeDate,
    completionDate: body.completionDate === undefined ? current.completionDate : body.completionDate,
    issuedAt: body.issuedAt ?? current.issuedAt,
    dueDate: body.dueDate === undefined ? current.dueDate : body.dueDate,
    taxRate: body.taxRate ?? current.taxRate,
    note: body.note === undefined ? current.note : body.note,
    items: body.items === undefined ? currentItems.filter((item) => item.itemType === '作業' || item.itemType === '部品').map(toInputItem) : body.items,
    fees: body.fees === undefined ? extractFees(currentItems) : body.fees,
    adjustment: body.adjustment === undefined ? extractAdjustment(currentItems) : body.adjustment,
  }, database)
  const totals = calculateTotals(input.items, input.fees, input.adjustment, input.taxRate, input.rounding)

  await database.update(maintenanceDocuments).set({
    type: input.type,
    category: input.category,
    status: input.status,
    customerId: input.customerId,
    vehicleId: input.vehicleId,
    intakeDate: input.intakeDate,
    completionDate: input.completionDate,
    issuedAt: input.issuedAt,
    dueDate: input.dueDate,
    taxRate: input.taxRate,
    subtotal: totals.subtotal,
    tax: totals.tax,
    total: totals.total,
    note: input.note,
    updatedAt: new Date().toISOString(),
  }).where(eq(maintenanceDocuments.id, documentId)).run()
  await database.delete(maintenanceItems).where(eq(maintenanceItems.documentId, documentId)).run()
  await insertMaintenanceItems(database, documentId, input.items, input.fees, input.adjustment)

  return jsonResponse({ document: await findMaintenanceDocument(database, documentId) }, 200, env)
}

async function loadMaintenanceDocuments(database: ReturnType<typeof createDatabase>) {
  const [documentRows, itemRows, customerRows, vehicleRows] = await Promise.all([
    database.select().from(maintenanceDocuments).orderBy(desc(maintenanceDocuments.issuedAt), desc(maintenanceDocuments.number)).all(),
    database.select().from(maintenanceItems).orderBy(asc(maintenanceItems.sortOrder)).all(),
    database.select().from(customers).all(),
    database.select().from(vehicles).all(),
  ])
  const itemsByDocument = groupBy(itemRows, (item) => item.documentId)
  const customersById = new Map(customerRows.map((customer) => [customer.id, customer]))
  const vehiclesById = new Map(vehicleRows.map((vehicle) => [vehicle.id, vehicle]))

  return documentRows.map((document) => serializeMaintenanceDocument(
    document,
    customersById.get(document.customerId),
    vehiclesById.get(document.vehicleId),
    itemsByDocument.get(document.id) ?? [],
  ))
}

async function findMaintenanceDocument(database: ReturnType<typeof createDatabase>, documentId: string) {
  const documents = await loadMaintenanceDocuments(database)
  return documents.find((document) => document.id === documentId) ?? null
}

async function loadMaintenanceItems(database: ReturnType<typeof createDatabase>, documentId: string) {
  return database.select().from(maintenanceItems).where(eq(maintenanceItems.documentId, documentId)).orderBy(asc(maintenanceItems.sortOrder)).all()
}

async function insertMaintenanceItems(database: ReturnType<typeof createDatabase>, documentId: string, items: MaintenanceItemInput[], fees: Record<FeeName, number>, adjustment: number) {
  const rows = [
    ...items.map((item) => ({ itemType: item.kind, description: item.description, quantity: item.quantity, unit: item.unit, unitPrice: item.unitPrice, amount: item.amount })),
    ...feeNames.map((name) => ({ itemType: '法定費用', description: name, quantity: 1, unit: '式', unitPrice: fees[name], amount: fees[name] })),
    ...(adjustment === 0 ? [] : [{ itemType: '調整', description: '調整額', quantity: 1, unit: '式', unitPrice: adjustment, amount: adjustment }]),
  ]
  if (!rows.length) return
  await database.insert(maintenanceItems).values(rows.map((item, index) => ({ id: crypto.randomUUID(), documentId, ...item, sortOrder: index }))).run()
}

function parseMaintenanceInput(body: Record<string, unknown>, database: ReturnType<typeof createDatabase>): Promise<MaintenanceInput> {
  return parseMaintenanceInputAsync(body, database)
}

async function parseMaintenanceInputAsync(body: Record<string, unknown>, database: ReturnType<typeof createDatabase>): Promise<MaintenanceInput> {
  const type = stringValue(body, 'type') || '整備請求書'
  if (!maintenanceDocumentTypes.has(type)) throw new HttpError(400, '書類種別が不正です。')
  const status = stringValue(body, 'status') || '受付中'
  if (!maintenanceStatuses.has(status)) throw new HttpError(400, '整備書類ステータスが不正です。')
  const category = stringValue(body, 'category')
  if (!maintenanceCategories.has(category)) throw new HttpError(400, '入庫区分が不正です。')

  const customerId = stringValue(body, 'customerId')
  const customer = customerId ? await database.select({ id: customers.id }).from(customers).where(eq(customers.id, customerId)).get() : null
  if (!customer) throw new HttpError(400, '顧客を選択してください。')
  const vehicleId = stringValue(body, 'vehicleId')
  const vehicle = vehicleId ? await database.select({ id: vehicles.id, customerId: vehicles.customerId }).from(vehicles).where(eq(vehicles.id, vehicleId)).get() : null
  if (!vehicle || vehicle.customerId !== customerId) throw new HttpError(400, '選択した車両が顧客と一致しません。')

  const items = parseItems(body.items)
  const fees = parseFees(body.fees)
  const adjustment = integerNumber(body.adjustment, 0)
  const taxRate = parseTaxRate(body.taxRate)
  const rounding = body.rounding === '四捨五入' ? '四捨五入' : '切り捨て'
  return {
    type,
    status,
    category,
    customerId,
    vehicleId,
    intakeDate: nullableDate(body.intakeDate),
    completionDate: nullableDate(body.completionDate ?? body.plannedReleaseDate),
    issuedAt: dateValue(body.issuedAt) || today(),
    dueDate: nullableDate(body.dueDate),
    taxRate,
    rounding,
    note: nullableString(body, 'note'),
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
    vehicleId: document.vehicleId,
    vehicle: vehicle ? [vehicle.maker, vehicle.name].filter(Boolean).join(' ') : '',
    plate: vehicle?.registrationNumber ?? '',
    mileage: vehicle?.mileage === null || vehicle?.mileage === undefined ? '' : `${vehicle.mileage.toLocaleString('ja-JP')} km`,
    intakeDate: document.intakeDate,
    plannedReleaseDate: document.completionDate,
    issuedAt: document.issuedAt,
    dueDate: document.dueDate,
    taxRate: document.taxRate,
    subtotal: document.subtotal,
    tax: document.tax,
    total: document.total,
    fees,
    adjustment,
    note: document.note ?? '',
    items: items.map((item) => ({ id: item.id, kind: item.itemType === '部品' ? '部品' : '作業', description: item.description, quantity: item.quantity, unit: item.unit, unitPrice: item.unitPrice })),
  }
}

async function nextMaintenanceDocumentNumber(database: ReturnType<typeof createDatabase>, issuedAt: string) {
  const prefix = `M-${issuedAt.slice(0, 4) || new Date().getFullYear()}-`
  const rows = await database.select({ number: maintenanceDocuments.number }).from(maintenanceDocuments).all()
  const usedNumbers = new Set(rows.map((row) => row.number))
  let sequence = 1
  while (usedNumbers.has(`${prefix}${String(sequence).padStart(3, '0')}`)) sequence += 1
  return `${prefix}${String(sequence).padStart(3, '0')}`
}

function calculateTotals(items: MaintenanceItemInput[], fees: Record<FeeName, number>, adjustment: number, taxRate: number, rounding: '切り捨て' | '四捨五入') {
  const subtotal = items.reduce((sum, item) => sum + item.amount, 0)
  const taxableAmount = Math.max(0, subtotal + adjustment)
  const taxValue = taxableAmount * taxRate / 100
  const tax = rounding === '四捨五入' ? Math.round(taxValue) : Math.floor(taxValue)
  return { subtotal, tax, total: subtotal + Object.values(fees).reduce((sum, fee) => sum + fee, 0) + adjustment + tax }
}

function parseItems(value: unknown): MaintenanceItemInput[] {
  if (!Array.isArray(value)) return []
  return value.filter((item): item is Record<string, unknown> => Boolean(item) && typeof item === 'object' && !Array.isArray(item)).map((item) => {
    const quantity = nonNegativeNumber(item.quantity, 1)
    const unitPrice = integerNumber(item.unitPrice, 0)
    return { kind: item.kind === '部品' ? '部品' : '作業', description: stringValue(item, 'description'), quantity, unit: stringValue(item, 'unit') || '式', unitPrice, amount: Math.round(quantity * unitPrice) }
  })
}

function parseFees(value: unknown): Record<FeeName, number> {
  const source = value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : {}
  return { 自賠責: integerNumber(source.自賠責, 0), 重量税: integerNumber(source.重量税, 0), 印紙代: integerNumber(source.印紙代, 0), リサイクル料金: integerNumber(source.リサイクル料金, 0) }
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
  return { kind: item.itemType, description: item.description, quantity: item.quantity, unit: item.unit, unitPrice: item.unitPrice }
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
function today() { return new Date().toISOString().slice(0, 10) }

type MaintenanceItemInput = { kind: '作業' | '部品'; description: string; quantity: number; unit: string; unitPrice: number; amount: number }
type MaintenanceInput = { type: string; status: string; category: string; customerId: string; vehicleId: string; intakeDate: string | null; completionDate: string | null; issuedAt: string; dueDate: string | null; taxRate: number; rounding: '切り捨て' | '四捨五入'; note: string | null; items: MaintenanceItemInput[]; fees: Record<FeeName, number>; adjustment: number }
