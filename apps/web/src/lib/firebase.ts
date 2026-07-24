import { connectAuthEmulator, getAuth, type Auth } from 'firebase/auth'
import { getApp, getApps, initializeApp, type FirebaseApp, type FirebaseOptions } from 'firebase/app'

const firebaseConfig: FirebaseOptions = {
  apiKey: import.meta.env.VITE_FIREBASE_API_KEY,
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
  const missingKeys = ['apiKey', 'authDomain', 'projectId', 'appId'].filter((key) => !firebaseConfig[key as keyof FirebaseOptions])
  if (missingKeys.length > 0) throw new Error(`Firebaseの環境変数が不足しています: ${missingKeys.join(', ')}`)
  firebaseApp = getApps().length > 0 ? getApp() : initializeApp(firebaseConfig)
  return firebaseApp
}

export function getFirebaseAuth() {
  if (!auth) auth = getAuth(getFirebaseApp())
  const emulatorUrl = import.meta.env.VITE_FIREBASE_AUTH_EMULATOR_URL
  if (import.meta.env.DEV && emulatorUrl && !authEmulatorConnected) {
    connectAuthEmulator(auth, emulatorUrl, { disableWarnings: true })
    authEmulatorConnected = true
  }
  return auth
}
