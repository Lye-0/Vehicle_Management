import { eq } from 'drizzle-orm'
import { staffProfiles } from '@vehicle-management/database'
import { requireAuthenticatedUser, UnauthorizedError, type FirebaseUser } from '../auth/firebase'
import { completeInitialOrganizationSetup, completeInitialPasswordChange, loadAuthSession } from '../auth/organization'
import { createDatabase } from '../db/client'
import { HttpError, jsonResponse, readJson } from '../http'

export async function handleOrganizationRoutes(request: Request, env: Env): Promise<Response | null> {
  const pathname = new URL(request.url).pathname.replace(/\/$/, '') || '/'
  const isSessionRoute = pathname === '/api/auth/me'
  const isSetupRoute = pathname === '/api/setup/organization'
  const isPasswordCompleteRoute = pathname === '/api/auth/password/complete'
  const isProfileRoute = pathname === '/api/auth/profile'
  if (!isSessionRoute && !isSetupRoute && !isPasswordCompleteRoute && !isProfileRoute) return null

  try {
    const user = await requireAuthenticatedUser(request, env)
    const database = createDatabase(env.DB)
    if (isSessionRoute && request.method === 'GET') return jsonResponse(await loadAuthSession(database, env, user), 200, env)
    if (isProfileRoute && request.method === 'PATCH') return await updateProfile(request, database, user, env)
    if (isSetupRoute && request.method === 'POST') {
      const body = await readJson(request)
      const organizationId = await completeInitialOrganizationSetup(database, env, user, stringValue(body, 'name'), stringValue(body, 'setupKey'))
      return jsonResponse({ session: await loadAuthSession(database, env, user), organizationId }, 201, env)
    }
    if (isPasswordCompleteRoute && request.method === 'POST') {
      await completeInitialPasswordChange(database, user.uid)
      return jsonResponse({ session: await loadAuthSession(database, env, user) }, 200, env)
    }
    throw new HttpError(405, 'この操作には対応していません。')
  } catch (error) {
    if (error instanceof UnauthorizedError) return jsonResponse({ error: error.message }, 401, env)
    if (error instanceof HttpError) return jsonResponse({ error: error.message }, error.status, env)
    console.error(error)
    return jsonResponse({ error: '組織・認証情報の処理に失敗しました。' }, 500, env)
  }
}

async function updateProfile(request: Request, database: ReturnType<typeof createDatabase>, user: FirebaseUser, env: Env) {
  if (user.isAnonymous) throw new HttpError(403, '開発用匿名ログインではプロフィールを保存できません。')
  const body = await readJson(request)
  const existing = await database.select().from(staffProfiles).where(eq(staffProfiles.uid, user.uid)).get()
  const displayName = body.displayName === undefined ? existing?.displayName ?? user.displayName ?? user.email ?? 'ログインユーザー' : requiredProfileDisplayName(body.displayName)
  const email = body.email === undefined ? existing?.email ?? user.email : normalizedProfileEmail(body.email)
  const now = new Date().toISOString()
  if (existing) {
    await database.update(staffProfiles).set({ displayName, email, updatedAt: now }).where(eq(staffProfiles.uid, user.uid)).run()
  } else {
    await database.insert(staffProfiles).values({ uid: user.uid, displayName, email, role: 'employee', updatedAt: now }).run()
  }
  return jsonResponse({ profile: { displayName, email } }, 200, env)
}

function requiredProfileDisplayName(value: unknown) {
  const displayName = typeof value === 'string' ? value.trim().slice(0, 100) : ''
  if (!displayName) throw new HttpError(400, '表示名を入力してください。')
  return displayName
}

function normalizedProfileEmail(value: unknown) {
  if (value === null) return null
  if (typeof value !== 'string') throw new HttpError(400, 'メールアドレスが不正です。')
  const email = value.trim().toLowerCase()
  if (!email || email.length > 254 || !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email)) throw new HttpError(400, '有効なメールアドレスを入力してください。')
  return email
}
function stringValue(body: Record<string, unknown>, key: string) {
  return typeof body[key] === 'string' ? body[key].trim() : ''
}
