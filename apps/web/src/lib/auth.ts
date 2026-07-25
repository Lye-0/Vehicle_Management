import {
  EmailAuthProvider,
  GoogleAuthProvider,
  createUserWithEmailAndPassword,
  linkWithCredential,
  linkWithPopup,
  onAuthStateChanged,
  reauthenticateWithCredential,
  reauthenticateWithPopup,
  reload,
  sendEmailVerification,
  sendPasswordResetEmail,
  signInAnonymously,
  signInWithEmailAndPassword,
  signInWithPopup,
  signOut,
  unlink,
  updateEmail,
  updatePassword,
  type User,
} from 'firebase/auth'
import { getFirebaseAuth } from './firebase'

let localSignInPromise: ReturnType<typeof signInAnonymously> | null = null

export function observeAuthState(callback: (user: User | null) => void) {
  return onAuthStateChanged(getFirebaseAuth(), callback)
}

export async function signInWithGoogle() {
  const provider = new GoogleAuthProvider()
  provider.setCustomParameters({ prompt: 'select_account' })
  return signInWithPopup(getFirebaseAuth(), provider)
}

export async function signInWithEmailPassword(email: string, password: string) {
  return signInWithEmailAndPassword(getFirebaseAuth(), normalizeEmail(email), password)
}

export async function createAccountWithEmailPassword(email: string, password: string) {
  return createUserWithEmailAndPassword(getFirebaseAuth(), normalizeEmail(email), password)
}

export async function sendPasswordReset(email: string) {
  return sendPasswordResetEmail(getFirebaseAuth(), normalizeEmail(email))
}

export async function changeCurrentPassword(password: string) {
  return updatePassword(requireCurrentUser(), password)
}

export async function changeCurrentEmail(email: string) {
  return updateEmail(requireCurrentUser(), normalizeEmail(email))
}

export async function sendCurrentEmailVerification() {
  return sendEmailVerification(requireCurrentUser())
}

export async function refreshCurrentUser() {
  const user = requireCurrentUser()
  await reload(user)
  return getFirebaseAuth().currentUser
}

export async function addGoogleLogin() {
  const provider = new GoogleAuthProvider()
  provider.setCustomParameters({ prompt: 'select_account' })
  return linkWithPopup(requireCurrentUser(), provider)
}

export async function addEmailPasswordLogin(email: string, password: string) {
  const credential = EmailAuthProvider.credential(normalizeEmail(email), password)
  return linkWithCredential(requireCurrentUser(), credential)
}

export async function reauthenticateWithEmailPassword(email: string, password: string) {
  const credential = EmailAuthProvider.credential(normalizeEmail(email), password)
  return reauthenticateWithCredential(requireCurrentUser(), credential)
}

export async function reauthenticateWithGoogle() {
  const provider = new GoogleAuthProvider()
  provider.setCustomParameters({ prompt: 'select_account' })
  return reauthenticateWithPopup(requireCurrentUser(), provider)
}

export async function removeLoginProvider(providerId: string) {
  const user = requireCurrentUser()
  if (user.providerData.length <= 1) throw new Error('ログイン方法は1つ以上残してください。')
  return unlink(user, providerId)
}

export async function signInAnonymouslyForDevelopment() {
  if (!import.meta.env.DEV || !import.meta.env.VITE_FIREBASE_AUTH_EMULATOR_URL) {
    throw new Error('開発用匿名ログインはAuth Emulator接続時のみ利用できます。')
  }
  return signInAnonymously(getFirebaseAuth())
}

export async function signOutCurrentUser() {
  await signOut(getFirebaseAuth())
}

export async function getCurrentIdToken() {
  const auth = getFirebaseAuth()
  let user = auth.currentUser
  if (!user && import.meta.env.DEV && import.meta.env.VITE_FIREBASE_AUTH_EMULATOR_URL) {
    localSignInPromise ??= signInAnonymously(auth)
    user = (await localSignInPromise).user
  }
  return user ? user.getIdToken() : null
}

function requireCurrentUser() {
  const user = getFirebaseAuth().currentUser
  if (!user) throw new Error('ログインが必要です。')
  return user
}

function normalizeEmail(email: string) {
  return email.trim().toLowerCase()
}
