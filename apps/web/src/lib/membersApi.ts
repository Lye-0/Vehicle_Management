import { apiFetch } from './api'

export type MemberRole = 'owner' | 'admin' | 'employee'
export type MemberStatus = 'active' | 'suspended'

export type MemberRecord = {
  uid: string
  displayName: string
  email: string | null
  role: MemberRole
  status: MemberStatus
  mustChangePassword: boolean
  isSelf: boolean
}

export type MembersResponse = {
  currentRole: MemberRole
  members: MemberRecord[]
}

export async function fetchMembers() {
  return apiFetch<MembersResponse>('/api/organization/members')
}

export async function createMember(input: { displayName: string; email: string }) {
  return apiFetch<{ member: MemberRecord; temporaryPassword: string }>('/api/organization/members', {
    method: 'POST',
    body: JSON.stringify(input),
  })
}

export async function updateMember(uid: string, input: { displayName?: string; role?: Exclude<MemberRole, 'owner'>; status?: MemberStatus }) {
  return apiFetch<MembersResponse>(`/api/organization/members/${encodeURIComponent(uid)}`, {
    method: 'PATCH',
    body: JSON.stringify(input),
  })
}

export async function removeMemberFromOrganization(uid: string) {
  return apiFetch<MembersResponse>(`/api/organization/members/${encodeURIComponent(uid)}`, { method: 'DELETE' })
}
