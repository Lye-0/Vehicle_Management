import { importPKCS8, SignJWT } from 'jose'
import { HttpError } from '../http'

export type ManagedAuthUser = {
  localId: string
  email?: string
  disabled?: boolean
  emailVerified?: boolean
  passwordUpdatedAt?: number
  providerUserInfo?: Array<{ providerId: string }>
}

// Never log request/response bodies: they can contain credentials and password hashes.
export async function firebaseAdminClient(env: Env) {
  const emulator = env.APP_ENV === 'development' && env.FIREBASE_AUTH_EMULATOR === 'true'
  const project = env.FIREBASE_PROJECT_ID
  if (!project) throw new HttpError(503, '認証サービスの設定を確認してください。')
  let accessToken = 'owner'
  if (!emulator) {
    try {
      const account = JSON.parse(env.FIREBASE_ADMIN_SERVICE_ACCOUNT_JSON || '{}') as { project_id?: string; client_email?: string; private_key?: string }
      if (account.project_id !== project || !account.client_email || !account.private_key) throw new Error('Invalid configuration')
      const key = await importPKCS8(account.private_key, 'RS256')
      const assertion = await new SignJWT({ scope: 'https://www.googleapis.com/auth/identitytoolkit' })
        .setProtectedHeader({ alg: 'RS256' }).setIssuer(account.client_email)
        .setAudience('https://oauth2.googleapis.com/token').setIssuedAt().setExpirationTime('5m').sign(key)
      const response = await fetch('https://oauth2.googleapis.com/token', {
        method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
        body: new URLSearchParams({ grant_type: 'urn:ietf:params:oauth:grant-type:jwt-bearer', assertion }),
        signal: AbortSignal.timeout(15000),
      })
      const token = await response.json() as { access_token?: string }
      if (!response.ok || !token.access_token) throw new Error('Authorization failed')
      accessToken = token.access_token
    } catch {
      throw new HttpError(503, '初期パスワードの再発行設定が利用できません。システム管理者に連絡してください。')
    }
  }
  const base = emulator ? 'http://127.0.0.1:9099/identitytoolkit.googleapis.com/v1' : 'https://identitytoolkit.googleapis.com/v1'
  async function call<T>(operation: string, body: Record<string, unknown>): Promise<T> {
    try {
      const response = await fetch(`${base}/projects/${encodeURIComponent(project!)}/accounts:${operation}`, {
        method: 'POST', headers: { Authorization: `Bearer ${accessToken}`, 'Content-Type': 'application/json' },
        body: JSON.stringify(body), signal: AbortSignal.timeout(15000),
      })
      if (!response.ok) throw new Error('Authentication service request failed')
      return await response.json() as T
    } catch {
      throw new HttpError(503, '認証サービスで再発行を確認できませんでした。システム管理者に連絡してください。')
    }
  }
  return {
    async lookup(uid: string) {
      const response = await call<{ users?: ManagedAuthUser[] }>('lookup', { localId: [uid] })
      return response.users?.find((user) => user.localId === uid) ?? null
    },
    async setPassword(uid: string, password: string) {
      const response = await call<{ localId?: string }>('update', { localId: uid, password, validSince: String(Math.floor(Date.now() / 1000)) })
      if (response.localId !== uid) throw new HttpError(503, '初期パスワードの再発行結果を確認できませんでした。')
    },
  }
}
