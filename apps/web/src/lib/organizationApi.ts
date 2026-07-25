import { apiFetch } from './api'

export type OrganizationRole = 'owner' | 'admin' | 'employee'

export type OrganizationMembership = {
  id: string
  organizationId: string
  name: string
  role: OrganizationRole
  status: string
}

export type AuthSession = {
  user: {
    uid: string
    email: string | null
    displayName: string | null
    emailVerified: boolean
  }
  profile: {
    displayName: string
    email: string | null
    role: string
  } | null
  organizations: OrganizationMembership[]
  setupAvailable: boolean
  mustChangePassword: boolean
}

export async function fetchAuthSession() {
  return apiFetch<AuthSession>('/api/auth/me')
}

export async function completeOrganizationSetup(name: string, setupKey: string) {
  const response = await apiFetch<{ session: AuthSession; organizationId: string }>('/api/setup/organization', {
    method: 'POST',
    body: JSON.stringify({ name, setupKey }),
  })
  return response.session
}

export async function completeInitialPasswordChange() {
  const response = await apiFetch<{ session: AuthSession }>('/api/auth/password/complete', { method: 'POST' })
  return response.session
}
