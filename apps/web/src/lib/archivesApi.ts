import { apiFetch } from './api'

export type ArchiveKind = 'sales' | 'maintenance'

export type ArchiveRecord = {
  id: string
  kind: ArchiveKind
  number: string
  type: string
  category: string
  status: string
  customerName: string
  vehicle: string
  issuedAt: string
  archivedAt: string | null
  purgeAt: string | null
  keepForever: boolean
  total: number
}

export type ArchiveList = {
  canManage: boolean
  archives: ArchiveRecord[]
}

export async function fetchArchives(query = '') {
  const suffix = query.trim() ? `?q=${encodeURIComponent(query.trim())}` : ''
  return apiFetch<ArchiveList>(`/api/archives${suffix}`)
}

export async function restoreArchive(record: Pick<ArchiveRecord, 'kind' | 'id'>) {
  return apiFetch<{ restored: boolean }>(`/api/archives/${record.kind}/${encodeURIComponent(record.id)}/restore`, { method: 'POST' })
}

export async function deleteArchive(record: Pick<ArchiveRecord, 'kind' | 'id'>) {
  return apiFetch<{ deleted: boolean }>(`/api/archives/${record.kind}/${encodeURIComponent(record.id)}`, { method: 'DELETE' })
}

export async function updateArchiveRetention(record: Pick<ArchiveRecord, 'kind' | 'id'>, keepForever: boolean) {
  return apiFetch<{ updated: boolean; keepForever: boolean; purgeAt: string | null }>(`/api/archives/${record.kind}/${encodeURIComponent(record.id)}`, { method: 'PATCH', body: JSON.stringify({ keepForever }) })
}
