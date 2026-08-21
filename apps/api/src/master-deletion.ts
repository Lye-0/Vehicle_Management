import { and, eq, inArray, isNull, sql, type SQL } from 'drizzle-orm'
import type { BatchItem } from 'drizzle-orm/batch'
import { customers, inspectionSchedules, maintenanceDocuments, salesDocuments, vehicleFiles, vehicles } from '@vehicle-management/database'
import type { Database } from './db/client'
import { HttpError } from './http'
import { addDays } from './backup-settings'

export type MasterDeletionKind = 'customer' | 'vehicle'

export type MasterDeletionImpact = {
  kind: MasterDeletionKind
  id: string
  label: string
  vehicleCount: number
  documentCount: number
  archivedDocumentCount: number
  inspectionCount: number
  attachmentCount: number
}

type DeletionTarget = {
  kind: MasterDeletionKind
  id: string
  customerId: string
  label: string
  vehicleIds: string[]
  expectedUpdatedAt: string | undefined
}

export async function getMasterDeletionImpact(database: Database, organizationId: string, kind: MasterDeletionKind, id: string): Promise<MasterDeletionImpact> {
  const target = await loadDeletionTarget(database, organizationId, kind, id)
  const [salesRows, maintenanceRows, scheduleRows, fileRows] = await Promise.all([
    database.select({ id: salesDocuments.id, archivedAt: salesDocuments.archivedAt }).from(salesDocuments).where(and(eq(salesDocuments.organizationId, organizationId), documentTarget(salesDocuments, target))).all(),
    database.select({ id: maintenanceDocuments.id, archivedAt: maintenanceDocuments.archivedAt }).from(maintenanceDocuments).where(and(eq(maintenanceDocuments.organizationId, organizationId), documentTarget(maintenanceDocuments, target))).all(),
    target.vehicleIds.length > 0
      ? database.select({ id: inspectionSchedules.id }).from(inspectionSchedules).where(and(eq(inspectionSchedules.organizationId, organizationId), inArray(inspectionSchedules.vehicleId, target.vehicleIds), isNull(inspectionSchedules.deletionBatchId))).all()
      : Promise.resolve([]),
    target.vehicleIds.length > 0
      ? database.select({ id: vehicleFiles.id }).from(vehicleFiles).where(and(eq(vehicleFiles.organizationId, organizationId), inArray(vehicleFiles.vehicleId, target.vehicleIds))).all()
      : Promise.resolve([]),
  ])
  const documents = [...salesRows, ...maintenanceRows]
  return {
    kind,
    id,
    label: target.label,
    vehicleCount: target.vehicleIds.length,
    documentCount: documents.length,
    archivedDocumentCount: documents.filter((document) => document.archivedAt !== null).length,
    inspectionCount: scheduleRows.length,
    attachmentCount: fileRows.length,
  }
}

export async function deleteMaster(
  database: Database,
  organizationId: string,
  kind: MasterDeletionKind,
  id: string,
  userId: string,
  retentionDays: number,
  expectedUpdatedAt?: string,
) {
  const target = await loadDeletionTarget(database, organizationId, kind, id, expectedUpdatedAt)
  const now = new Date()
  const updatedAt = now.toISOString()
  const deletionBatchId = crypto.randomUUID()
  const purgeAt = addDays(now, retentionDays)
  const guard = deletionGuard(target, organizationId)
  const statements: BatchItem<'sqlite'>[] = []

  statements.push(
    database.update(salesDocuments).set({
      status: 'アーカイブ済み',
      archivedAt: updatedAt,
      archivedPreviousStatus: sql`${salesDocuments.status}`,
      archivedBy: userId,
      purgeAt,
      keepForever: false,
      archiveReason: kind === 'customer' ? 'customer_deleted' : 'vehicle_deleted',
      deletionBatchId,
      updatedAt,
    }).where(and(
      eq(salesDocuments.organizationId, organizationId),
      documentTarget(salesDocuments, target),
      isNull(salesDocuments.archivedAt),
      guard,
    )),
  )
  statements.push(
    database.update(maintenanceDocuments).set({
      status: 'アーカイブ済み',
      archivedAt: updatedAt,
      archivedPreviousStatus: sql`${maintenanceDocuments.status}`,
      archivedBy: userId,
      purgeAt,
      keepForever: false,
      archiveReason: kind === 'customer' ? 'customer_deleted' : 'vehicle_deleted',
      deletionBatchId,
      updatedAt,
    }).where(and(
      eq(maintenanceDocuments.organizationId, organizationId),
      documentTarget(maintenanceDocuments, target),
      isNull(maintenanceDocuments.archivedAt),
      guard,
    )),
  )
  statements.push(
    database.update(salesDocuments).set({ archiveReason: kind === 'customer' ? 'customer_deleted' : 'vehicle_deleted', deletionBatchId, updatedAt }).where(and(
      eq(salesDocuments.organizationId, organizationId),
      documentTarget(salesDocuments, target),
      sql`${salesDocuments.archivedAt} IS NOT NULL`,
      guard,
    )),
  )
  statements.push(
    database.update(maintenanceDocuments).set({ archiveReason: kind === 'customer' ? 'customer_deleted' : 'vehicle_deleted', deletionBatchId, updatedAt }).where(and(
      eq(maintenanceDocuments.organizationId, organizationId),
      documentTarget(maintenanceDocuments, target),
      sql`${maintenanceDocuments.archivedAt} IS NOT NULL`,
      guard,
    )),
  )
  if (target.vehicleIds.length > 0) {
    statements.push(
      database.update(inspectionSchedules).set({ deletionBatchId, updatedAt }).where(and(
        eq(inspectionSchedules.organizationId, organizationId),
        inArray(inspectionSchedules.vehicleId, target.vehicleIds),
        isNull(inspectionSchedules.deletionBatchId),
        guard,
      )),
    )
    statements.push(
      database.update(vehicles).set({ deletedAt: updatedAt, deletedBy: userId, deletionBatchId, updatedAt }).where(and(
        eq(vehicles.organizationId, organizationId),
        inArray(vehicles.id, target.vehicleIds),
        isNull(vehicles.deletedAt),
        guard,
      )),
    )
  }
  if (kind === 'customer') {
    statements.push(
      database.update(customers).set({ deletedAt: updatedAt, deletedBy: userId, deletionBatchId, updatedAt }).where(and(
        eq(customers.id, target.customerId),
        eq(customers.organizationId, organizationId),
        isNull(customers.deletedAt),
        guard,
      )),
    )
  }

  const results = await database.batch(statements as [BatchItem<'sqlite'>, ...BatchItem<'sqlite'>[]])
  const finalResult = results.at(-1)
  if (!finalResult || finalResult.meta.changes !== 1) throw new HttpError(409, '対象データが他の端末で更新されています。再読み込みしてから、もう一度お試しください。')
  return { deletionBatchId, customerId: target.customerId, vehicleIds: target.vehicleIds }
}

async function loadDeletionTarget(database: Database, organizationId: string, kind: MasterDeletionKind, id: string, expectedUpdatedAt?: string): Promise<DeletionTarget> {
  if (kind === 'customer') {
    const customer = await database.select({ id: customers.id, name: customers.name, updatedAt: customers.updatedAt }).from(customers).where(and(eq(customers.id, id), eq(customers.organizationId, organizationId), isNull(customers.deletedAt))).get()
    if (!customer) throw new HttpError(404, '顧客が見つかりません。')
    if (expectedUpdatedAt && expectedUpdatedAt !== customer.updatedAt) throw new HttpError(409, '顧客情報が他の端末で更新されています。再読み込みしてください。')
    const vehicleRows = await database.select({ id: vehicles.id }).from(vehicles).where(and(eq(vehicles.customerId, customer.id), eq(vehicles.organizationId, organizationId), isNull(vehicles.deletedAt))).all()
    return { kind, id, customerId: customer.id, label: customer.name, vehicleIds: vehicleRows.map((vehicle) => vehicle.id), expectedUpdatedAt }
  }

  const vehicle = await database.select({ id: vehicles.id, customerId: vehicles.customerId, maker: vehicles.maker, name: vehicles.name, updatedAt: vehicles.updatedAt }).from(vehicles).where(and(eq(vehicles.id, id), eq(vehicles.organizationId, organizationId), isNull(vehicles.deletedAt))).get()
  if (!vehicle) throw new HttpError(404, '車両が見つかりません。')
  if (expectedUpdatedAt && expectedUpdatedAt !== vehicle.updatedAt) throw new HttpError(409, '車両情報が他の端末で更新されています。再読み込みしてください。')
  const customer = await database.select({ id: customers.id }).from(customers).where(and(eq(customers.id, vehicle.customerId), eq(customers.organizationId, organizationId), isNull(customers.deletedAt))).get()
  if (!customer) throw new HttpError(409, 'この車両の顧客情報が見つかりません。')
  return { kind, id, customerId: vehicle.customerId, label: [vehicle.maker, vehicle.name].filter(Boolean).join(' ') || '車両', vehicleIds: [vehicle.id], expectedUpdatedAt }
}

function documentTarget(table: typeof salesDocuments | typeof maintenanceDocuments, target: DeletionTarget): SQL {
  return target.kind === 'customer' ? eq(table.customerId, target.customerId) : eq(table.vehicleId, target.id)
}

function deletionGuard(target: DeletionTarget, organizationId: string): SQL {
  if (target.kind === 'customer' && target.expectedUpdatedAt) {
    return sql`EXISTS (SELECT 1 FROM customers WHERE id = ${target.customerId} AND organization_id = ${organizationId} AND deleted_at IS NULL AND updated_at = ${target.expectedUpdatedAt})`
  }
  if (target.kind === 'vehicle' && target.expectedUpdatedAt) {
    return sql`EXISTS (SELECT 1 FROM vehicles WHERE id = ${target.id} AND organization_id = ${organizationId} AND deleted_at IS NULL AND updated_at = ${target.expectedUpdatedAt})`
  }
  return sql`1 = 1`
}
