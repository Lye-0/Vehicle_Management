import { GoogleAuthProvider, onAuthStateChanged, signInAnonymously, signInWithPopup, signOut, type User } from 'firebase/auth'
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
