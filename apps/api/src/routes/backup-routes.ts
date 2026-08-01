import { and, asc, desc, eq } from 'drizzle-orm'
import { appSettings, backupRecords, customers, inspectionSchedules, maintenanceDocuments, maintenanceItems, paymentEntries, paymentRecords, salesDocumentItems, salesDocuments, vehicleFiles, vehicles } from '@vehicle-management/database'
import { requireAdminOrganizationContext, requireOrganizationContext } from '../auth/organization'
import { UnauthorizedError } from '../auth/firebase'
import { createDatabase } from '../db/client'
import { HttpError, jsonResponse, readJson } from '../http'
import { createB2Storage } from '../storage/b2'

export async function handleBackupRoutes(request: Request, env: Env): Promise<Response | null> {
  const pathname = new URL(request.url).pathname.replace(/\/$/, '') || '/'
  const collectionPath = pathname === '/api/backups'
  const restoreMatch = pathname.match(/^\/api\/backups\/([^/]+)\/restore$/)
  const itemMatch = pathname.match(/^\/api\/backups\/([^/]+)$/)
  if (!collectionPath && !restoreMatch && !itemMatch) return null

  try {
    const database = createDatabase(env.DB)
    if (collectionPath && request.method === 'GET') {
      const context = await requireOrganizationContext(request, env, database)
      const records = await database.select().from(backupRecords).where(eq(backupRecords.organizationId, context.organization.organizationId)).orderBy(desc(backupRecords.createdAt)).all()
      return jsonResponse({ canManage: context.organization.role === 'owner' || context.organization.role === 'admin', backups: records.map(serializeBackup) }, 200, env)
    }
    if (collectionPath && request.method === 'POST') return await createBackup(request, env, database)
    if (restoreMatch && request.method === 'POST') return await restoreBackup(request, env, database, decodeURIComponent(restoreMatch[1]))
    if (itemMatch && request.method === 'DELETE') return await deleteBackup(request, env, database, decodeURIComponent(itemMatch[1]))
    throw new HttpError(405, 'この操作には対応していません。')
  } catch (error) {
    if (error instanceof UnauthorizedError) return jsonResponse({ error: error.message }, 401, env)
    if (error instanceof HttpError) return jsonResponse({ error: error.message }, error.status, env)
    console.error(error)
    return jsonResponse({ error: 'バックアップ処理に失敗しました。' }, 500, env)
  }
}

async function createBackup(request: Request, env: Env, database: ReturnType<typeof createDatabase>) {
  const context = await requireAdminOrganizationContext(request, env, database)
  const storage = getStorage(env)
  const organizationId = context.organization.organizationId
  const id = crypto.randomUUID()
  const manifestKey = `backups/${organizationId}/${id}/manifest.json`
  const snapshot = await loadSnapshot(database, organizationId, id, context.organization.name)
  const copiedKeys: string[] = []
  try {
    for (const file of snapshot.tables.vehicleFiles) {
      const backupObjectKey = `backups/${organizationId}/${id}/files/${file.id}`
      await storage.copyObject(file.objectKey, backupObjectKey)
      file.backupObjectKey = backupObjectKey
      copiedKeys.push(backupObjectKey)
    }
    await storage.putText(manifestKey, JSON.stringify(snapshot), 'application/json; charset=utf-8')
    await database.insert(backupRecords).values({ id, organizationId, manifestKey, fileCount: snapshot.tables.vehicleFiles.length, rowCount: countRows(snapshot.tables), status: 'completed' }).run()
  } catch (error) {
    await deleteObjects(storage, copiedKeys)
    try { await storage.deleteObject(manifestKey) } catch { /* manifest may not exist */ }
    throw error
  }
  return jsonResponse({ backup: serializeBackup({ id, organizationId, manifestKey, fileCount: snapshot.tables.vehicleFiles.length, rowCount: countRows(snapshot.tables), status: 'completed', createdAt: snapshot.createdAt, updatedAt: snapshot.createdAt }) }, 201, env)
}

async function restoreBackup(request: Request, env: Env, database: ReturnType<typeof createDatabase>, id: string) {
  const context = await requireAdminOrganizationContext(request, env, database)
  const body = await readJson(request).catch(() => ({} as Record<string, unknown>))
  if (body.confirmId !== id) throw new HttpError(400, '復元確認が一致しません。')
  const record = await database.select().from(backupRecords).where(and(eq(backupRecords.id, id), eq(backupRecords.organizationId, context.organization.organizationId))).get()
  if (!record) throw new HttpError(404, 'バックアップが見つかりません。')
  const storage = getStorage(env)
  const manifest = await readManifest(storage, record.manifestKey)
  if (manifest.organizationId !== context.organization.organizationId || manifest.version !== 1) throw new HttpError(400, 'このバックアップは現在の組織へ復元できません。')
  for (const file of manifest.tables.vehicleFiles) await storage.copyObject(file.backupObjectKey, file.objectKey)
  const currentFiles = await database.select({ objectKey: vehicleFiles.objectKey }).from(vehicleFiles).where(eq(vehicleFiles.organizationId, context.organization.organizationId)).all()
  await clearOrganizationData(database, context.organization.organizationId)
  await insertSnapshot(database, manifest)
  const backupObjectKeys = new Set(manifest.tables.vehicleFiles.map((file) => file.objectKey))
  await deleteObjects(storage, currentFiles.map((file) => file.objectKey).filter((key) => !backupObjectKeys.has(key)))
  return jsonResponse({ restored: true, backupId: id, rowCount: countRows(manifest.tables) }, 200, env)
}

async function deleteBackup(request: Request, env: Env, database: ReturnType<typeof createDatabase>, id: string) {
  const context = await requireAdminOrganizationContext(request, env, database)
  const record = await database.select().from(backupRecords).where(and(eq(backupRecords.id, id), eq(backupRecords.organizationId, context.organization.organizationId))).get()
  if (!record) throw new HttpError(404, 'バックアップが見つかりません。')
  const storage = getStorage(env)
  const manifest = await readManifest(storage, record.manifestKey)
  await deleteObjects(storage, manifest.tables.vehicleFiles.map((file) => file.backupObjectKey))
  await storage.deleteObject(record.manifestKey)
  await database.delete(backupRecords).where(and(eq(backupRecords.id, id), eq(backupRecords.organizationId, context.organization.organizationId))).run()
  return jsonResponse({ deleted: true }, 200, env)
}

async function loadSnapshot(database: ReturnType<typeof createDatabase>, organizationId: string, id: string, organizationName: string): Promise<BackupManifest> {
  const [customerRows, vehicleRows, fileRows, salesRows, salesItemRows, maintenanceRows, maintenanceItemRows, paymentRows, paymentEntryRows, scheduleRows, settingsRows] = await Promise.all([
    database.select().from(customers).where(eq(customers.organizationId, organizationId)).orderBy(asc(customers.createdAt)).all(),
    database.select().from(vehicles).where(eq(vehicles.organizationId, organizationId)).orderBy(asc(vehicles.createdAt)).all(),
    database.select().from(vehicleFiles).where(eq(vehicleFiles.organizationId, organizationId)).orderBy(asc(vehicleFiles.createdAt)).all(),
    database.select().from(salesDocuments).where(eq(salesDocuments.organizationId, organizationId)).orderBy(asc(salesDocuments.createdAt)).all(),
    database.select().from(salesDocumentItems).where(eq(salesDocumentItems.organizationId, organizationId)).orderBy(asc(salesDocumentItems.sortOrder)).all(),
    database.select().from(maintenanceDocuments).where(eq(maintenanceDocuments.organizationId, organizationId)).orderBy(asc(maintenanceDocuments.createdAt)).all(),
    database.select().from(maintenanceItems).where(eq(maintenanceItems.organizationId, organizationId)).orderBy(asc(maintenanceItems.sortOrder)).all(),
    database.select().from(paymentRecords).where(eq(paymentRecords.organizationId, organizationId)).orderBy(asc(paymentRecords.createdAt)).all(),
    database.select().from(paymentEntries).where(eq(paymentEntries.organizationId, organizationId)).orderBy(asc(paymentEntries.createdAt)).all(),
    database.select().from(inspectionSchedules).where(eq(inspectionSchedules.organizationId, organizationId)).orderBy(asc(inspectionSchedules.createdAt)).all(),
    database.select().from(appSettings).where(eq(appSettings.organizationId, organizationId)).orderBy(asc(appSettings.key)).all(),
  ])
  const createdAt = new Date().toISOString()
  return {
    version: 1,
    id,
    organizationId,
    organizationName,
    createdAt,
    tables: {
      customers: customerRows,
      vehicles: vehicleRows,
      vehicleFiles: fileRows.map((file) => ({ ...file, backupObjectKey: '' })),
      salesDocuments: salesRows,
      salesDocumentItems: salesItemRows,
      maintenanceDocuments: maintenanceRows,
      maintenanceItems: maintenanceItemRows,
      paymentRecords: paymentRows,
      paymentEntries: paymentEntryRows,
      inspectionSchedules: scheduleRows,
      appSettings: settingsRows,
    },
  }
}

async function clearOrganizationData(database: ReturnType<typeof createDatabase>, organizationId: string) {
  await database.delete(paymentEntries).where(eq(paymentEntries.organizationId, organizationId)).run()
  await database.delete(paymentRecords).where(eq(paymentRecords.organizationId, organizationId)).run()
  await database.delete(salesDocumentItems).where(eq(salesDocumentItems.organizationId, organizationId)).run()
  await database.delete(maintenanceItems).where(eq(maintenanceItems.organizationId, organizationId)).run()
  await database.delete(salesDocuments).where(eq(salesDocuments.organizationId, organizationId)).run()
  await database.delete(maintenanceDocuments).where(eq(maintenanceDocuments.organizationId, organizationId)).run()
  await database.delete(vehicleFiles).where(eq(vehicleFiles.organizationId, organizationId)).run()
  await database.delete(inspectionSchedules).where(eq(inspectionSchedules.organizationId, organizationId)).run()
  await database.delete(vehicles).where(eq(vehicles.organizationId, organizationId)).run()
  await database.delete(customers).where(eq(customers.organizationId, organizationId)).run()
  await database.delete(appSettings).where(eq(appSettings.organizationId, organizationId)).run()
}

async function insertSnapshot(database: ReturnType<typeof createDatabase>, manifest: BackupManifest) {
  for (const row of manifest.tables.customers) await database.insert(customers).values(row).run()
  for (const row of manifest.tables.vehicles) await database.insert(vehicles).values(row).run()
  for (const row of manifest.tables.vehicleFiles) {
    const { backupObjectKey: _backupObjectKey, ...file } = row
    await database.insert(vehicleFiles).values(file).run()
  }
  for (const row of manifest.tables.salesDocuments) await database.insert(salesDocuments).values(row).run()
  for (const row of manifest.tables.salesDocumentItems) await database.insert(salesDocumentItems).values(row).run()
  for (const row of manifest.tables.maintenanceDocuments) await database.insert(maintenanceDocuments).values(row).run()
  for (const row of manifest.tables.maintenanceItems) await database.insert(maintenanceItems).values(row).run()
  for (const row of manifest.tables.paymentRecords) await database.insert(paymentRecords).values(row).run()
  for (const row of manifest.tables.paymentEntries ?? []) await database.insert(paymentEntries).values(row).run()
  for (const row of manifest.tables.inspectionSchedules) await database.insert(inspectionSchedules).values(row).run()
  for (const row of manifest.tables.appSettings) await database.insert(appSettings).values(row).run()
}

async function readManifest(storage: ReturnType<typeof createB2Storage>, key: string) {
  const response = await storage.getObject(key)
  const value: unknown = await response.json().catch(() => null)
  if (!value || typeof value !== 'object') throw new HttpError(400, 'バックアップマニフェストが不正です。')
  return value as BackupManifest
}

function getStorage(env: Env) {
  try {
    return createB2Storage(env)
  } catch {
    throw new HttpError(503, 'B2のバックアップ設定がありません。')
  }
}

async function deleteObjects(storage: ReturnType<typeof createB2Storage>, keys: string[]) {
  for (const key of keys) {
    try { await storage.deleteObject(key) } catch (error) { console.error(error) }
  }
}

function countRows(tables: BackupManifest['tables']) {
  return Object.values(tables).reduce((total, rows) => total + rows.length, 0)
}

function serializeBackup(record: { id: string; organizationId: string; manifestKey: string; fileCount: number; rowCount: number; status: string; createdAt: string; updatedAt: string }) {
  return { id: record.id, fileCount: record.fileCount, rowCount: record.rowCount, status: record.status, createdAt: record.createdAt, updatedAt: record.updatedAt }
}

type BackupManifest = {
  version: 1
  id: string
  organizationId: string
  organizationName: string
  createdAt: string
  tables: {
    customers: Array<typeof customers.$inferSelect>
    vehicles: Array<typeof vehicles.$inferSelect>
    vehicleFiles: Array<typeof vehicleFiles.$inferSelect & { backupObjectKey: string }>
    salesDocuments: Array<typeof salesDocuments.$inferSelect>
    salesDocumentItems: Array<typeof salesDocumentItems.$inferSelect>
    maintenanceDocuments: Array<typeof maintenanceDocuments.$inferSelect>
    maintenanceItems: Array<typeof maintenanceItems.$inferSelect>
    paymentRecords: Array<typeof paymentRecords.$inferSelect>
    paymentEntries?: Array<typeof paymentEntries.$inferSelect>
    inspectionSchedules: Array<typeof inspectionSchedules.$inferSelect>
    appSettings: Array<typeof appSettings.$inferSelect>
  }
}
