import { and, asc, desc, eq } from 'drizzle-orm'
import { customers, salesDocumentItems, salesDocuments, vehicles } from '@vehicle-management/database'
import { UnauthorizedError } from '../auth/firebase'
import { requireOrganizationContext } from '../auth/organization'
import { createDatabase } from '../db/client'
import { HttpError, jsonResponse, readJson } from '../http'

const salesDocumentTypes = new Set(['見積書', '注文書', '請求書'])
const salesStatuses = new Set(['下書き', '発行済み', '入金待ち'])

export async function handleSalesRoutes(request: Request, env: Env): Promise<Response | null> {
  const pathname = new URL(request.url).pathname.replace(/\/$/, '') || '/'
  const isCollection = pathname === '/api/sales-documents'
  const documentMatch = pathname.match(/^\/api\/sales-documents\/([^/]+)$/)
  if (!isCollection && !documentMatch) return null

  try {
    const database = createDatabase(env.DB)
    const context = await requireOrganizationContext(request, env, database)
    const organizationId = context.organization.organizationId

    if (isCollection) {
      if (request.method === 'GET') return await listSalesDocuments(request, env, database, organizationId)
      if (request.method === 'POST') return await createSalesDocument(request, env, database, organizationId)
      throw new HttpError(405, 'この操作には対応していません。')
    }

    if (!documentMatch) throw new HttpError(404, '販売書類のAPIが見つかりません。')
    if (request.method === 'PATCH') return await updateSalesDocument(request, env, database, documentMatch[1], organizationId)
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
  const documents = await loadSalesDocuments(database, organizationId)
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
  const number = await nextSalesDocumentNumber(database, input.issuedAt, organizationId)
  const totals = calculateTotals(input.items, input.taxRate, input.rounding)

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
    subtotal: totals.subtotal,
    tax: totals.tax,
    total: totals.total,
    note: input.note,
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
    taxRate: body.taxRate ?? current.taxRate,
    note: body.note === undefined ? current.note : body.note,
    items: body.items === undefined ? await loadSalesItems(database, documentId, organizationId) : body.items,
  }, database, organizationId)
  const totals = calculateTotals(input.items, input.taxRate, input.rounding)

  await database.update(salesDocuments).set({
    type: input.type,
    status: input.status,
    customerId: input.customerId,
    vehicleId: input.vehicleId,
    issuedAt: input.issuedAt,
    dueDate: input.dueDate,
    taxRate: input.taxRate,
    subtotal: totals.subtotal,
    tax: totals.tax,
    total: totals.total,
    note: input.note,
    updatedAt: new Date().toISOString(),
  }).where(and(eq(salesDocuments.id, documentId), eq(salesDocuments.organizationId, organizationId))).run()

  await database.delete(salesDocumentItems).where(and(eq(salesDocumentItems.documentId, documentId), eq(salesDocumentItems.organizationId, organizationId))).run()
  await insertSalesItems(database, documentId, input.items, organizationId)

  return jsonResponse({ document: await findSalesDocument(database, documentId, organizationId) }, 200, env)
}

async function loadSalesDocuments(database: ReturnType<typeof createDatabase>, organizationId: string) {
  const [documentRows, itemRows, customerRows, vehicleRows] = await Promise.all([
    database.select().from(salesDocuments).where(eq(salesDocuments.organizationId, organizationId)).orderBy(desc(salesDocuments.issuedAt), desc(salesDocuments.number)).all(),
    database.select().from(salesDocumentItems).where(eq(salesDocumentItems.organizationId, organizationId)).orderBy(asc(salesDocumentItems.sortOrder)).all(),
    database.select().from(customers).where(eq(customers.organizationId, organizationId)).all(),
    database.select().from(vehicles).where(eq(vehicles.organizationId, organizationId)).all(),
  ])
  const itemsByDocument = groupBy(itemRows, (item) => item.documentId)
  const customersById = new Map(customerRows.map((customer) => [customer.id, customer]))
  const vehiclesById = new Map(vehicleRows.map((vehicle) => [vehicle.id, vehicle]))

  return documentRows.map((document) => serializeSalesDocument(
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
  await database.insert(salesDocumentItems).values(items.map((item, index) => ({
    id: crypto.randomUUID(),
    organizationId,
    documentId,
    description: item.description,
    quantity: item.quantity,
    unit: item.unit,
    unitPrice: item.unitPrice,
    amount: item.amount,
    sortOrder: index,
  }))).run()
}

async function parseSalesDocumentInput(body: Record<string, unknown>, database: ReturnType<typeof createDatabase>, organizationId: string): Promise<SalesDocumentInput> {
  const type = stringValue(body, 'type')
  if (!salesDocumentTypes.has(type)) throw new HttpError(400, '書類種別が不正です。')

  const status = stringValue(body, 'status') || '下書き'
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
  const items = parseItems(body.items)
  return { type, status, customerId, vehicleId, issuedAt, dueDate, taxRate, rounding, note: nullableString(body, 'note'), items }
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
    status: document.status,
    customerId: document.customerId,
    customerName: customer?.name ?? '',
    phone: customer?.phone ?? '',
    vehicleId: document.vehicleId,
    vehicle: vehicle ? [vehicle.maker, vehicle.name].filter(Boolean).join(' ') : '',
    plate: vehicle?.registrationNumber ?? '',
    issuedAt: document.issuedAt,
    dueDate: document.dueDate,
    taxRate: document.taxRate,
    subtotal: document.subtotal,
    tax: document.tax,
    total: document.total,
    note: document.note ?? '',
    items: items.map((item) => ({
      id: item.id,
      description: item.description,
      quantity: item.quantity,
      unit: item.unit,
      unitPrice: item.unitPrice,
      amount: item.amount,
    })),
  }
}

async function nextSalesDocumentNumber(database: ReturnType<typeof createDatabase>, issuedAt: string, organizationId: string) {
  const year = issuedAt.slice(0, 4) || String(new Date().getFullYear())
  const prefix = `S-${year}-`
  const rows = await database.select({ number: salesDocuments.number }).from(salesDocuments).where(eq(salesDocuments.organizationId, organizationId)).all()
  const usedNumbers = new Set(rows.map((row) => row.number))
  let sequence = 1
  while (usedNumbers.has(`${prefix}${String(sequence).padStart(3, '0')}`)) sequence += 1
  return `${prefix}${String(sequence).padStart(3, '0')}`
}

function calculateTotals(items: SalesItemInput[], taxRate: number, rounding: '切り捨て' | '四捨五入') {
  const subtotal = items.reduce((sum, item) => sum + item.amount, 0)
  const taxValue = Math.max(0, subtotal) * taxRate / 100
  const tax = rounding === '四捨五入' ? Math.round(taxValue) : Math.floor(taxValue)
  return { subtotal, tax, total: subtotal + tax }
}

function parseItems(value: unknown): SalesItemInput[] {
  if (!Array.isArray(value)) return []
  return value.filter((item): item is Record<string, unknown> => Boolean(item) && typeof item === 'object' && !Array.isArray(item)).map((item) => {
    const quantity = nonNegativeNumber(item.quantity, 1)
    const unitPrice = integerNumber(item.unitPrice, 0)
    return {
      description: stringValue(item, 'description'),
      quantity,
      unit: stringValue(item, 'unit') || '式',
      unitPrice,
      amount: Math.round(quantity * unitPrice),
    }
  })
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
  description: string
  quantity: number
  unit: string
  unitPrice: number
  amount: number
}

type SalesDocumentInput = {
  type: string
  status: string
  customerId: string
  vehicleId: string | null
  issuedAt: string
  dueDate: string | null
  taxRate: number
  rounding: '切り捨て' | '四捨五入'
  note: string | null
  items: SalesItemInput[]
}
