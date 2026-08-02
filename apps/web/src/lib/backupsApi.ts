import { apiFetch } from './api'

export type BackupRecord = {
  id: string
  fileCount: number
  rowCount: number
  status: string
  trigger: string
  protectedUntil: string | null
  keepForever: boolean
  createdAt: string
  updatedAt: string
}

export type BackupList = {
  canManage: boolean
  backups: BackupRecord[]
}

export type BackupDestination = 'b2' | 'pc' | 'both'
export type BackupFrequency = 'daily' | 'weekly'

export type BackupSettings = {
  autoEnabled: boolean
  frequency: BackupFrequency
  destination: BackupDestination
  retentionDays: number
  archiveRetentionDays: number
  pcRetentionDays: number
}

export type BackupExport = {
  version: 1
  id: string
  organizationId: string
  organizationName: string
  createdAt: string
  tables: Record<string, unknown[]>
  files: Array<{ id: string; fileName: string; contentType: string; data: string }>
}

export async function fetchBackups() {
  return apiFetch<BackupList>('/api/backups')
}

export async function fetchBackupSettings() {
  return apiFetch<{ canManage: boolean; settings: BackupSettings }>('/api/backups/settings')
}

export async function updateBackupSettings(settings: BackupSettings) {
  const response = await apiFetch<{ settings: BackupSettings }>('/api/backups/settings', { method: 'PATCH', body: JSON.stringify({ settings }) })
  return response.settings
}

export async function createBackup() {
  return apiFetch<{ backup: BackupRecord }>('/api/backups', { method: 'POST' })
}

export async function exportBackup() {
  const response = await apiFetch<{ backup: BackupExport }>('/api/backups/export')
  return response.backup
}

export async function restoreBackup(id: string) {
  return apiFetch<{ restored: boolean; backupId: string; safetyBackupId: string; rowCount: number }>(`/api/backups/${encodeURIComponent(id)}/restore`, { method: 'POST', body: JSON.stringify({ confirmId: id }) })
}

export async function restoreImportedBackup(backup: BackupExport) {
  return apiFetch<{ restored: boolean; backupId: string; safetyBackupId: string; rowCount: number }>('/api/backups/import', { method: 'POST', body: JSON.stringify({ backup }) })
}

export async function updateBackupRetention(id: string, keepForever: boolean) {
  return apiFetch<{ updated: boolean; keepForever: boolean }>(`/api/backups/${encodeURIComponent(id)}`, { method: 'PATCH', body: JSON.stringify({ keepForever }) })
}

export async function deleteBackup(id: string) {
  return apiFetch<{ deleted: boolean }>(`/api/backups/${encodeURIComponent(id)}`, { method: 'DELETE' })
}
