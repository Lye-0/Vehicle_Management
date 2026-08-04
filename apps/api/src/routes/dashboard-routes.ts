import { and, desc, eq, isNull } from 'drizzle-orm'
import { customers, inspectionSchedules, maintenanceDocuments, paymentRecords, salesDocuments, sharedSchedules, staffProfiles, vehicles } from '@vehicle-management/database'
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
  const [customerRows, vehicleRows, salesRows, maintenanceRows, paymentRows, scheduleRows, sharedScheduleRows, staffProfileRows] = await Promise.all([
    database.select().from(customers).where(eq(customers.organizationId, organizationId)).all(),
    database.select().from(vehicles).where(eq(vehicles.organizationId, organizationId)).all(),
    database.select().from(salesDocuments).where(and(eq(salesDocuments.organizationId, organizationId), isNull(salesDocuments.archivedAt))).orderBy(desc(salesDocuments.updatedAt)).all(),
    database.select().from(maintenanceDocuments).where(and(eq(maintenanceDocuments.organizationId, organizationId), isNull(maintenanceDocuments.archivedAt))).orderBy(desc(maintenanceDocuments.updatedAt)).all(),
    database.select().from(paymentRecords).where(eq(paymentRecords.organizationId, organizationId)).orderBy(desc(paymentRecords.updatedAt)).all(),
    database.select().from(inspectionSchedules).where(eq(inspectionSchedules.organizationId, organizationId)).orderBy(desc(inspectionSchedules.dueDate)).all(),
    database.select().from(sharedSchedules).where(eq(sharedSchedules.organizationId, organizationId)).orderBy(desc(sharedSchedules.startDate)).all(),
    database.select().from(staffProfiles).all(),
  ])
  const customersById = new Map(customerRows.map((customer) => [customer.id, customer]))
  const vehiclesById = new Map(vehicleRows.map((vehicle) => [vehicle.id, vehicle]))
  const today = startOfDay(new Date())
  const inspections = buildInspectionRows(vehicleRows, customersById, today)
  const upcomingIntakeVehicles = buildMaintenanceDateRows(maintenanceRows, customersById, vehiclesById, today, 'intakeDate')
  const upcomingReleaseVehicles = buildMaintenanceDateRows(maintenanceRows, customersById, vehiclesById, today, 'plannedReleaseDate')
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
    upcomingIntakeVehicles,
    upcomingReleaseVehicles,
    unpaidInvoices,
    recentActivities: buildRecentActivities(salesRows, vehicleRows, paymentRows, customersById, vehiclesById),
    calendarEvents: buildCalendarEvents(vehicleRows, scheduleRows, salesRows, maintenanceRows, paymentRows, sharedScheduleRows, staffProfileRows, customersById, vehiclesById),
  }
}

type CalendarEventCategory = 'vehicle-inspection' | 'inspection' | 'maintenance' | 'sales' | 'payment-due' | 'payment' | 'shared'
type CalendarEventNavigation =
  | { section: 'customers'; customerId: string; vehicleId: string }
  | { section: 'sales' | 'maintenance' | 'payments'; recordId: string }

type CalendarEvent = {
  id: string
  date: string
  category: CalendarEventCategory
  categoryLabel: string
  title: string
  detail: string
  status: string | null
  amount: number | null
  endDate: string
  navigation?: CalendarEventNavigation
}

const calendarEventLabels: Record<CalendarEventCategory, string> = {
  'vehicle-inspection': '車検満了',
  inspection: '車検',
  maintenance: '整備書類作成日',
  sales: '販売書類作成日',
  'payment-due': '支払期限',
  payment: '入金',
  shared: '組織内共有スケジュール',
}

function buildCalendarEvents(
  vehicleRows: Array<typeof vehicles.$inferSelect>,
  scheduleRows: Array<typeof inspectionSchedules.$inferSelect>,
  salesRows: Array<typeof salesDocuments.$inferSelect>,
  maintenanceRows: Array<typeof maintenanceDocuments.$inferSelect>,
  paymentRows: Array<typeof paymentRecords.$inferSelect>,
  sharedScheduleRows: Array<typeof sharedSchedules.$inferSelect>,
  staffProfileRows: Array<typeof staffProfiles.$inferSelect>,
  customersById: Map<string, typeof customers.$inferSelect>,
  vehiclesById: Map<string, typeof vehicles.$inferSelect>,
) {
  const events: CalendarEvent[] = []
  const salesById = new Map(salesRows.map((document) => [document.id, document]))
  const maintenanceById = new Map(maintenanceRows.map((document) => [document.id, document]))
  const staffProfilesByUid = new Map(staffProfileRows.map((profile) => [profile.uid, profile.displayName]))

  for (const vehicle of vehicleRows) {
    const customer = customersById.get(vehicle.customerId)?.name ?? '顧客未登録'
    addCalendarEvent(events, vehicle.inspectionDate, `vehicle-${vehicle.id}-inspection`, 'vehicle-inspection', customerVehicleLabel(customer, vehicleLabel(vehicle.id, vehiclesById)), '車検満了日', '車検満了', null, { section: 'customers', customerId: vehicle.customerId, vehicleId: vehicle.id })
  }

  for (const schedule of scheduleRows) {
    const customer = customersById.get(schedule.customerId)?.name ?? '顧客未登録'
    addCalendarEvent(events, schedule.dueDate, `inspection-schedule-${schedule.id}`, 'inspection', customerVehicleLabel(customer, vehicleLabel(schedule.vehicleId, vehiclesById)), schedule.inspectionType, schedule.status, null, { section: 'customers', customerId: schedule.customerId, vehicleId: schedule.vehicleId })
  }

  for (const document of salesRows) {
    const customer = customersById.get(document.customerId)?.name ?? '顧客未登録'
    const vehicle = vehicleLabel(document.vehicleId, vehiclesById)
    addCalendarEvent(events, document.issuedAt, `sales-${document.id}-issued`, 'sales', `${document.type}：${customer}`, `${vehicle} ・ #${document.number}`, document.status, document.total, { section: 'sales', recordId: document.id })
    addCalendarEvent(events, document.dueDate, `sales-${document.id}-due`, 'payment-due', `支払期限：${customer}`, `${document.type} ・ #${document.number}`, document.status, document.total, { section: 'sales', recordId: document.id })
  }

  for (const document of maintenanceRows) {
    const customer = customersById.get(document.customerId)?.name ?? '顧客未登録'
    const vehicle = vehicleLabel(document.vehicleId, vehiclesById)
    const documentLabel = `${document.type} ・ #${document.number}`
    addCalendarEvent(events, document.issuedAt, `maintenance-${document.id}-issued`, 'maintenance', `整備書類：${customer}`, documentLabel, document.status, document.total, { section: 'maintenance', recordId: document.id })
    if (document.category === '車検') {
      addCalendarEvent(events, document.intakeDate, `inspection-document-${document.id}`, 'inspection', customerVehicleLabel(customer, vehicle), documentLabel, document.status, null, { section: 'maintenance', recordId: document.id }, document.plannedReleaseDate ?? document.completionDate ?? document.intakeDate)
    }
    addCalendarEvent(events, document.dueDate, `maintenance-${document.id}-due`, 'payment-due', `支払期限：${customer}`, documentLabel, document.status, document.total, { section: 'maintenance', recordId: document.id })
  }

  for (const payment of paymentRows) {
    if (!payment.paymentDate || payment.paidAmount <= 0) continue
    const document = payment.documentType === '販売請求書' ? salesById.get(payment.documentId) : maintenanceById.get(payment.documentId)
    if (!document) continue
    const customer = customersById.get(document.customerId)?.name ?? '顧客未登録'
    const vehicle = vehicleLabel(document.vehicleId, vehiclesById)
    const method = payment.method ? `・${payment.method}` : ''
    addCalendarEvent(events, payment.paymentDate, `payment-${payment.id}`, 'payment', `入金：${customer}`, `${payment.documentType} ・ ${vehicle}${method}`, '入金済み', payment.paidAmount, { section: 'payments', recordId: payment.id })
  }

  for (const schedule of sharedScheduleRows) {
    const authorName = staffProfilesByUid.get(schedule.createdByUid) ?? '未設定ユーザー'
    addCalendarEvent(events, schedule.startDate, `shared-${schedule.id}`, 'shared', schedule.title, schedule.detail, null, null, undefined, schedule.endDate, authorName)
  }

  return events.sort((left, right) => left.date.localeCompare(right.date) || left.title.localeCompare(right.title, 'ja'))
}

function addCalendarEvent(
  events: CalendarEvent[],
  date: string | null,
  id: string,
  category: CalendarEventCategory,
  title: string,
  detail: string,
  status: string | null,
  amount: number | null = null,
  navigation?: CalendarEventNavigation,
  endDate: string | null = null,
  authorName: string | null = null,
) {
  const normalizedDate = date ? normalizeDate(date) : ''
  if (!isCalendarDate(normalizedDate)) return
  const normalizedEndDate = endDate ? normalizeDate(endDate) : normalizedDate
  const safeEndDate = isCalendarDate(normalizedEndDate) && normalizedEndDate >= normalizedDate ? normalizedEndDate : normalizedDate
  events.push({ id, date: normalizedDate, category, categoryLabel: calendarEventLabels[category], title, detail, status, amount, endDate: safeEndDate, navigation, ...(authorName?.trim() ? { authorName: authorName.trim() } : {}) })
}

function isCalendarDate(value: string) {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(value)) return false
  const date = new Date(`${value}T00:00:00`)
  return !Number.isNaN(date.getTime()) && date.getFullYear() === Number(value.slice(0, 4)) && date.getMonth() + 1 === Number(value.slice(5, 7)) && date.getDate() === Number(value.slice(8, 10))
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
      customerId: vehicle.customerId,
      vehicleId: vehicle.id,
      customer: customersById.get(vehicle.customerId)?.name ?? '顧客未登録',
      vehicle: [vehicle.maker, vehicle.name].filter(Boolean).join(' '),
      plate: vehicle.registrationNumber ?? '',
      date: formatDate(date),
      tone,
      diffDays,
    }
  }).filter((inspection): inspection is NonNullable<typeof inspection> => inspection !== null && inspection.diffDays <= 30).sort((left, right) => left.diffDays - right.diffDays).slice(0, 5).map(({ diffDays: _diffDays, ...inspection }) => inspection)
}

function buildMaintenanceDateRows(
  maintenanceRows: Array<typeof maintenanceDocuments.$inferSelect>,
  customersById: Map<string, typeof customers.$inferSelect>,
  vehiclesById: Map<string, typeof vehicles.$inferSelect>,
  today: Date,
  dateField: 'intakeDate' | 'plannedReleaseDate',
) {
  return maintenanceRows.map((document) => {
    if (!document.vehicleId) return null
    const vehicle = vehiclesById.get(document.vehicleId)
    if (!vehicle) return null
    const dateValue = dateField === 'intakeDate' ? document.intakeDate : document.plannedReleaseDate
    const date = parseDate(dateValue)
    if (!date) return null
    const diffDays = daysBetween(today, date)
    const tone = diffDays <= 30 ? 'warning' : 'normal'
    return {
      customerId: document.customerId,
      vehicleId: vehicle.id,
      customer: customersById.get(document.customerId)?.name ?? '顧客未登録',
      vehicle: [vehicle.maker, vehicle.name].filter(Boolean).join(' '),
      plate: vehicle.registrationNumber ?? '',
      date: formatDate(date),
      tone,
      diffDays,
    }
  }).filter((row): row is NonNullable<typeof row> => row !== null && row.diffDays >= 0).sort((left, right) => left.diffDays - right.diffDays).slice(0, 5).map(({ diffDays: _diffDays, ...row }) => row)
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
      documentId: invoice.documentId,
      section: invoice.source === '販売請求書' ? 'sales' as const : 'maintenance' as const,
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

function customerVehicleLabel(customer: string, vehicle: string) {
  return `${customer} - ${vehicle}`
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
