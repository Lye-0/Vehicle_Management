import { requireAuthenticatedUser, UnauthorizedError } from '../auth/firebase'
import { completeInitialOrganizationSetup, completeInitialPasswordChange, loadAuthSession } from '../auth/organization'
import { createDatabase } from '../db/client'
import { HttpError, jsonResponse, readJson } from '../http'

export async function handleOrganizationRoutes(request: Request, env: Env): Promise<Response | null> {
  const pathname = new URL(request.url).pathname.replace(/\/$/, '') || '/'
  const isSessionRoute = pathname === '/api/auth/me'
  const isSetupRoute = pathname === '/api/setup/organization'
  const isPasswordCompleteRoute = pathname === '/api/auth/password/complete'
  if (!isSessionRoute && !isSetupRoute && !isPasswordCompleteRoute) return null

  try {
    const user = await requireAuthenticatedUser(request, env)
    const database = createDatabase(env.DB)
    if (isSessionRoute && request.method === 'GET') return jsonResponse(await loadAuthSession(database, env, user), 200, env)
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

function stringValue(body: Record<string, unknown>, key: string) {
  return typeof body[key] === 'string' ? body[key].trim() : ''
}
