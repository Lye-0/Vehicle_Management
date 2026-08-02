import { and, eq, lte } from 'drizzle-orm'
import { maintenanceDocuments, maintenanceItems, paymentEntries, paymentRecords, salesDocumentItems, salesDocuments } from '@vehicle-management/database'
import type { Database } from './db/client'
import { addDays } from './backup-settings'

export type ArchivedDocumentKind = 'sales' | 'maintenance'

export async function archiveDocument(database: Database, kind: ArchivedDocumentKind, documentId: string, organizationId: string, userId: string, retentionDays: number) {
  const now = new Date()
  const updatedAt = now.toISOString()
  const purgeAt = addDays(now, retentionDays)
  if (kind === 'sales') {
    const current = await database.select().from(salesDocuments).where(and(eq(salesDocuments.id, documentId), eq(salesDocuments.organizationId, organizationId))).get()
    if (!current) return false
    const previousStatus = current.status === 'アーカイブ済み' ? current.archivedPreviousStatus ?? '下書き' : current.status
    await database.update(salesDocuments).set({ status: 'アーカイブ済み', archivedAt: updatedAt, archivedPreviousStatus: previousStatus, archivedBy: userId, purgeAt, keepForever: false, updatedAt }).where(and(eq(salesDocuments.id, documentId), eq(salesDocuments.organizationId, organizationId))).run()
    return true
  }

  const current = await database.select().from(maintenanceDocuments).where(and(eq(maintenanceDocuments.id, documentId), eq(maintenanceDocuments.organizationId, organizationId))).get()
  if (!current) return false
  const previousStatus = current.status === 'アーカイブ済み' ? current.archivedPreviousStatus ?? '下書き' : current.status
  await database.update(maintenanceDocuments).set({ status: 'アーカイブ済み', archivedAt: updatedAt, archivedPreviousStatus: previousStatus, archivedBy: userId, purgeAt, keepForever: false, updatedAt }).where(and(eq(maintenanceDocuments.id, documentId), eq(maintenanceDocuments.organizationId, organizationId))).run()
  return true
}

export async function restoreArchivedDocument(database: Database, kind: ArchivedDocumentKind, documentId: string, organizationId: string) {
  const updatedAt = new Date().toISOString()
  if (kind === 'sales') {
    const current = await database.select().from(salesDocuments).where(and(eq(salesDocuments.id, documentId), eq(salesDocuments.organizationId, organizationId))).get()
    if (!current) return false
    await database.update(salesDocuments).set({ status: current.archivedPreviousStatus && current.archivedPreviousStatus !== 'アーカイブ済み' ? current.archivedPreviousStatus : '下書き', archivedAt: null, archivedPreviousStatus: null, archivedBy: null, purgeAt: null, updatedAt }).where(and(eq(salesDocuments.id, documentId), eq(salesDocuments.organizationId, organizationId))).run()
    return true
  }

  const current = await database.select().from(maintenanceDocuments).where(and(eq(maintenanceDocuments.id, documentId), eq(maintenanceDocuments.organizationId, organizationId))).get()
  if (!current) return false
  await database.update(maintenanceDocuments).set({ status: current.archivedPreviousStatus && current.archivedPreviousStatus !== 'アーカイブ済み' ? current.archivedPreviousStatus : '下書き', archivedAt: null, archivedPreviousStatus: null, archivedBy: null, purgeAt: null, updatedAt }).where(and(eq(maintenanceDocuments.id, documentId), eq(maintenanceDocuments.organizationId, organizationId))).run()
  return true
}

export async function setArchiveKeepForever(database: Database, kind: ArchivedDocumentKind, documentId: string, organizationId: string, keepForever: boolean, retentionDays: number) {
  const updatedAt = new Date().toISOString()
  const purgeAt = keepForever ? null : addDays(new Date(), retentionDays)
  if (kind === 'sales') {
    const result = await database.update(salesDocuments).set({ keepForever, purgeAt, updatedAt }).where(and(eq(salesDocuments.id, documentId), eq(salesDocuments.organizationId, organizationId), eq(salesDocuments.status, 'アーカイブ済み'))).run()
    return { updated: result.meta.changes > 0, purgeAt }
  }
  const result = await database.update(maintenanceDocuments).set({ keepForever, purgeAt, updatedAt }).where(and(eq(maintenanceDocuments.id, documentId), eq(maintenanceDocuments.organizationId, organizationId), eq(maintenanceDocuments.status, 'アーカイブ済み'))).run()
  return { updated: result.meta.changes > 0, purgeAt }
}

export async function permanentlyDeleteArchivedDocument(database: Database, kind: ArchivedDocumentKind, documentId: string, organizationId: string) {
  if (kind === 'sales') {
    const current = await database.select({ id: salesDocuments.id }).from(salesDocuments).where(and(eq(salesDocuments.id, documentId), eq(salesDocuments.organizationId, organizationId), eq(salesDocuments.status, 'アーカイブ済み'))).get()
    if (!current) return false
    await database.delete(paymentEntries).where(and(eq(paymentEntries.organizationId, organizationId), eq(paymentEntries.documentType, '販売請求書'), eq(paymentEntries.documentId, documentId))).run()
    await database.delete(paymentRecords).where(and(eq(paymentRecords.organizationId, organizationId), eq(paymentRecords.documentType, '販売請求書'), eq(paymentRecords.documentId, documentId))).run()
    await database.delete(salesDocumentItems).where(and(eq(salesDocumentItems.organizationId, organizationId), eq(salesDocumentItems.documentId, documentId))).run()
    await database.delete(salesDocuments).where(and(eq(salesDocuments.id, documentId), eq(salesDocuments.organizationId, organizationId))).run()
    return true
  }

  const current = await database.select({ id: maintenanceDocuments.id }).from(maintenanceDocuments).where(and(eq(maintenanceDocuments.id, documentId), eq(maintenanceDocuments.organizationId, organizationId), eq(maintenanceDocuments.status, 'アーカイブ済み'))).get()
  if (!current) return false
  await database.delete(paymentEntries).where(and(eq(paymentEntries.organizationId, organizationId), eq(paymentEntries.documentType, '整備請求書'), eq(paymentEntries.documentId, documentId))).run()
  await database.delete(paymentRecords).where(and(eq(paymentRecords.organizationId, organizationId), eq(paymentRecords.documentType, '整備請求書'), eq(paymentRecords.documentId, documentId))).run()
  await database.delete(maintenanceItems).where(and(eq(maintenanceItems.organizationId, organizationId), eq(maintenanceItems.documentId, documentId))).run()
  await database.delete(maintenanceDocuments).where(and(eq(maintenanceDocuments.id, documentId), eq(maintenanceDocuments.organizationId, organizationId))).run()
  return true
}

export async function purgeExpiredArchivedDocuments(database: Database, organizationId: string, now = new Date().toISOString()) {
  const [sales, maintenance] = await Promise.all([
    database.select({ id: salesDocuments.id }).from(salesDocuments).where(and(eq(salesDocuments.organizationId, organizationId), eq(salesDocuments.status, 'アーカイブ済み'), eq(salesDocuments.keepForever, false), lte(salesDocuments.purgeAt, now))).all(),
    database.select({ id: maintenanceDocuments.id }).from(maintenanceDocuments).where(and(eq(maintenanceDocuments.organizationId, organizationId), eq(maintenanceDocuments.status, 'アーカイブ済み'), eq(maintenanceDocuments.keepForever, false), lte(maintenanceDocuments.purgeAt, now))).all(),
  ])
  let deleted = 0
  for (const document of sales) if (await permanentlyDeleteArchivedDocument(database, 'sales', document.id, organizationId)) deleted += 1
  for (const document of maintenance) if (await permanentlyDeleteArchivedDocument(database, 'maintenance', document.id, organizationId)) deleted += 1
  return deleted
}
