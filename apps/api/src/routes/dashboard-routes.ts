import { and, desc, eq, isNull } from 'drizzle-orm'
import { customers, maintenanceDocuments, paymentRecords, salesDocuments, vehicles } from '@vehicle-management/database'
import { UnauthorizedError } from '../auth/firebase'
import { requireOrganizationContext } from '../auth/organization'
import { createDatabase } from '../db/client'
import { HttpError, jsonResponse } from '../http'

export async function handleDashboardRoutes(request: Request, env: Env): Promise<Response | null> {
  const pathname = new URL(request.url).pathname.replace(/\/$/, '') || '/'
  if (pathname !== '/api/dashboard') return null

  try {
    if (request.method !== 'GET') throw new HttpError(405, 'この操作には対応していません。')
    const database = createDatabase(env.DB)
    const context = await requireOrganizationContext(request, env, database)
    return jsonResponse({ dashboard: await loadDashboard(database, context.organization.organizationId) }, 200, env)
  } catch (error) {
    if (error instanceof UnauthorizedError) return jsonResponse({ error: error.message }, 401, env)
    if (error instanceof HttpError) return jsonResponse({ error: error.message }, error.status, env)
    console.error(error)
    return jsonResponse({ error: 'ダッシュボードの集計に失敗しました。' }, 500, env)
  }
}

async function loadDashboard(database: ReturnType<typeof createDatabase>, organizationId: string) {
  const [customerRows, vehicleRows, salesRows, maintenanceRows, paymentRows] = await Promise.all([
    database.select().from(customers).where(eq(customers.organizationId, organizationId)).all(),
    database.select().from(vehicles).where(eq(vehicles.organizationId, organizationId)).all(),
    database.select().from(salesDocuments).where(and(eq(salesDocuments.organizationId, organizationId), isNull(salesDocuments.archivedAt))).orderBy(desc(salesDocuments.updatedAt)).all(),
    database.select().from(maintenanceDocuments).where(and(eq(maintenanceDocuments.organizationId, organizationId), isNull(maintenanceDocuments.archivedAt))).orderBy(desc(maintenanceDocuments.updatedAt)).all(),
    database.select().from(paymentRecords).where(eq(paymentRecords.organizationId, organizationId)).orderBy(desc(paymentRecords.updatedAt)).all(),
  ])
  const customersById = new Map(customerRows.map((customer) => [customer.id, customer]))
  const vehiclesById = new Map(vehicleRows.map((vehicle) => [vehicle.id, vehicle]))
  const today = startOfDay(new Date())
  const inspections = buildInspectionRows(vehicleRows, customersById, today)
  const unpaidInvoices = buildUnpaidInvoices(salesRows, maintenanceRows, paymentRows, customersById, vehiclesById, today)
  const monthlySales = sumMonthlySales(salesRows, maintenanceRows, today)

  return {
    summary: {
      registeredVehicles: vehicleRows.length,
      monthlySales,
      inspectionsWithin30Days: inspections.length,
      overdueInspections: inspections.filter((inspection) => inspection.tone === 'danger').length,
      unpaidInvoices: unpaidInvoices.length,
      unpaidAmount: unpaidInvoices.reduce((sum, invoice) => sum + invoice.amount, 0),
    },
    inspections,
    unpaidInvoices,
    recentActivities: buildRecentActivities(salesRows, vehicleRows, paymentRows, customersById, vehiclesById),
  }
}

function buildInspectionRows(
  vehicleRows: Array<typeof vehicles.$inferSelect>,
  customersById: Map<string, typeof customers.$inferSelect>,
  today: Date,
) {
  return vehicleRows.map((vehicle) => {
    const date = parseDate(vehicle.inspectionDate)
    if (!date) return null
    const diffDays = daysBetween(today, date)
    const tone = diffDays < 0 ? 'danger' : diffDays <= 30 ? 'warning' : 'normal'
    return {
      customer: customersById.get(vehicle.customerId)?.name ?? '顧客未登録',
      vehicle: [vehicle.maker, vehicle.name].filter(Boolean).join(' '),
      plate: vehicle.registrationNumber ?? '',
      date: formatDate(date),
      tone,
      diffDays,
    }
  }).filter((inspection): inspection is NonNullable<typeof inspection> => inspection !== null && inspection.diffDays <= 30).sort((left, right) => left.diffDays - right.diffDays).slice(0, 5).map(({ diffDays: _diffDays, ...inspection }) => inspection)
}

function buildUnpaidInvoices(
  salesRows: Array<typeof salesDocuments.$inferSelect>,
  maintenanceRows: Array<typeof maintenanceDocuments.$inferSelect>,
  paymentRows: Array<typeof paymentRecords.$inferSelect>,
  customersById: Map<string, typeof customers.$inferSelect>,
  vehiclesById: Map<string, typeof vehicles.$inferSelect>,
  today: Date,
) {
  const invoices = [
    ...salesRows.filter((document) => document.type === '請求書' || document.status === '入金待ち').map((document) => ({
      source: '販売請求書',
      number: document.number,
      customerId: document.customerId,
      vehicleId: document.vehicleId,
      total: document.total,
      dueDate: document.dueDate,
      documentId: document.id,
    })),
    ...maintenanceRows.filter((document) => document.status === '入金待ち' || paymentRows.some((payment) => payment.documentId === document.id)).map((document) => ({
      source: '整備請求書',
      number: document.number,
      customerId: document.customerId,
      vehicleId: document.vehicleId,
      total: document.total,
      dueDate: document.dueDate,
      documentId: document.id,
    })),
  ]

  return invoices.map((invoice) => {
    const payment = paymentRows.find((row) => row.documentId === invoice.documentId)
    const amount = Math.max(0, invoice.total - (payment?.paidAmount ?? 0))
    const dueDate = parseDate(invoice.dueDate)
    const diffDays = dueDate ? daysBetween(today, dueDate) : null
    return {
      customer: customersById.get(invoice.customerId)?.name ?? '顧客未登録',
      document: `${invoice.source} #${invoice.number}`,
      vehicle: invoice.vehicleId ? [vehiclesById.get(invoice.vehicleId)?.maker, vehiclesById.get(invoice.vehicleId)?.name].filter(Boolean).join(' ') : '',
      amount,
      due: diffDays === null ? '期限未設定' : diffDays < 0 ? '期限超過' : `期限まで${diffDays}日`,
      tone: diffDays !== null && diffDays < 0 ? 'danger' : diffDays !== null && diffDays <= 7 ? 'warning' : 'normal',
      dueDate: dueDate ? dueDate.getTime() : Number.MAX_SAFE_INTEGER,
    }
  }).filter((invoice) => invoice.amount > 0).sort((left, right) => left.dueDate - right.dueDate).slice(0, 5).map(({ dueDate: _dueDate, ...invoice }) => invoice)
}

function buildRecentActivities(
  salesRows: Array<typeof salesDocuments.$inferSelect>,
  vehicleRows: Array<typeof vehicles.$inferSelect>,
  paymentRows: Array<typeof paymentRecords.$inferSelect>,
  customersById: Map<string, typeof customers.$inferSelect>,
  vehiclesById: Map<string, typeof vehicles.$inferSelect>,
) {
  const salesActivities = salesRows.slice(0, 5).map((document) => ({
    kind: 'sales' as const,
    label: `${document.type}を作成`,
    detail: `${customersById.get(document.customerId)?.name ?? '顧客未登録'}・${vehicleLabel(document.vehicleId, vehiclesById)}`,
    at: document.updatedAt,
  }))
  const vehicleActivities = vehicleRows.slice().sort((left, right) => right.updatedAt.localeCompare(left.updatedAt)).slice(0, 5).map((vehicle) => ({
    kind: 'vehicle' as const,
    label: '車両情報を更新',
    detail: `${customersById.get(vehicle.customerId)?.name ?? '顧客未登録'}・${vehicleLabel(vehicle.id, vehiclesById)}`,
    at: vehicle.updatedAt,
  }))
  const paymentActivities = paymentRows.filter((payment) => payment.paidAmount > 0).slice(0, 5).map((payment) => ({
    kind: 'payment' as const,
    label: '入金を登録',
    detail: `${payment.documentType}・¥${new Intl.NumberFormat('ja-JP').format(payment.paidAmount)}`,
    at: payment.updatedAt,
  }))
  return [...salesActivities, ...vehicleActivities, ...paymentActivities].sort((left, right) => timestamp(right.at) - timestamp(left.at)).slice(0, 5)
}

function sumMonthlySales(
  salesRows: Array<typeof salesDocuments.$inferSelect>,
  maintenanceRows: Array<typeof maintenanceDocuments.$inferSelect>,
  today: Date,
) {
  const prefix = `${today.getFullYear()}-${String(today.getMonth() + 1).padStart(2, '0')}`
  const salesTotal = salesRows.filter((document) => document.status !== '下書き' && normalizeDate(document.issuedAt).startsWith(prefix)).reduce((sum, document) => sum + document.total, 0)
  const maintenanceTotal = maintenanceRows.filter((document) => document.status !== '下書き' && normalizeDate(document.issuedAt).startsWith(prefix)).reduce((sum, document) => sum + document.total, 0)
  return salesTotal + maintenanceTotal
}

function vehicleLabel(vehicleId: string | null, vehiclesById: Map<string, typeof vehicles.$inferSelect>) {
  if (!vehicleId) return '車両未指定'
  const vehicle = vehiclesById.get(vehicleId)
  return vehicle ? [vehicle.maker, vehicle.name].filter(Boolean).join(' ') : '車両未登録'
}

function parseDate(value: string | null) {
  if (!value) return null
  const normalized = normalizeDate(value)
  if (!/^\d{4}-\d{2}-\d{2}$/.test(normalized)) return null
  const date = new Date(`${normalized}T00:00:00`)
  return Number.isNaN(date.getTime()) ? null : date
}

function normalizeDate(value: string) {
  return value.slice(0, 10).replaceAll('/', '-')
}

function formatDate(value: Date) {
  return `${value.getFullYear()}/${String(value.getMonth() + 1).padStart(2, '0')}/${String(value.getDate()).padStart(2, '0')}`
}

function startOfDay(value: Date) {
  return new Date(value.getFullYear(), value.getMonth(), value.getDate())
}

function daysBetween(from: Date, to: Date) {
  return Math.floor((to.getTime() - from.getTime()) / 86_400_000)
}

function timestamp(value: string) {
  const time = Date.parse(value.replace(' ', 'T') + (value.includes('Z') ? '' : 'Z'))
  return Number.isNaN(time) ? 0 : time
}
