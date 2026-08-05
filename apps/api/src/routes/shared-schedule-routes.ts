import { and, asc, eq } from 'drizzle-orm'
import { sharedSchedules, staffProfiles } from '@vehicle-management/database'
import { UnauthorizedError } from '../auth/firebase'
import { requireOrganizationContext } from '../auth/organization'
import { createDatabase } from '../db/client'
import { HttpError, jsonResponse, readJson } from '../http'

export async function handleSharedScheduleRoutes(request: Request, env: Env): Promise<Response | null> {
  const pathname = new URL(request.url).pathname.replace(/\/$/, '') || '/'
  const isCollection = pathname === '/api/shared-schedules'
  const itemMatch = pathname.match(/^\/api\/shared-schedules\/([^/]+)$/)
  if (!isCollection && !itemMatch) return null

  try {
    const database = createDatabase(env.DB)
    const context = await requireOrganizationContext(request, env, database)
    const organizationId = context.organization.organizationId
    if (isCollection) {
      if (request.method === 'GET') return await listSharedSchedules(database, env, organizationId, context.user.uid, context.user.displayName, context.user.email)
      if (request.method === 'POST') return await createSharedSchedule(request, database, env, organizationId, context.user.uid, context.user.displayName, context.user.email)
      throw new HttpError(405, 'この操作には対応していません。')
    }
    const id = decodeURIComponent(itemMatch![1])
    if (request.method === 'PATCH') return await updateSharedSchedule(request, database, env, id, organizationId, context.user.uid, context.user.displayName, context.user.email)
    if (request.method === 'DELETE') return await deleteSharedSchedule(database, env, id, organizationId)
    throw new HttpError(405, 'この操作には対応していません。')
  } catch (error) {
    if (error instanceof UnauthorizedError) return jsonResponse({ error: error.message }, 401, env)
    if (error instanceof HttpError) return jsonResponse({ error: error.message }, error.status, env)
    console.error(error)
    return jsonResponse({ error: '共有スケジュールの処理に失敗しました。' }, 500, env)
  }
}

async function listSharedSchedules(database: ReturnType<typeof createDatabase>, env: Env, organizationId: string, currentUid: string, currentDisplayName: string | null, currentEmail: string | null) {
  const [rows, profiles] = await Promise.all([
    database.select().from(sharedSchedules).where(eq(sharedSchedules.organizationId, organizationId)).orderBy(asc(sharedSchedules.startDate), asc(sharedSchedules.createdAt)).all(),
    database.select({ uid: staffProfiles.uid, displayName: staffProfiles.displayName }).from(staffProfiles).all(),
  ])
  return jsonResponse({ schedules: serializeSharedSchedules(rows, profiles, currentUid, currentDisplayName, currentEmail) }, 200, env)
}

async function createSharedSchedule(request: Request, database: ReturnType<typeof createDatabase>, env: Env, organizationId: string, currentUid: string, currentDisplayName: string | null, currentEmail: string | null) {
  const input = parseSharedScheduleInput(await readJson(request))
  const id = crypto.randomUUID()
  const now = new Date().toISOString()
  await database.insert(sharedSchedules).values({ id, organizationId, ...input, createdByUid: currentUid, createdAt: now, updatedAt: now }).run()
  const row = await database.select().from(sharedSchedules).where(eq(sharedSchedules.id, id)).get()
  if (!row) throw new HttpError(500, '共有スケジュールを保存できませんでした。')
  const profile = await database.select({ uid: staffProfiles.uid, displayName: staffProfiles.displayName }).from(staffProfiles).where(eq(staffProfiles.uid, currentUid)).get()
  const authorName = profile?.displayName?.trim() || currentDisplayName?.trim() || currentEmail?.trim() || '未設定ユーザー'
  return jsonResponse({ schedule: serializeSharedSchedules([row], [{ uid: currentUid, displayName: authorName }], currentUid, currentDisplayName, currentEmail)[0] }, 201, env)
}

async function updateSharedSchedule(request: Request, database: ReturnType<typeof createDatabase>, env: Env, id: string, organizationId: string, currentUid: string, currentDisplayName: string | null, currentEmail: string | null) {
  const current = await database.select().from(sharedSchedules).where(and(eq(sharedSchedules.id, id), eq(sharedSchedules.organizationId, organizationId))).get()
  if (!current) throw new HttpError(404, '共有スケジュールが見つかりません。')
  const input = parseSharedScheduleInput(await readJson(request))
  const updatedAt = new Date().toISOString()
  await database.update(sharedSchedules).set({ ...input, updatedAt }).where(and(eq(sharedSchedules.id, id), eq(sharedSchedules.organizationId, organizationId))).run()
  const row = await database.select().from(sharedSchedules).where(and(eq(sharedSchedules.id, id), eq(sharedSchedules.organizationId, organizationId))).get()
  if (!row) throw new HttpError(500, '共有スケジュールを更新できませんでした。')
  const profiles = await database.select({ uid: staffProfiles.uid, displayName: staffProfiles.displayName }).from(staffProfiles).all()
  return jsonResponse({ schedule: serializeSharedSchedules([row], profiles, currentUid, currentDisplayName, currentEmail)[0] }, 200, env)
}

async function deleteSharedSchedule(database: ReturnType<typeof createDatabase>, env: Env, id: string, organizationId: string) {
  const result = await database.delete(sharedSchedules).where(and(eq(sharedSchedules.id, id), eq(sharedSchedules.organizationId, organizationId))).run()
  if (!result.success || result.meta.changes === 0) throw new HttpError(404, '共有スケジュールが見つかりません。')
  return jsonResponse({ deleted: true }, 200, env)
}

function serializeSharedSchedules(rows: Array<typeof sharedSchedules.$inferSelect>, profiles: Array<{ uid: string; displayName: string }>, currentUid: string, currentDisplayName: string | null, currentEmail: string | null) {
  const profilesByUid = new Map(profiles.map((profile) => [profile.uid, profile.displayName]))
  const currentUserName = currentDisplayName?.trim() || currentEmail?.trim() || '未設定ユーザー'
  return rows.map((row) => ({
    id: row.id,
    title: row.title,
    startDate: row.startDate,
    endDate: row.endDate,
    detail: row.detail,
    authorName: profilesByUid.get(row.createdByUid)?.trim() || (row.createdByUid === currentUid ? currentUserName : '未設定ユーザー'),
    createdAt: row.createdAt,
    updatedAt: row.updatedAt,
  }))
}

function parseSharedScheduleInput(body: Record<string, unknown>) {
  const title = stringValue(body.title)
  if (!title) throw new HttpError(400, '予定名を入力してください。')
  if (title.length > 100) throw new HttpError(400, '予定名は100文字以内で入力してください。')

  const startDate = dateValue(body.startDate)
  if (!startDate) throw new HttpError(400, '開始日を正しく入力してください。')
  const endDate = dateValue(body.endDate) || startDate
  if (!isCalendarDate(endDate)) throw new HttpError(400, '終了日を正しく入力してください。')
  if (endDate < startDate) throw new HttpError(400, '終了日は開始日以降の日付を選択してください。')

  const detail = body.detail === undefined || body.detail === null ? '' : stringValue(body.detail)
  if (body.detail !== undefined && body.detail !== null && typeof body.detail !== 'string') throw new HttpError(400, '詳細は文字列で入力してください。')
  if (detail.length > 2000) throw new HttpError(400, '詳細は2000文字以内で入力してください。')
  return { title, startDate, endDate, detail }
}

function stringValue(value: unknown) {
  return typeof value === 'string' ? value.trim() : ''
}

function dateValue(value: unknown) {
  const normalized = typeof value === 'string' ? value.trim().replaceAll('/', '-') : ''
  return isCalendarDate(normalized) ? normalized : ''
}

function isCalendarDate(value: string) {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(value)) return false
  const date = new Date(`${value}T00:00:00`)
  return !Number.isNaN(date.getTime()) && date.getFullYear() === Number(value.slice(0, 4)) && date.getMonth() + 1 === Number(value.slice(5, 7)) && date.getDate() === Number(value.slice(8, 10))
}
