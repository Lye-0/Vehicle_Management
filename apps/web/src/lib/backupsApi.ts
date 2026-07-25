import { apiFetch } from './api'

export type BackupRecord = {
  id: string
  fileCount: number
  rowCount: number
  status: string
  createdAt: string
  updatedAt: string
}

export type BackupList = {
  canManage: boolean
  backups: BackupRecord[]
}

export async function fetchBackups() {
  return apiFetch<BackupList>('/api/backups')
}

export async function createBackup() {
  return apiFetch<{ backup: BackupRecord }>('/api/backups', { method: 'POST' })
}

export async function restoreBackup(id: string) {
  return apiFetch<{ restored: boolean; backupId: string; rowCount: number }>(`/api/backups/${encodeURIComponent(id)}/restore`, { method: 'POST', body: JSON.stringify({ confirmId: id }) })
}

export async function deleteBackup(id: string) {
  return apiFetch<{ deleted: boolean }>(`/api/backups/${encodeURIComponent(id)}`, { method: 'DELETE' })
}
