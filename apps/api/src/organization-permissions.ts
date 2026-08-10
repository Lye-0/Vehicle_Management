import { eq } from 'drizzle-orm'
import { organizationPermissions } from '@vehicle-management/database'
import type { Database } from './db/client'

export const defaultOrganizationPermissions: OrganizationPermissions = {
  employeeCanExportCsv: true,
  employeeCanEditShop: true,
  employeeCanEditTax: true,
  employeeCanCreateRestoreBackup: true,
  employeeCanManageBackupRetention: false,
  employeeCanManageArchiveRetention: false,
}

export type OrganizationPermissions = {
  employeeCanExportCsv: boolean
  employeeCanEditShop: boolean
  employeeCanEditTax: boolean
  employeeCanCreateRestoreBackup: boolean
  employeeCanManageBackupRetention: boolean
  employeeCanManageArchiveRetention: boolean
}

export async function loadOrganizationPermissions(database: Database, organizationId: string): Promise<OrganizationPermissions> {
  const row = await database.select().from(organizationPermissions).where(eq(organizationPermissions.organizationId, organizationId)).get()
  return normalizeOrganizationPermissions(row)
}

export async function saveOrganizationPermissions(database: Database, organizationId: string, value: unknown): Promise<OrganizationPermissions> {
  const permissions = normalizeOrganizationPermissions(value)
  const now = new Date().toISOString()
  const existing = await database.select({ organizationId: organizationPermissions.organizationId }).from(organizationPermissions).where(eq(organizationPermissions.organizationId, organizationId)).get()
  if (existing) {
    await database.update(organizationPermissions).set({ ...permissions, updatedAt: now }).where(eq(organizationPermissions.organizationId, organizationId)).run()
  } else {
    await database.insert(organizationPermissions).values({ organizationId, ...permissions, updatedAt: now }).run()
  }
  return permissions
}

export function normalizeOrganizationPermissions(value: unknown): OrganizationPermissions {
  const record = value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : {}
  return {
    employeeCanExportCsv: booleanValue(record.employeeCanExportCsv, defaultOrganizationPermissions.employeeCanExportCsv),
    employeeCanEditShop: booleanValue(record.employeeCanEditShop, defaultOrganizationPermissions.employeeCanEditShop),
    employeeCanEditTax: booleanValue(record.employeeCanEditTax, defaultOrganizationPermissions.employeeCanEditTax),
    employeeCanCreateRestoreBackup: booleanValue(record.employeeCanCreateRestoreBackup, defaultOrganizationPermissions.employeeCanCreateRestoreBackup),
    employeeCanManageBackupRetention: booleanValue(record.employeeCanManageBackupRetention, defaultOrganizationPermissions.employeeCanManageBackupRetention),
    employeeCanManageArchiveRetention: booleanValue(record.employeeCanManageArchiveRetention, defaultOrganizationPermissions.employeeCanManageArchiveRetention),
  }
}

export function canManageArchiveRetention(role: string, permissions: OrganizationPermissions) {
  return role === 'owner' || role === 'admin' || permissions.employeeCanManageArchiveRetention
}

function booleanValue(value: unknown, fallback: boolean) {
  return typeof value === 'boolean' ? value : fallback
}
