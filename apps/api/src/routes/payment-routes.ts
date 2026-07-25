import { and, desc, eq } from 'drizzle-orm'
import { customers, maintenanceDocuments, paymentRecords, salesDocuments, vehicles } from '@vehicle-management/database'
import { UnauthorizedError } from '../auth/firebase'
import { requireOrganizationContext } from '../auth/organization'
import { createDatabase } from '../db/client'
import { HttpError, jsonResponse, readJson } from '../http'

const paymentDocumentTypes = new Set(['販売請求書', '整備請求書'])
const paymentMethods = new Set(['現金', '銀行振込', 'クレジットカード', 'その他'])

export async function handlePaymentRoutes(request: Request, env: Env): Promise<Response | null> {
  const pathname = new URL(request.url).pathname.replace(/\/$/, '') || '/'
  const isCollection = pathname === '/api/payments'
  const itemMatch = pathname.match(/^\/api\/payments\/([^/]+)\/([^/]+)$/)
  if (!isCollection && !itemMatch) return null

  try {
    const database = createDatabase(env.DB)
    const context = await requireOrganizationContext(request, env, database)
    const organizationId = context.organization.organizationId
    if (isCollection) {
      if (request.method === 'GET') return await listPayments(env, database, organizationId)
      throw new HttpError(405, 'この操作には対応していません。')
    }
    if (request.method === 'PATCH') return await updatePayment(request, env, database, decodeURIComponent(itemMatch![1]), decodeURIComponent(itemMatch![2]), organizationId)
    throw new HttpError(405, 'この操作には対応していません。')
  } catch (error) {
    if (error instanceof UnauthorizedError) return jsonResponse({ error: error.message }, 401, env)
    if (error instanceof HttpError) return jsonResponse({ error: error.message }, error.status, env)
    console.error(error)
    return jsonResponse({ error: '入金情報の処理に失敗しました。' }, 500, env)
  }
}

async function listPayments(env: Env, database: ReturnType<typeof createDatabase>, organizationId: string) {
  const [salesRows, maintenanceRows, paymentRows, customerRows, vehicleRows] = await Promise.all([
    database.select().from(salesDocuments).where(and(eq(salesDocuments.organizationId, organizationId), eq(salesDocuments.type, '請求書'))).orderBy(desc(salesDocuments.issuedAt)).all(),
    database.select().from(maintenanceDocuments).where(and(eq(maintenanceDocuments.organizationId, organizationId), eq(maintenanceDocuments.type, '整備請求書'))).orderBy(desc(maintenanceDocuments.issuedAt)).all(),
    database.select().from(paymentRecords).where(eq(paymentRecords.organizationId, organizationId)).orderBy(desc(paymentRecords.updatedAt)).all(),
    database.select().from(customers).where(eq(customers.organizationId, organizationId)).all(),
    database.select().from(vehicles).where(eq(vehicles.organizationId, organizationId)).all(),
  ])
  const paymentsByKey = new Map(paymentRows.map((payment) => [`${payment.documentType}:${payment.documentId}`, payment]))
  const customersById = new Map(customerRows.map((customer) => [customer.id, customer]))
  const vehiclesById = new Map(vehicleRows.map((vehicle) => [vehicle.id, vehicle]))
  const records = [
    ...salesRows.map((document) => serializePayment('販売請求書', document, paymentsByKey.get(`販売請求書:${document.id}`), customersById, vehiclesById)),
    ...maintenanceRows.map((document) => serializePayment('整備請求書', document, paymentsByKey.get(`整備請求書:${document.id}`), customersById, vehiclesById)),
  ].sort((left, right) => right.issuedAt.localeCompare(left.issuedAt))
  return jsonResponse({ records }, 200, env)
}

async function updatePayment(request: Request, env: Env, database: ReturnType<typeof createDatabase>, documentType: string, documentId: string, organizationId: string) {
  if (!paymentDocumentTypes.has(documentType)) throw new HttpError(400, '請求書種別が不正です。')
  const invoice = await findInvoice(database, documentType, documentId, organizationId)
  if (!invoice) throw new HttpError(404, '請求書が見つかりません。')
  const body = await readJson(request)
  const paidAmount = Math.min(invoice.total, Math.max(0, integerNumber(body.paidAmount, 0)))
  const paymentDate = nullableDate(body.paymentDate)
  const method = typeof body.method === 'string' && paymentMethods.has(body.method) ? body.method : null
  const note = nullableString(body, 'note')
  const current = await database.select().from(paymentRecords).where(and(eq(paymentRecords.organizationId, organizationId), eq(paymentRecords.documentType, documentType), eq(paymentRecords.documentId, documentId))).get()
  const now = new Date().toISOString()
  if (current) {
    await database.update(paymentRecords).set({ invoiceAmount: invoice.total, paidAmount, paymentDate, method, note, updatedAt: now }).where(and(eq(paymentRecords.id, current.id), eq(paymentRecords.organizationId, organizationId))).run()
  } else {
    await database.insert(paymentRecords).values({ id: crypto.randomUUID(), organizationId, documentType, documentId, invoiceAmount: invoice.total, paidAmount, paymentDate, method, note, updatedAt: now }).run()
  }
  const [customerRows, vehicleRows] = await Promise.all([database.select().from(customers).where(eq(customers.organizationId, organizationId)).all(), database.select().from(vehicles).where(eq(vehicles.organizationId, organizationId)).all()])
  const payment = await database.select().from(paymentRecords).where(and(eq(paymentRecords.organizationId, organizationId), eq(paymentRecords.documentType, documentType), eq(paymentRecords.documentId, documentId))).get()
  return jsonResponse({ record: serializePayment(documentType as '販売請求書' | '整備請求書', invoice, payment, new Map(customerRows.map((customer) => [customer.id, customer])), new Map(vehicleRows.map((vehicle) => [vehicle.id, vehicle]))) }, 200, env)
}

async function findInvoice(database: ReturnType<typeof createDatabase>, documentType: string, documentId: string, organizationId: string) {
  if (documentType === '販売請求書') return database.select().from(salesDocuments).where(and(eq(salesDocuments.id, documentId), eq(salesDocuments.organizationId, organizationId), eq(salesDocuments.type, '請求書'))).get()
  return database.select().from(maintenanceDocuments).where(and(eq(maintenanceDocuments.id, documentId), eq(maintenanceDocuments.organizationId, organizationId), eq(maintenanceDocuments.type, '整備請求書'))).get()
}

function serializePayment(documentType: '販売請求書' | '整備請求書', document: InvoiceRow, payment: typeof paymentRecords.$inferSelect | undefined, customersById: Map<string, typeof customers.$inferSelect>, vehiclesById: Map<string, typeof vehicles.$inferSelect>) {
  const customer = customersById.get(document.customerId)
  const vehicle = document.vehicleId ? vehiclesById.get(document.vehicleId) : undefined
  return {
    id: payment?.id ?? `${documentType}:${document.id}`,
    documentType,
    documentId: document.id,
    number: document.number,
    sourceType: documentType,
    customerName: customer?.name ?? '',
    phone: customer?.phone ?? '',
    vehicle: vehicle ? [vehicle.maker, vehicle.name].filter(Boolean).join(' ') : '',
    plate: vehicle?.registrationNumber ?? '',
    issuedAt: document.issuedAt,
    dueDate: document.dueDate,
    invoiceAmount: document.total,
    paidAmount: payment?.paidAmount ?? 0,
    paymentDate: payment?.paymentDate ?? null,
    method: payment?.method ?? null,
    note: payment?.note ?? '',
  }
}

function stringValue(body: Record<string, unknown>, key: string) { return typeof body[key] === 'string' ? body[key].trim() : '' }
function nullableString(body: Record<string, unknown>, key: string) { const value = stringValue(body, key); return value || null }
function nullableDate(value: unknown) { return typeof value === 'string' && /^\d{4}[-/]\d{2}[-/]\d{2}$/.test(value.trim()) ? value.trim().replaceAll('/', '-') : null }
function integerNumber(value: unknown, fallback: number) { const number = typeof value === 'number' ? value : Number(value); return Number.isFinite(number) ? Math.round(number) : fallback }

type InvoiceRow = typeof salesDocuments.$inferSelect | typeof maintenanceDocuments.$inferSelect
