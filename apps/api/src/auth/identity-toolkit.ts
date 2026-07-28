import { importPKCS8, SignJWT } from 'jose'
import { HttpError } from '../http'

type IdentityToolkitResponse = {
  localId?: string
  idToken?: string
  email?: string
  requestType?: string
  error?: { message?: string }
}

type EmulatorOobCode = {
  email?: string
  oobCode?: string
  requestType?: string
}

type ServiceAccountCredentials = {
  client_email?: unknown
  private_key?: unknown
  token_uri?: unknown
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

export async function deleteEmailPasswordUser(env: Env, idToken: string) {
  const response = await callIdentityToolkit(env, 'accounts:delete', { idToken })
  if (!response.ok) throw new IdentityToolkitError(getErrorCode(response.body), getErrorCode(response.body))
}

export async function sendPasswordResetEmail(env: Env, email: string) {
  const response = await callIdentityToolkit(env, 'accounts:sendOobCode', { requestType: 'PASSWORD_RESET', email })
  if (!response.ok) throw new IdentityToolkitError(getErrorCode(response.body), getErrorCode(response.body))
}

export async function resetEmailPasswordUser(env: Env, uid: string, email: string, password: string) {
  if (env.APP_ENV === 'development' && env.FIREBASE_AUTH_EMULATOR_RESET_MODE === 'skip') return
  if (isAuthEmulator(env)) {
    await resetEmulatorPassword(env, email, password)
    return
  }
  await resetProductionPassword(env, uid, password)
}

async function resetEmulatorPassword(env: Env, email: string, password: string) {
  await sendPasswordResetEmail(env, email)
  const projectId = env.FIREBASE_PROJECT_ID?.trim()
  if (!projectId) throw new HttpError(503, 'FirebaseプロジェクトIDが設定されていません。')

  const response = await fetch('http://127.0.0.1:9099/emulator/v1/projects/' + encodeURIComponent(projectId) + '/oobCodes')
  const body = await response.json().catch(() => ({})) as { oobCodes?: EmulatorOobCode[] }
  const code = [...(body.oobCodes ?? [])].reverse().find((item) => item.email === email && item.requestType === 'PASSWORD_RESET' && item.oobCode)
  if (!response.ok || !code?.oobCode) throw new IdentityToolkitError('OOB_CODE_NOT_FOUND', 'パスワード再設定コードを取得できませんでした。')

  const resetResponse = await callIdentityToolkit(env, 'accounts:resetPassword', { oobCode: code.oobCode, newPassword: password })
  if (!resetResponse.ok) throw new IdentityToolkitError(getErrorCode(resetResponse.body), getErrorCode(resetResponse.body))
}

async function resetProductionPassword(env: Env, uid: string, password: string) {
  const projectId = env.FIREBASE_PROJECT_ID?.trim()
  if (!projectId) throw new HttpError(503, 'FirebaseプロジェクトIDが設定されていません。')
  const credentials = parseServiceAccountCredentials(env.FIREBASE_ADMIN_SERVICE_ACCOUNT_JSON)
  const accessToken = await createGoogleAccessToken(credentials)
  const response = await fetch('https://identitytoolkit.googleapis.com/v1/projects/' + encodeURIComponent(projectId) + '/accounts:update', {
    method: 'POST',
    headers: {
      Authorization: 'Bearer ' + accessToken,
      'Content-Type': 'application/json',
    },
    body: JSON.stringify({ localId: uid, password, returnSecureToken: false }),
  })
  const body = await response.json().catch(() => ({})) as IdentityToolkitResponse
  if (!response.ok) throw new IdentityToolkitError(getErrorCode(body), getErrorCode(body))
}

function parseServiceAccountCredentials(value: string | undefined) {
  if (!value?.trim()) throw new HttpError(503, 'Firebase管理者用サービスアカウントが設定されていません。')
  let credentials: ServiceAccountCredentials
  try {
    credentials = JSON.parse(value) as ServiceAccountCredentials
  } catch {
    throw new HttpError(503, 'Firebase管理者用サービスアカウントの形式が不正です。')
  }
  if (typeof credentials.client_email !== 'string' || typeof credentials.private_key !== 'string') {
    throw new HttpError(503, 'Firebase管理者用サービスアカウントに必要な情報がありません。')
  }
  return {
    clientEmail: credentials.client_email,
    privateKey: credentials.private_key.replaceAll('\\n', '\n'),
    tokenUri: typeof credentials.token_uri === 'string' && credentials.token_uri ? credentials.token_uri : 'https://oauth2.googleapis.com/token',
  }
}

async function createGoogleAccessToken(credentials: { clientEmail: string; privateKey: string; tokenUri: string }) {
  let key: CryptoKey
  try {
    key = await importPKCS8(credentials.privateKey, 'RS256')
  } catch {
    throw new HttpError(503, 'Firebase管理者用サービスアカウントの秘密鍵が不正です。')
  }
  const now = Math.floor(Date.now() / 1000)
  const assertion = await new SignJWT({ scope: 'https://www.googleapis.com/auth/identitytoolkit' })
    .setProtectedHeader({ alg: 'RS256', typ: 'JWT' })
    .setIssuer(credentials.clientEmail)
    .setSubject(credentials.clientEmail)
    .setAudience(credentials.tokenUri)
    .setIssuedAt(now)
    .setExpirationTime(now + 3600)
    .sign(key)
  const response = await fetch(credentials.tokenUri, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ grant_type: 'urn:ietf:params:oauth:grant-type:jwt-bearer', assertion }),
  })
  const body = await response.json().catch(() => ({})) as { access_token?: string }
  if (!response.ok || !body.access_token) throw new HttpError(503, 'Firebase管理者用アクセストークンを取得できませんでした。')
  return body.access_token
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
