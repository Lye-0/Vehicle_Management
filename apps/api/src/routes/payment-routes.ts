import { and, desc, eq, inArray, isNull } from 'drizzle-orm'
import { customers, maintenanceDocuments, paymentEntries, paymentRecords, salesDocuments, vehicles } from '@vehicle-management/database'
import { UnauthorizedError } from '../auth/firebase'
import { requireOrganizationContext } from '../auth/organization'
import { createDatabase } from '../db/client'
import { HttpError, jsonResponse, readJson } from '../http'

const paymentDocumentTypes = new Set(['販売請求書', '整備請求書'])
const paymentMethods = new Set(['現金', '銀行振込', 'クレジットカード', 'その他'])
const paymentDocumentStatuses = ['入金待ち', '完了'] as const

export async function handlePaymentRoutes(request: Request, env: Env): Promise<Response | null> {
  const pathname = new URL(request.url).pathname.replace(/\/$/, '') || '/'
  const isCollection = pathname === '/api/payments'
  const itemMatch = pathname.match(/^\/api\/payments\/([^/]+)\/([^/]+)$/)
  const entryCollectionMatch = pathname.match(/^\/api\/payments\/([^/]+)\/([^/]+)\/entries$/)
  const entryItemMatch = pathname.match(/^\/api\/payments\/([^/]+)\/([^/]+)\/entries\/([^/]+)$/)
  if (!isCollection && !itemMatch && !entryCollectionMatch && !entryItemMatch) return null

  try {
    const database = createDatabase(env.DB)
    const context = await requireOrganizationContext(request, env, database)
    const organizationId = context.organization.organizationId
    if (isCollection) {
      if (request.method === 'GET') return await listPayments(env, database, organizationId)
      throw new HttpError(405, 'この操作には対応していません。')
    }
    if (entryCollectionMatch) {
      if (request.method === 'POST') return await addPaymentEntry(request, env, database, decodeURIComponent(entryCollectionMatch[1]), decodeURIComponent(entryCollectionMatch[2]), organizationId)
      throw new HttpError(405, 'この操作には対応していません。')
    }
    if (entryItemMatch) {
      const documentType = decodeURIComponent(entryItemMatch[1])
      const documentId = decodeURIComponent(entryItemMatch[2])
      const entryId = decodeURIComponent(entryItemMatch[3])
      if (request.method === 'PATCH') return await updatePaymentEntry(request, env, database, documentType, documentId, entryId, organizationId)
      if (request.method === 'DELETE') return await deletePaymentEntry(request, env, database, documentType, documentId, entryId, organizationId)
      throw new HttpError(405, 'この操作には対応していません。')
    }
    if (itemMatch && request.method === 'PATCH') return await replacePayment(request, env, database, decodeURIComponent(itemMatch[1]), decodeURIComponent(itemMatch[2]), organizationId)
    throw new HttpError(405, 'この操作には対応していません。')
  } catch (error) {
    if (error instanceof UnauthorizedError) return jsonResponse({ error: error.message }, 401, env)
    if (error instanceof HttpError) return jsonResponse({ error: error.message }, error.status, env)
    console.error(error)
    return jsonResponse({ error: '入金情報の処理に失敗しました。' }, 500, env)
  }
}

async function listPayments(env: Env, database: ReturnType<typeof createDatabase>, organizationId: string) {
  const [salesRows, maintenanceRows, paymentRows, paymentEntryRows, customerRows, vehicleRows] = await Promise.all([
    database.select().from(salesDocuments).where(and(eq(salesDocuments.organizationId, organizationId), eq(salesDocuments.type, '請求書'), inArray(salesDocuments.status, paymentDocumentStatuses), isNull(salesDocuments.archivedAt))).orderBy(desc(salesDocuments.issuedAt)).all(),
    database.select().from(maintenanceDocuments).where(and(eq(maintenanceDocuments.organizationId, organizationId), eq(maintenanceDocuments.type, '整備請求書'), inArray(maintenanceDocuments.status, paymentDocumentStatuses), isNull(maintenanceDocuments.archivedAt))).orderBy(desc(maintenanceDocuments.issuedAt)).all(),
    database.select().from(paymentRecords).where(eq(paymentRecords.organizationId, organizationId)).orderBy(desc(paymentRecords.updatedAt)).all(),
    database.select().from(paymentEntries).where(eq(paymentEntries.organizationId, organizationId)).orderBy(desc(paymentEntries.paymentDate), desc(paymentEntries.createdAt)).all(),
    database.select().from(customers).where(eq(customers.organizationId, organizationId)).all(),
    database.select().from(vehicles).where(eq(vehicles.organizationId, organizationId)).all(),
  ])
  const paymentsByKey = new Map(paymentRows.map((payment) => [`${payment.documentType}:${payment.documentId}`, payment]))
  const entriesByKey = groupPaymentEntries(paymentEntryRows)
  const customersById = new Map(customerRows.map((customer) => [customer.id, customer]))
  const vehiclesById = new Map(vehicleRows.map((vehicle) => [vehicle.id, vehicle]))
  const records = [
    ...salesRows.map((document) => serializePayment('販売請求書', document, paymentsByKey.get(`販売請求書:${document.id}`), entriesByKey.get(`販売請求書:${document.id}`) ?? [], customersById, vehiclesById)),
    ...maintenanceRows.map((document) => serializePayment('整備請求書', document, paymentsByKey.get(`整備請求書:${document.id}`), entriesByKey.get(`整備請求書:${document.id}`) ?? [], customersById, vehiclesById)),
  ].sort((left, right) => right.issuedAt.localeCompare(left.issuedAt))
  return jsonResponse({ records }, 200, env)
}

async function addPaymentEntry(request: Request, env: Env, database: ReturnType<typeof createDatabase>, documentType: string, documentId: string, organizationId: string) {
  assertPaymentDocumentType(documentType)
  const invoice = await requireInvoice(database, documentType, documentId, organizationId)
  const input = readPaymentEntryInput(await readJson(request))
  const existingEntries = await ensurePaymentHistory(database, documentType, documentId, organizationId)
  assertWithinOutstanding(invoice.total, sumPaymentEntries(existingEntries), input.amount)
  const now = new Date().toISOString()
  await database.insert(paymentEntries).values({ id: crypto.randomUUID(), organizationId, documentType, documentId, amount: input.amount, paymentDate: input.paymentDate, method: input.method, note: input.note, createdAt: now, updatedAt: now }).run()
  await syncPaymentRecord(database, invoice, documentType, documentId, organizationId)
  return paymentResponse(env, database, documentType, documentId, organizationId, 201)
}

async function updatePaymentEntry(request: Request, env: Env, database: ReturnType<typeof createDatabase>, documentType: string, documentId: string, entryId: string, organizationId: string) {
  assertPaymentDocumentType(documentType)
  const invoice = await requireInvoice(database, documentType, documentId, organizationId)
  await ensurePaymentHistory(database, documentType, documentId, organizationId)
  const current = await database.select().from(paymentEntries).where(and(eq(paymentEntries.id, entryId), eq(paymentEntries.organizationId, organizationId), eq(paymentEntries.documentType, documentType), eq(paymentEntries.documentId, documentId))).get()
  if (!current) throw new HttpError(404, '入金履歴が見つかりません。')
  const input = readPaymentEntryInput(await readJson(request), current)
  const existingEntries = await loadRawPaymentEntries(database, documentType, documentId, organizationId)
  const otherPaidAmount = sumPaymentEntries(existingEntries) - current.amount
  assertWithinOutstanding(invoice.total, otherPaidAmount, input.amount)
  await database.update(paymentEntries).set({ amount: input.amount, paymentDate: input.paymentDate, method: input.method, note: input.note, updatedAt: new Date().toISOString() }).where(and(eq(paymentEntries.id, entryId), eq(paymentEntries.organizationId, organizationId))).run()
  await syncPaymentRecord(database, invoice, documentType, documentId, organizationId)
  return paymentResponse(env, database, documentType, documentId, organizationId, 200)
}

async function deletePaymentEntry(request: Request, env: Env, database: ReturnType<typeof createDatabase>, documentType: string, documentId: string, entryId: string, organizationId: string) {
  assertPaymentDocumentType(documentType)
  const invoice = await requireInvoice(database, documentType, documentId, organizationId)
  await ensurePaymentHistory(database, documentType, documentId, organizationId)
  const current = await database.select().from(paymentEntries).where(and(eq(paymentEntries.id, entryId), eq(paymentEntries.organizationId, organizationId), eq(paymentEntries.documentType, documentType), eq(paymentEntries.documentId, documentId))).get()
  if (!current) throw new HttpError(404, '入金履歴が見つかりません。')
  await database.delete(paymentEntries).where(and(eq(paymentEntries.id, entryId), eq(paymentEntries.organizationId, organizationId))).run()
  await syncPaymentRecord(database, invoice, documentType, documentId, organizationId)
  return paymentResponse(env, database, documentType, documentId, organizationId, 200)
}

// 旧画面・既存APIとの互換用。新しい画面は入金履歴APIを利用します。
async function replacePayment(request: Request, env: Env, database: ReturnType<typeof createDatabase>, documentType: string, documentId: string, organizationId: string) {
  assertPaymentDocumentType(documentType)
  const invoice = await requireInvoice(database, documentType, documentId, organizationId)
  const body = await readJson(request)
  const paidAmount = Math.min(invoice.total, Math.max(0, integerNumber(body.paidAmount, 0)))
  const paymentDate = nullableDate(body.paymentDate)
  const method = optionalPaymentMethod(body.method)
  const note = nullableString(body, 'note') ?? ''
  await database.delete(paymentEntries).where(and(eq(paymentEntries.organizationId, organizationId), eq(paymentEntries.documentType, documentType), eq(paymentEntries.documentId, documentId))).run()
  if (paidAmount > 0 || paymentDate || method || note) {
    const now = new Date().toISOString()
    await database.insert(paymentEntries).values({ id: crypto.randomUUID(), organizationId, documentType, documentId, amount: paidAmount, paymentDate, method, note, createdAt: now, updatedAt: now }).run()
  }
  await syncPaymentRecord(database, invoice, documentType, documentId, organizationId)
  return paymentResponse(env, database, documentType, documentId, organizationId, 200)
}

async function paymentResponse(env: Env, database: ReturnType<typeof createDatabase>, documentType: string, documentId: string, organizationId: string, status: number) {
  const record = await loadPaymentRecord(database, documentType, documentId, organizationId)
  return jsonResponse({ record }, status, env)
}

async function loadPaymentRecord(database: ReturnType<typeof createDatabase>, documentType: string, documentId: string, organizationId: string) {
  const invoice = await requireInvoice(database, documentType, documentId, organizationId)
  const [payment, entryRows, customerRows, vehicleRows] = await Promise.all([
    database.select().from(paymentRecords).where(and(eq(paymentRecords.organizationId, organizationId), eq(paymentRecords.documentType, documentType), eq(paymentRecords.documentId, documentId))).get(),
    database.select().from(paymentEntries).where(and(eq(paymentEntries.organizationId, organizationId), eq(paymentEntries.documentType, documentType), eq(paymentEntries.documentId, documentId))).orderBy(desc(paymentEntries.paymentDate), desc(paymentEntries.createdAt)).all(),
    database.select().from(customers).where(eq(customers.organizationId, organizationId)).all(),
    database.select().from(vehicles).where(eq(vehicles.organizationId, organizationId)).all(),
  ])
  return serializePayment(documentType as '販売請求書' | '整備請求書', invoice, payment, entryRows, new Map(customerRows.map((customer) => [customer.id, customer])), new Map(vehicleRows.map((vehicle) => [vehicle.id, vehicle])))
}

async function requireInvoice(database: ReturnType<typeof createDatabase>, documentType: string, documentId: string, organizationId: string) {
  const invoice = await findInvoice(database, documentType, documentId, organizationId)
  if (!invoice) throw new HttpError(404, '請求書が見つかりません。')
  return invoice
}

async function findInvoice(database: ReturnType<typeof createDatabase>, documentType: string, documentId: string, organizationId: string) {
  if (documentType === '販売請求書') return database.select().from(salesDocuments).where(and(eq(salesDocuments.id, documentId), eq(salesDocuments.organizationId, organizationId), eq(salesDocuments.type, '請求書'), inArray(salesDocuments.status, paymentDocumentStatuses))).get()
  return database.select().from(maintenanceDocuments).where(and(eq(maintenanceDocuments.id, documentId), eq(maintenanceDocuments.organizationId, organizationId), eq(maintenanceDocuments.type, '整備請求書'), inArray(maintenanceDocuments.status, paymentDocumentStatuses))).get()
}

async function loadRawPaymentEntries(database: ReturnType<typeof createDatabase>, documentType: string, documentId: string, organizationId: string) {
  return database.select().from(paymentEntries).where(and(eq(paymentEntries.organizationId, organizationId), eq(paymentEntries.documentType, documentType), eq(paymentEntries.documentId, documentId))).orderBy(desc(paymentEntries.paymentDate), desc(paymentEntries.createdAt)).all()
}

async function ensurePaymentHistory(database: ReturnType<typeof createDatabase>, documentType: string, documentId: string, organizationId: string) {
  const entries = await loadRawPaymentEntries(database, documentType, documentId, organizationId)
  if (entries.length) return entries
  const legacy = await database.select().from(paymentRecords).where(and(eq(paymentRecords.organizationId, organizationId), eq(paymentRecords.documentType, documentType), eq(paymentRecords.documentId, documentId))).get()
  if (!legacy || !hasPaymentActivity(legacy)) return []
  const entry = legacyEntry(legacy)
  await database.insert(paymentEntries).values({ id: entry.id, organizationId, documentType, documentId, amount: entry.amount, paymentDate: entry.paymentDate, method: entry.method, note: entry.note, createdAt: entry.createdAt, updatedAt: entry.updatedAt }).run()
  return [entry]
}

async function syncPaymentRecord(database: ReturnType<typeof createDatabase>, invoice: InvoiceRow, documentType: string, documentId: string, organizationId: string) {
  const entries = await loadRawPaymentEntries(database, documentType, documentId, organizationId)
  const paidAmount = sumPaymentEntries(entries)
  const latest = entries.slice().sort(comparePaymentEntries)[0]
  const current = await database.select().from(paymentRecords).where(and(eq(paymentRecords.organizationId, organizationId), eq(paymentRecords.documentType, documentType), eq(paymentRecords.documentId, documentId))).get()
  const data = { invoiceAmount: invoice.total, paidAmount, paymentDate: latest?.paymentDate ?? null, method: latest?.method ?? null, note: latest?.note ?? null, updatedAt: new Date().toISOString() }
  if (current) await database.update(paymentRecords).set(data).where(and(eq(paymentRecords.id, current.id), eq(paymentRecords.organizationId, organizationId))).run()
  else await database.insert(paymentRecords).values({ id: crypto.randomUUID(), organizationId, documentType, documentId, ...data }).run()
}

function serializePayment(documentType: '販売請求書' | '整備請求書', document: InvoiceRow, payment: typeof paymentRecords.$inferSelect | undefined, entries: Array<typeof paymentEntries.$inferSelect>, customersById: Map<string, typeof customers.$inferSelect>, vehiclesById: Map<string, typeof vehicles.$inferSelect>) {
  const history = entries.length ? entries : payment && hasPaymentActivity(payment) ? [legacyEntry(payment)] : []
  const sortedHistory = history.slice().sort(comparePaymentEntries)
  const latest = sortedHistory[0]
  const customer = customersById.get(document.customerId)
  const vehicle = document.vehicleId ? vehiclesById.get(document.vehicleId) : undefined
  return {
    id: payment?.id ?? `${documentType}:${document.id}`,
    documentType,
    documentId: document.id,
    number: document.number,
    sourceType: documentType,
    documentStatus: document.status,
    customerName: customer?.name ?? '',
    phone: customer?.phone ?? '',
    vehicle: vehicle ? [vehicle.maker, vehicle.name].filter(Boolean).join(' ') : '',
    plate: vehicle?.registrationNumber ?? '',
    issuedAt: document.issuedAt,
    dueDate: document.dueDate,
    invoiceAmount: document.total,
    paidAmount: sumPaymentEntries(history),
    paymentDate: latest?.paymentDate ?? null,
    method: latest?.method ?? null,
    note: latest?.note ?? '',
    paymentHistory: sortedHistory.map((entry) => ({ id: entry.id, amount: entry.amount, paymentDate: entry.paymentDate, method: entry.method, note: entry.note, createdAt: entry.createdAt, updatedAt: entry.updatedAt })),
  }
}

function groupPaymentEntries(entries: Array<typeof paymentEntries.$inferSelect>) {
  const grouped = new Map<string, Array<typeof paymentEntries.$inferSelect>>()
  for (const entry of entries) grouped.set(`${entry.documentType}:${entry.documentId}`, [...(grouped.get(`${entry.documentType}:${entry.documentId}`) ?? []), entry])
  return grouped
}

function readPaymentEntryInput(body: Record<string, unknown>, fallback?: typeof paymentEntries.$inferSelect) {
  const amount = body.amount === undefined && fallback ? fallback.amount : Math.max(0, integerNumber(body.amount, 0))
  const paymentDate = body.paymentDate === undefined && fallback ? fallback.paymentDate : nullableDate(body.paymentDate)
  const method = body.method === undefined && fallback ? fallback.method : optionalPaymentMethod(body.method)
  const note = body.note === undefined && fallback ? fallback.note : nullableString(body, 'note') ?? ''
  if (amount <= 0) throw new HttpError(400, '入金額は1円以上で入力してください。')
  if (!paymentDate) throw new HttpError(400, '入金日を入力してください。')
  if (!method) throw new HttpError(400, '入金方法を選択してください。')
  return { amount, paymentDate, method, note }
}

function assertWithinOutstanding(invoiceAmount: number, currentPaidAmount: number, amount: number) {
  if (currentPaidAmount + amount > invoiceAmount) throw new HttpError(400, '入金額が請求残額を超えています。')
}

function assertPaymentDocumentType(documentType: string): asserts documentType is '販売請求書' | '整備請求書' {
  if (!paymentDocumentTypes.has(documentType)) throw new HttpError(400, '請求書種別が不正です。')
}

function hasPaymentActivity(payment: typeof paymentRecords.$inferSelect) { return payment.paidAmount > 0 || Boolean(payment.paymentDate || payment.method || payment.note) }
function legacyEntry(payment: typeof paymentRecords.$inferSelect) { return { id: `legacy-${payment.id}`, amount: payment.paidAmount, paymentDate: payment.paymentDate, method: payment.method, note: payment.note ?? '', createdAt: payment.createdAt, updatedAt: payment.updatedAt } }
function sumPaymentEntries(entries: Array<{ amount: number }>) { return entries.reduce((sum, entry) => sum + Math.max(0, entry.amount), 0) }
function comparePaymentEntries(left: { paymentDate: string | null; createdAt: string }, right: { paymentDate: string | null; createdAt: string }) { return (right.paymentDate ?? '').localeCompare(left.paymentDate ?? '') || right.createdAt.localeCompare(left.createdAt) }
function optionalPaymentMethod(value: unknown) { return typeof value === 'string' && paymentMethods.has(value) ? value : null }
function stringValue(body: Record<string, unknown>, key: string) { return typeof body[key] === 'string' ? body[key].trim() : '' }
function nullableString(body: Record<string, unknown>, key: string) { const value = stringValue(body, key); return value || null }
function nullableDate(value: unknown) { return typeof value === 'string' && /^\d{4}[-/]\d{2}[-/]\d{2}$/.test(value.trim()) ? value.trim().replaceAll('/', '-') : null }
function integerNumber(value: unknown, fallback: number) { const number = typeof value === 'number' ? value : Number(value); return Number.isFinite(number) ? Math.round(number) : fallback }

type InvoiceRow = typeof salesDocuments.$inferSelect | typeof maintenanceDocuments.$inferSelect
