import { connectAuthEmulator, getAuth, type Auth } from 'firebase/auth'
import { getApp, getApps, initializeApp, type FirebaseApp, type FirebaseOptions } from 'firebase/app'

const appEnvironment = import.meta.env.VITE_APP_ENV?.trim() || (import.meta.env.DEV ? 'development' : 'production')
const emulatorUrl = import.meta.env.VITE_FIREBASE_AUTH_EMULATOR_URL?.trim()
const isDevelopmentAuthEmulator = import.meta.env.DEV && appEnvironment === 'development' && Boolean(emulatorUrl)

const firebaseConfig: FirebaseOptions = {
  apiKey: import.meta.env.VITE_FIREBASE_API_KEY || (isDevelopmentAuthEmulator ? 'demo' : undefined),
  authDomain: import.meta.env.VITE_FIREBASE_AUTH_DOMAIN,
  projectId: import.meta.env.VITE_FIREBASE_PROJECT_ID,
  storageBucket: import.meta.env.VITE_FIREBASE_STORAGE_BUCKET,
  messagingSenderId: import.meta.env.VITE_FIREBASE_MESSAGING_SENDER_ID,
  appId: import.meta.env.VITE_FIREBASE_APP_ID,
}

let firebaseApp: FirebaseApp | undefined
let auth: Auth | undefined
let authEmulatorConnected = false

export function getFirebaseApp() {
  if (firebaseApp) return firebaseApp
  validateFirebaseEnvironment()
  const requiredKeys = isDevelopmentAuthEmulator ? ['projectId'] : ['apiKey', 'authDomain', 'projectId', 'appId']
  const missingKeys = requiredKeys.filter((key) => !firebaseConfig[key as keyof FirebaseOptions])
  if (missingKeys.length > 0) throw new Error(`Firebaseの環境変数が不足しています: ${missingKeys.join(', ')}`)
  firebaseApp = getApps().length > 0 ? getApp() : initializeApp(firebaseConfig)
  return firebaseApp
}

export function getFirebaseAuth() {
  if (!auth) auth = getAuth(getFirebaseApp())
  if (isDevelopmentAuthEmulator && emulatorUrl && !authEmulatorConnected) {
    connectAuthEmulator(auth, emulatorUrl, { disableWarnings: true })
    authEmulatorConnected = true
  }
  return auth
}

function validateFirebaseEnvironment() {
  const issues: string[] = []
  const projectId = firebaseConfig.projectId?.trim()

  if (appEnvironment !== 'development' && appEnvironment !== 'production') {
    issues.push(`VITE_APP_ENVの値が不正です: ${appEnvironment}`)
  }
  if (import.meta.env.PROD && appEnvironment !== 'production') {
    issues.push('本番ビルドではVITE_APP_ENV=productionが必要です。')
  }
  if (appEnvironment === 'development' && import.meta.env.DEV && !emulatorUrl) {
    issues.push('開発環境ではVITE_FIREBASE_AUTH_EMULATOR_URLが必要です。')
  }
  if (appEnvironment === 'production') {
    if (emulatorUrl) issues.push('本番環境ではFirebase Auth Emulator URLを設定できません。')
    if (!projectId) issues.push('本番環境のVITE_FIREBASE_PROJECT_IDが未設定です。')
    else if (projectId.includes('REPLACE_WITH')) issues.push('本番環境のVITE_FIREBASE_PROJECT_IDにプレースホルダーは使用できません。')
  }

  if (issues.length > 0) throw new Error(`Firebaseの環境設定が不正です: ${issues.join(' ')}`)
}
