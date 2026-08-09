import { HttpError } from '../http'

type IdentityToolkitResponse = {
  localId?: string
  idToken?: string
  email?: string
  requestType?: string
  error?: { message?: string }
}

export class IdentityToolkitError extends Error {
  constructor(public readonly code: string, message = code) {
    super(message)
    this.name = 'IdentityToolkitError'
  }
}

export async function createEmailPasswordUser(env: Env, email: string, password: string) {
  const response = await callIdentityToolkit(env, 'accounts:signUp', { email, password, returnSecureToken: true })
  if (!response.ok) throw new IdentityToolkitError(getErrorCode(response.body), getErrorCode(response.body))
  if (!response.body.localId || !response.body.idToken) throw new IdentityToolkitError('INVALID_RESPONSE', 'Firebase Authenticationの応答が不正です。')
  return { uid: response.body.localId, idToken: response.body.idToken }
}

export async function updatePasswordWithIdToken(env: Env, idToken: string, password: string) {
  const response = await callIdentityToolkit(env, 'accounts:update', { idToken, password, returnSecureToken: true })
  if (!response.ok) throw new IdentityToolkitError(getErrorCode(response.body), getErrorCode(response.body))
  if (!response.body.localId) throw new IdentityToolkitError('INVALID_RESPONSE', 'Firebase Authenticationの応答が不正です。')
}

export async function deleteEmailPasswordUser(env: Env, idToken: string) {
  const response = await callIdentityToolkit(env, 'accounts:delete', { idToken })
  if (!response.ok) throw new IdentityToolkitError(getErrorCode(response.body), getErrorCode(response.body))
}

function isAuthEmulator(env: Pick<Env, 'APP_ENV' | 'FIREBASE_AUTH_EMULATOR'>) {
  return env.APP_ENV === 'development' && env.FIREBASE_AUTH_EMULATOR === 'true'
}

async function callIdentityToolkit(env: Env, operation: string, body: Record<string, unknown>) {
  const baseUrl = isAuthEmulator(env)
    ? 'http://127.0.0.1:9099/identitytoolkit.googleapis.com/v1'
    : 'https://identitytoolkit.googleapis.com/v1'
  const apiKey = env.FIREBASE_WEB_API_KEY || (env.FIREBASE_AUTH_EMULATOR === 'true' ? 'demo' : '')
  if (!apiKey) throw new HttpError(503, 'Firebase Web APIキーが設定されていません。')
  const response = await fetch(baseUrl + '/' + operation + '?key=' + encodeURIComponent(apiKey), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
  const responseBody = await response.json().catch(() => ({})) as IdentityToolkitResponse
  return { ok: response.ok, body: responseBody }
}

function getErrorCode(body: IdentityToolkitResponse) {
  return body.error?.message || 'FIREBASE_AUTH_REQUEST_FAILED'
}
