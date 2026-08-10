import { and, asc, desc, eq } from 'drizzle-orm'
import { customers, maintenanceItems, maintenanceDocuments, paymentRecords, salesDocumentItems, salesDocuments, vehicles } from '@vehicle-management/database'
import { UnauthorizedError } from '../auth/firebase'
import { requireOrganizationPermission } from '../auth/organization'
import { createDatabase } from '../db/client'
import { HttpError, corsHeaders } from '../http'

const resources = new Set(['customers', 'vehicles', 'sales', 'maintenance', 'payments'])

export async function handleExportRoutes(request: Request, env: Env): Promise<Response | null> {
  const pathname = new URL(request.url).pathname.replace(/\/$/, '') || '/'
  const match = pathname.match(/^\/api\/export\/([^/]+)$/)
  if (!match) return null

  try {
    if (request.method !== 'GET') throw new HttpError(405, 'この操作には対応していません。')
    const resource = match[1]
    if (!resources.has(resource)) throw new HttpError(404, '出力対象が見つかりません。')
    const database = createDatabase(env.DB)
    const context = await requireOrganizationPermission(request, env, database, 'employeeCanExportCsv')
    const organizationId = context.organization.organizationId
    if (resource === 'customers') return await exportCustomers(env, database, organizationId)
    if (resource === 'vehicles') return await exportVehicles(env, database, organizationId)
    if (resource === 'sales') return await exportSales(env, database, organizationId)
    if (resource === 'maintenance') return await exportMaintenance(env, database, organizationId)
    return await exportPayments(env, database, organizationId)
  } catch (error) {
    if (error instanceof UnauthorizedError) return jsonError(error.message, 401, env)
    if (error instanceof HttpError) return jsonError(error.message, error.status, env)
    console.error(error)
    return jsonError('CSV出力に失敗しました。', 500, env)
  }
}

async function exportCustomers(env: Env, database: ReturnType<typeof createDatabase>, organizationId: string) {
  const [rows, vehicleRows] = await Promise.all([database.select().from(customers).where(eq(customers.organizationId, organizationId)).orderBy(asc(customers.name)).all(), database.select({ customerId: vehicles.customerId }).from(vehicles).where(eq(vehicles.organizationId, organizationId)).all()])
  const vehicleCounts = new Map<string, number>()
  for (const vehicle of vehicleRows) vehicleCounts.set(vehicle.customerId, (vehicleCounts.get(vehicle.customerId) ?? 0) + 1)
  return csvResponse(['顧客ID', '顧客番号', '顧客名', 'ふりがな', '電話番号', 'メールアドレス', '郵便番号', '住所', 'メモ', '車両台数'], rows.map((row) => [row.id, row.customerNumber, row.name, row.nameKana, row.phone, row.email, row.postalCode, row.address, row.memo, vehicleCounts.get(row.id) ?? 0]), 'customers.csv', env)
}

async function exportVehicles(env: Env, database: ReturnType<typeof createDatabase>, organizationId: string) {
  const [vehicleRows, customerRows] = await Promise.all([database.select().from(vehicles).where(eq(vehicles.organizationId, organizationId)).orderBy(asc(vehicles.name)).all(), database.select().from(customers).where(eq(customers.organizationId, organizationId)).all()])
  const customersById = new Map(customerRows.map((customer) => [customer.id, customer]))
  return csvResponse(['車両ID', '顧客ID', '顧客名', 'メーカー', '車名', '型式', '登録番号', '車台番号', '年式', '車検満了日', '走行距離', '車体色', '排気量', 'ミッション', '記録簿', '備考'], vehicleRows.map((row) => [row.id, row.customerId, customersById.get(row.customerId)?.name, row.maker, row.name, row.model, row.registrationNumber, row.chassisNumber, row.modelYear, row.inspectionDate, row.mileage, row.bodyColor, row.displacement, row.transmission, row.inspectionRecordAvailable ? 'あり' : 'なし', row.memo]), 'vehicles.csv', env)
}

async function exportSales(env: Env, database: ReturnType<typeof createDatabase>, organizationId: string) {
  const [documentRows, itemRows, customerRows, vehicleRows] = await Promise.all([database.select().from(salesDocuments).where(eq(salesDocuments.organizationId, organizationId)).orderBy(desc(salesDocuments.issuedAt)).all(), database.select().from(salesDocumentItems).where(eq(salesDocumentItems.organizationId, organizationId)).orderBy(asc(salesDocumentItems.sortOrder)).all(), database.select().from(customers).where(eq(customers.organizationId, organizationId)).all(), database.select().from(vehicles).where(eq(vehicles.organizationId, organizationId)).all()])
  const itemsByDocument = groupBy(itemRows, (item) => item.documentId)
  const customersById = new Map(customerRows.map((customer) => [customer.id, customer]))
  const vehiclesById = new Map(vehicleRows.map((vehicle) => [vehicle.id, vehicle]))
  return csvResponse(['書類ID', '書類番号', '書類種別', 'ステータス', '顧客名', '車名', '登録番号', '発行日', '支払期限', '税率', '小計', '消費税', '合計', '明細', '明細詳細', '帳票詳細'], documentRows.map((row) => {
    const items = itemsByDocument.get(row.id) ?? []
    return [row.id, row.number, row.type, row.status, customersById.get(row.customerId)?.name, vehicleLabel(row.vehicleId, vehiclesById), plateLabel(row.vehicleId, vehiclesById), row.issuedAt, row.dueDate, `${row.taxRate}%`, row.subtotal, row.tax, row.total, items.map((item) => `${item.description} x${item.quantity} ${item.unit} ¥${item.amount}`).join(' / '), JSON.stringify(items.map(({ itemType, description, quantity, unit, unitPrice, taxCategory, otherAmount, summary }) => ({ itemType, description, quantity, unit, unitPrice, taxCategory, otherAmount, summary }))), row.detailsJson]
  }), 'sales.csv', env)
}

async function exportMaintenance(env: Env, database: ReturnType<typeof createDatabase>, organizationId: string) {
  const [documentRows, itemRows, customerRows, vehicleRows] = await Promise.all([database.select().from(maintenanceDocuments).where(eq(maintenanceDocuments.organizationId, organizationId)).orderBy(desc(maintenanceDocuments.issuedAt)).all(), database.select().from(maintenanceItems).where(eq(maintenanceItems.organizationId, organizationId)).orderBy(asc(maintenanceItems.sortOrder)).all(), database.select().from(customers).where(eq(customers.organizationId, organizationId)).all(), database.select().from(vehicles).where(eq(vehicles.organizationId, organizationId)).all()])
  const itemsByDocument = groupBy(itemRows, (item) => item.documentId)
  const customersById = new Map(customerRows.map((customer) => [customer.id, customer]))
  const vehiclesById = new Map(vehicleRows.map((vehicle) => [vehicle.id, vehicle]))
  return csvResponse(['書類ID', '書類番号', '書類種別', '入庫区分', 'ステータス', '顧客名', '車名', '登録番号', '入庫日', '出庫予定日', '支払期限', '税率', '小計', '消費税', '合計', '明細'], documentRows.map((row) => [row.id, row.number, row.type, row.category, row.status, customersById.get(row.customerId)?.name, vehicleLabel(row.vehicleId, vehiclesById), plateLabel(row.vehicleId, vehiclesById), row.intakeDate, row.completionDate, row.dueDate, `${row.taxRate}%`, row.subtotal, row.tax, row.total, (itemsByDocument.get(row.id) ?? []).map((item) => `${item.itemType}:${item.description} x${item.quantity} ${item.unit} ¥${item.amount}`).join(' / ')]), 'maintenance.csv', env)
}

async function exportPayments(env: Env, database: ReturnType<typeof createDatabase>, organizationId: string) {
  const [salesRows, maintenanceRows, paymentRows, customerRows, vehicleRows] = await Promise.all([database.select().from(salesDocuments).where(and(eq(salesDocuments.organizationId, organizationId), eq(salesDocuments.type, '請求書'))).orderBy(desc(salesDocuments.issuedAt)).all(), database.select().from(maintenanceDocuments).where(and(eq(maintenanceDocuments.organizationId, organizationId), eq(maintenanceDocuments.type, '整備請求書'))).orderBy(desc(maintenanceDocuments.issuedAt)).all(), database.select().from(paymentRecords).where(eq(paymentRecords.organizationId, organizationId)).all(), database.select().from(customers).where(eq(customers.organizationId, organizationId)).all(), database.select().from(vehicles).where(eq(vehicles.organizationId, organizationId)).all()])
  const paymentsByKey = new Map(paymentRows.map((row) => [`${row.documentType}:${row.documentId}`, row]))
  const customersById = new Map(customerRows.map((customer) => [customer.id, customer]))
  const vehiclesById = new Map(vehicleRows.map((vehicle) => [vehicle.id, vehicle]))
  const invoices = [...salesRows.map((row) => ({ type: '販売請求書', row })), ...maintenanceRows.map((row) => ({ type: '整備請求書', row }))]
  return csvResponse(['請求書ID', '請求書種別', '請求書番号', '顧客名', '車名', '登録番号', '発行日', '支払期限', '請求金額', '入金済み', '未入金', '入金日', '入金方法', 'メモ'], invoices.map(({ type, row }) => { const payment = paymentsByKey.get(`${type}:${row.id}`); return [row.id, type, row.number, customersById.get(row.customerId)?.name, vehicleLabel(row.vehicleId, vehiclesById), plateLabel(row.vehicleId, vehiclesById), row.issuedAt, row.dueDate, row.total, payment?.paidAmount ?? 0, Math.max(row.total - (payment?.paidAmount ?? 0), 0), payment?.paymentDate, payment?.method, payment?.note] }), 'payments.csv', env)
}

function csvResponse(headers: string[], rows: unknown[][], filename: string, env: Env) {
  const lines = [headers, ...rows].map((row) => row.map(csvValue).join(',')).join('\r\n')
  const responseHeaders = new Headers(corsHeaders(env))
  responseHeaders.set('Content-Type', 'text/csv; charset=utf-8')
  responseHeaders.set('Content-Disposition', `attachment; filename*=UTF-8''${encodeURIComponent(filename)}`)
  return new Response(`\uFEFF${lines}\r\n`, { status: 200, headers: responseHeaders })
}

function csvValue(value: unknown) {
  const rawText = value === null || value === undefined ? '' : String(value)
  const text = typeof value === 'string' && /^[\t\r ]*[=+\-@]/u.test(rawText) ? `'${rawText}` : rawText
  return /[",\r\n]/.test(text) ? `"${text.replaceAll('"', '""')}"` : text
}

function jsonError(message: string, status: number, env: Env) {
  return Response.json({ error: message }, { status, headers: corsHeaders(env) })
}

function groupBy<T>(items: T[], getKey: (item: T) => string) {
  const grouped = new Map<string, T[]>()
  for (const item of items) grouped.set(getKey(item), [...(grouped.get(getKey(item)) ?? []), item])
  return grouped
}

function vehicleLabel(vehicleId: string | null, vehiclesById: Map<string, typeof vehicles.$inferSelect>) { const vehicle = vehicleId ? vehiclesById.get(vehicleId) : undefined; return vehicle ? [vehicle.maker, vehicle.name].filter(Boolean).join(' ') : '' }
function plateLabel(vehicleId: string | null, vehiclesById: Map<string, typeof vehicles.$inferSelect>) { return vehicleId ? vehiclesById.get(vehicleId)?.registrationNumber ?? '' : '' }
