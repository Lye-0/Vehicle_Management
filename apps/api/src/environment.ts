export type EnvironmentConfig = {
  APP_ENV?: string
  DATA_ENV?: string
  FIREBASE_PROJECT_ID?: string
  FIREBASE_AUTH_EMULATOR?: string
  FIREBASE_WEB_API_KEY?: string
  FIREBASE_ADMIN_SERVICE_ACCOUNT_JSON?: string
  INITIAL_SETUP_KEY?: string
  CORS_ORIGIN?: string
  B2_BUCKET?: string
}

const developmentB2Bucket = 'vehicle-management-64-dev'

export function getEnvironmentIssues(env: EnvironmentConfig): string[] {
  const issues: string[] = []
  const appEnvironment = env.APP_ENV?.trim()
  const dataEnvironment = env.DATA_ENV?.trim()
  const firebaseProjectId = env.FIREBASE_PROJECT_ID?.trim()
  const b2Bucket = env.B2_BUCKET?.trim()

  if (!appEnvironment) {
    issues.push('APP_ENVが設定されていません。')
    return issues
  }
  if (!['development', 'production', 'test'].includes(appEnvironment)) {
    issues.push(`APP_ENVの値が不正です: ${appEnvironment}`)
    return issues
  }

  if (appEnvironment === 'development') {
    if (env.FIREBASE_AUTH_EMULATOR !== 'true') issues.push('開発環境ではFIREBASE_AUTH_EMULATOR=trueが必要です。')
    if (dataEnvironment === 'production') issues.push('開発環境でDATA_ENV=productionは使用できません。')
    return issues
  }

  if (appEnvironment !== 'production') return issues

  if (dataEnvironment !== 'production') issues.push('本番環境ではDATA_ENV=productionが必要です。')
  if (env.FIREBASE_AUTH_EMULATOR !== 'false') issues.push('本番環境ではFIREBASE_AUTH_EMULATOR=falseが必要です。')
  if (!firebaseProjectId) {
    issues.push('本番環境のFIREBASE_PROJECT_IDが未設定です。')
  } else if (firebaseProjectId.includes('REPLACE_WITH')) {
    issues.push('本番環境のFIREBASE_PROJECT_IDにプレースホルダーは使用できません。')
  }
  if (!env.FIREBASE_WEB_API_KEY?.trim()) issues.push('本番環境のFIREBASE_WEB_API_KEYが未設定です。')
  if (!env.INITIAL_SETUP_KEY?.trim()) issues.push('本番環境のINITIAL_SETUP_KEYが未設定です。')
  if (!isProductionCorsOrigin(env.CORS_ORIGIN)) issues.push('本番環境のCORS_ORIGINはlocalhost以外のHTTPS URLが必要です。')
  if (!b2Bucket) {
    issues.push('本番環境のB2_BUCKETが未設定です。')
  } else if (b2Bucket === developmentB2Bucket || /(^|[-_])dev(?:[-_.]|$)/i.test(b2Bucket) || b2Bucket.includes('REPLACE_WITH')) {
    issues.push('本番環境では開発用とは別のB2バケットを指定してください。')
  }

  return issues
}

function isProductionCorsOrigin(value: string | undefined) {
  if (!value?.trim()) return false
  try {
    const url = new URL(value)
    return url.protocol === 'https:' && !['localhost', '127.0.0.1', '[::1]'].includes(url.hostname)
  } catch {
    return false
  }
}
