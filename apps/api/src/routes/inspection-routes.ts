import { and, asc, eq } from 'drizzle-orm'
import { customers, inspectionSchedules, vehicles } from '@vehicle-management/database'
import { UnauthorizedError } from '../auth/firebase'
import { requireOrganizationContext } from '../auth/organization'
import { createDatabase } from '../db/client'
import { HttpError, jsonResponse, readJson } from '../http'

const inspectionTypes = new Set(['車検', '12か月点検', '24か月点検', '一般点検'])
const inspectionStatuses = new Set(['予定', '完了', 'キャンセル'])

export async function handleInspectionRoutes(request: Request, env: Env): Promise<Response | null> {
  const pathname = new URL(request.url).pathname.replace(/\/$/, '') || '/'
  const isCollection = pathname === '/api/inspection-schedules'
  const itemMatch = pathname.match(/^\/api\/inspection-schedules\/([^/]+)$/)
  if (!isCollection && !itemMatch) return null

  try {
    const database = createDatabase(env.DB)
    const context = await requireOrganizationContext(request, env, database)
    const organizationId = context.organization.organizationId
    if (isCollection) {
      if (request.method === 'GET') return await listSchedules(request, env, database, organizationId)
      if (request.method === 'POST') return await createSchedule(request, env, database, organizationId)
      throw new HttpError(405, 'この操作には対応していません。')
    }
    if (request.method === 'PATCH') return await updateSchedule(request, env, database, itemMatch![1], organizationId)
    if (request.method === 'DELETE') return await deleteSchedule(env, database, itemMatch![1], organizationId)
    throw new HttpError(405, 'この操作には対応していません。')
  } catch (error) {
    if (error instanceof UnauthorizedError) return jsonResponse({ error: error.message }, 401, env)
    if (error instanceof HttpError) return jsonResponse({ error: error.message }, error.status, env)
    console.error(error)
    return jsonResponse({ error: '車検・点検予定の処理に失敗しました。' }, 500, env)
  }
}

async function listSchedules(request: Request, env: Env, database: ReturnType<typeof createDatabase>, organizationId: string) {
  const url = new URL(request.url)
  const vehicleId = url.searchParams.get('vehicleId')?.trim() ?? ''
  const status = url.searchParams.get('status')?.trim() ?? ''
  const conditions = [eq(inspectionSchedules.organizationId, organizationId)]
  if (vehicleId) conditions.push(eq(inspectionSchedules.vehicleId, vehicleId))
  if (status && inspectionStatuses.has(status)) conditions.push(eq(inspectionSchedules.status, status))
  const [rows, customerRows, vehicleRows] = await Promise.all([
    database.select().from(inspectionSchedules).where(and(...conditions)).orderBy(asc(inspectionSchedules.dueDate)).all(),
    database.select().from(customers).where(eq(customers.organizationId, organizationId)).all(),
    database.select().from(vehicles).where(eq(vehicles.organizationId, organizationId)).all(),
  ])
  return jsonResponse({ schedules: serializeSchedules(rows, customerRows, vehicleRows) }, 200, env)
}

async function createSchedule(request: Request, env: Env, database: ReturnType<typeof createDatabase>, organizationId: string) {
  const input = await parseScheduleInput(await readJson(request), database, organizationId)
  const id = crypto.randomUUID()
  await database.insert(inspectionSchedules).values({ id, organizationId, ...input }).run()
  return jsonResponse({ schedule: await findSchedule(database, id, organizationId) }, 201, env)
}

async function updateSchedule(request: Request, env: Env, database: ReturnType<typeof createDatabase>, id: string, organizationId: string) {
  const current = await database.select().from(inspectionSchedules).where(and(eq(inspectionSchedules.id, id), eq(inspectionSchedules.organizationId, organizationId))).get()
  if (!current) throw new HttpError(404, '点検予定が見つかりません。')
  const body = await readJson(request)
  const input = await parseScheduleInput({ ...body, customerId: body.customerId ?? current.customerId, vehicleId: body.vehicleId ?? current.vehicleId, inspectionType: body.inspectionType ?? current.inspectionType, dueDate: body.dueDate ?? current.dueDate, status: body.status ?? current.status, note: body.note === undefined ? current.note : body.note, notifiedAt: body.notifiedAt === undefined ? current.notifiedAt : body.notifiedAt }, database, organizationId)
  await database.update(inspectionSchedules).set({ ...input, updatedAt: new Date().toISOString() }).where(and(eq(inspectionSchedules.id, id), eq(inspectionSchedules.organizationId, organizationId))).run()
  return jsonResponse({ schedule: await findSchedule(database, id, organizationId) }, 200, env)
}

async function deleteSchedule(env: Env, database: ReturnType<typeof createDatabase>, id: string, organizationId: string) {
  const result = await database.delete(inspectionSchedules).where(and(eq(inspectionSchedules.id, id), eq(inspectionSchedules.organizationId, organizationId))).run()
  if (!result.success || result.meta.changes === 0) throw new HttpError(404, '点検予定が見つかりません。')
  return jsonResponse({ deleted: true }, 200, env)
}

async function findSchedule(database: ReturnType<typeof createDatabase>, id: string, organizationId: string) {
  const [row, customerRows, vehicleRows] = await Promise.all([
    database.select().from(inspectionSchedules).where(and(eq(inspectionSchedules.id, id), eq(inspectionSchedules.organizationId, organizationId))).get(),
    database.select().from(customers).where(eq(customers.organizationId, organizationId)).all(),
    database.select().from(vehicles).where(eq(vehicles.organizationId, organizationId)).all(),
  ])
  if (!row) return null
  return serializeSchedules([row], customerRows, vehicleRows)[0] ?? null
}

async function parseScheduleInput(body: Record<string, unknown>, database: ReturnType<typeof createDatabase>, organizationId: string) {
  const customerId = stringValue(body, 'customerId')
  const customer = await database.select({ id: customers.id }).from(customers).where(and(eq(customers.id, customerId), eq(customers.organizationId, organizationId))).get()
  if (!customer) throw new HttpError(400, '顧客を選択してください。')
  const vehicleId = stringValue(body, 'vehicleId')
  const vehicle = await database.select({ id: vehicles.id, customerId: vehicles.customerId }).from(vehicles).where(and(eq(vehicles.id, vehicleId), eq(vehicles.organizationId, organizationId))).get()
  if (!vehicle || vehicle.customerId !== customerId) throw new HttpError(400, '選択した車両が顧客と一致しません。')
  const inspectionType = stringValue(body, 'inspectionType')
  if (!inspectionTypes.has(inspectionType)) throw new HttpError(400, '点検種別が不正です。')
  const dueDate = dateValue(body.dueDate)
  if (!dueDate) throw new HttpError(400, '予定日を正しく入力してください。')
  const status = stringValue(body, 'status') || '予定'
  if (!inspectionStatuses.has(status)) throw new HttpError(400, '点検予定の状態が不正です。')
  return { customerId, vehicleId, inspectionType, dueDate, status, notifiedAt: nullableDate(body.notifiedAt), note: nullableString(body, 'note') }
}

function serializeSchedules(rows: Array<typeof inspectionSchedules.$inferSelect>, customerRows: Array<typeof customers.$inferSelect>, vehicleRows: Array<typeof vehicles.$inferSelect>) {
  const customersById = new Map(customerRows.map((customer) => [customer.id, customer]))
  const vehiclesById = new Map(vehicleRows.map((vehicle) => [vehicle.id, vehicle]))
  return rows.map((row) => {
    const vehicle = vehiclesById.get(row.vehicleId)
    return { id: row.id, customerId: row.customerId, customerName: customersById.get(row.customerId)?.name ?? '', vehicleId: row.vehicleId, vehicle: vehicle ? [vehicle.maker, vehicle.name].filter(Boolean).join(' ') : '', plate: vehicle?.registrationNumber ?? '', inspectionType: row.inspectionType, dueDate: row.dueDate, status: row.status, notifiedAt: row.notifiedAt, note: row.note ?? '', createdAt: row.createdAt, updatedAt: row.updatedAt }
  })
}

function stringValue(body: Record<string, unknown>, key: string) { return typeof body[key] === 'string' ? body[key].trim() : '' }
function nullableString(body: Record<string, unknown>, key: string) { const value = stringValue(body, key); return value || null }
function dateValue(value: unknown) { return typeof value === 'string' && /^\d{4}[-/]\d{2}[-/]\d{2}$/.test(value.trim()) ? value.trim().replaceAll('/', '-') : '' }
function nullableDate(value: unknown) { return dateValue(value) || null }
