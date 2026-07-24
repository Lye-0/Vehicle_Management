import { GoogleAuthProvider, onAuthStateChanged, signInWithPopup, signOut, type User } from 'firebase/auth'
import { getFirebaseAuth } from './firebase'

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
  const user = getFirebaseAuth().currentUser
  return user ? user.getIdToken() : null
}
