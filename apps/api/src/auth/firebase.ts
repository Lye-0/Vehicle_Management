import { createRemoteJWKSet, jwtVerify, type JWTPayload } from 'jose'

const firebasePublicKeys = createRemoteJWKSet(new URL('https://www.googleapis.com/service_accounts/v1/jwk/securetoken@system.gserviceaccount.com'))

export type FirebaseUser = {
  uid: string
  email: string | null
  displayName: string | null
  emailVerified: boolean
}

export class UnauthorizedError extends Error {
  constructor(message = '認証が必要です。') {
    super(message)
    this.name = 'UnauthorizedError'
  }
}

export async function verifyFirebaseIdToken(token: string, projectId: string): Promise<FirebaseUser> {
  const { payload } = await jwtVerify(token, firebasePublicKeys, {
    issuer: `https://securetoken.google.com/${projectId}`,
    audience: projectId,
  })
  return toFirebaseUser(payload)
}

export async function requireFirebaseUser(request: Request, projectId: string | undefined, allowEmulatorToken = false): Promise<FirebaseUser> {
  const token = getBearerToken(request)
  if (!token) throw new UnauthorizedError()
  if (!projectId) throw new Error('FIREBASE_PROJECT_IDが設定されていません。')

  try {
    if (allowEmulatorToken) return verifyFirebaseEmulatorToken(token, projectId)
    return await verifyFirebaseIdToken(token, projectId)
  } catch {
    throw new UnauthorizedError('認証トークンが無効です。')
  }
}

function getBearerToken(request: Request) {
  const authorization = request.headers.get('Authorization')
  const match = authorization?.match(/^Bearer\s+(.+)$/i)
  return match?.[1] ?? null
}

function toFirebaseUser(payload: JWTPayload): FirebaseUser {
  if (typeof payload.sub !== 'string' || payload.sub.length === 0) throw new UnauthorizedError('認証ユーザーが特定できません。')
  return {
    uid: payload.sub,
    email: typeof payload.email === 'string' ? payload.email : null,
    displayName: typeof payload.name === 'string' ? payload.name : null,
    emailVerified: payload.email_verified === true,
  }
}

function verifyFirebaseEmulatorToken(token: string, projectId: string) {
  const encodedPayload = token.split('.')[1]
  if (!encodedPayload) throw new UnauthorizedError('認証トークンの形式が不正です。')
  const payload = JSON.parse(new TextDecoder().decode(decodeBase64Url(encodedPayload))) as JWTPayload
  if (payload.aud !== projectId || payload.iss !== `https://securetoken.google.com/${projectId}`) throw new UnauthorizedError('認証トークンの対象が不正です。')
  if (typeof payload.exp !== 'number' || payload.exp <= Math.floor(Date.now() / 1000)) throw new UnauthorizedError('認証トークンの有効期限が切れています。')
  return toFirebaseUser(payload)
}

function decodeBase64Url(value: string) {
  const normalized = value.replace(/-/g, '+').replace(/_/g, '/')
  const padded = normalized.padEnd(Math.ceil(normalized.length / 4) * 4, '=')
  const binary = atob(padded)
  return Uint8Array.from(binary, (character) => character.charCodeAt(0))
}
