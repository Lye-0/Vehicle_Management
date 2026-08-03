import { and, eq } from 'drizzle-orm'
import { appSettings } from '@vehicle-management/database'
import type { Database } from './db/client'

export const backupSettingsKey = 'backup'

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

export const defaultBackupSettings: BackupSettings = {
  autoEnabled: false,
  frequency: 'daily',
  destination: 'b2',
  retentionDays: 30,
  archiveRetentionDays: 30,
  pcRetentionDays: 30,
}

export async function loadBackupSettings(database: Database, organizationId: string): Promise<BackupSettings> {
  const row = await database.select({ value: appSettings.value }).from(appSettings).where(and(eq(appSettings.organizationId, organizationId), eq(appSettings.key, backupSettingsKey))).get()
  return normalizeBackupSettings(parseJson(row?.value))
}

export async function saveBackupSettings(database: Database, organizationId: string, value: unknown) {
  const settings = normalizeBackupSettings(value)
  const now = new Date().toISOString()
  const existing = await database.select({ key: appSettings.key }).from(appSettings).where(and(eq(appSettings.organizationId, organizationId), eq(appSettings.key, backupSettingsKey))).get()
  const serialized = JSON.stringify(settings)
  if (existing) {
    await database.update(appSettings).set({ value: serialized, updatedAt: now }).where(and(eq(appSettings.organizationId, organizationId), eq(appSettings.key, backupSettingsKey))).run()
  } else {
    await database.insert(appSettings).values({ organizationId, key: backupSettingsKey, value: serialized, updatedAt: now }).run()
  }
  return settings
}

export function normalizeBackupSettings(value: unknown): BackupSettings {
  const record = value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : {}
  return {
    autoEnabled: record.autoEnabled === true,
    frequency: record.frequency === 'weekly' ? 'weekly' : 'daily',
    destination: record.destination === 'pc' || record.destination === 'both' ? record.destination : 'b2',
    retentionDays: integerValue(record.retentionDays, defaultBackupSettings.retentionDays, 7, 3650),
    archiveRetentionDays: integerValue(record.archiveRetentionDays, defaultBackupSettings.archiveRetentionDays, 1, 3650),
    pcRetentionDays: integerValue(record.pcRetentionDays, defaultBackupSettings.pcRetentionDays, 1, 3650),
  }
}

export function addDays(value: Date, days: number) {
  const next = new Date(value)
  next.setUTCDate(next.getUTCDate() + days)
  return next.toISOString()
}

function parseJson(value: string | undefined) {
  if (!value) return null
  try { return JSON.parse(value) as unknown } catch { return null }
}

function integerValue(value: unknown, fallback: number, minimum: number, maximum: number) {
  const number = typeof value === 'number' ? value : Number(value)
  if (!Number.isFinite(number)) return fallback
  return Math.min(maximum, Math.max(minimum, Math.round(number)))
}
