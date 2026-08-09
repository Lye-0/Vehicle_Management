import { and, asc, desc, eq, lte } from 'drizzle-orm'
import type { BatchItem } from 'drizzle-orm/batch'
import { appSettings, backupRecords, customers, inspectionSchedules, maintenanceDocuments, maintenanceItems, mileageHistories, organizations, paymentEntries, paymentRecords, salesDocumentItems, salesDocuments, sharedSchedules, vehicleFiles, vehicles } from '@vehicle-management/database'
import { requireOrganizationContext, requireOrganizationPermission } from '../auth/organization'
import { UnauthorizedError } from '../auth/firebase'
import { addDays, loadBackupSettings, normalizeBackupSettings, saveBackupSettings, type BackupSettings } from '../backup-settings'
import { purgeExpiredArchivedDocuments } from '../document-archive'
import { createDatabase } from '../db/client'
import { HttpError, jsonResponse, readJson } from '../http'
import { loadOrganizationPermissions } from '../organization-permissions'
import { attachmentKind, assertAttachmentSignature, assertSupportedAttachmentContentType, createVehicleFileObjectKey, isSafePathSegment, isSupportedAttachmentContentType, type SupportedAttachmentContentType } from '../lib/file-validation'
import { createB2Storage } from '../storage/b2'

const preRestoreProtectionDays = 30
const maximumBackupFileBytes = 20 * 1024 * 1024
const maximumBackupTotalFileBytes = 50 * 1024 * 1024
const maximumBackupRequestBytes = 80 * 1024 * 1024

export async function handleBackupRoutes(request: Request, env: Env): Promise<Response | null> {
  const pathname = new URL(request.url).pathname.replace(/\/$/, '') || '/'
  const collectionPath = pathname === '/api/backups'
  const settingsPath = pathname === '/api/backups/settings'
  const exportPath = pathname === '/api/backups/export'
  const importPath = pathname === '/api/backups/import'
  const restoreMatch = pathname.match(/^\/api\/backups\/([^/]+)\/restore$/)
  const itemMatch = pathname.match(/^\/api\/backups\/([^/]+)$/)
  if (!collectionPath && !settingsPath && !exportPath && !importPath && !restoreMatch && !itemMatch) return null

  try {
    const database = createDatabase(env.DB)
    if (settingsPath && request.method === 'GET') return await getSettings(request, env, database)
    if (settingsPath && request.method === 'PATCH') return await updateSettings(request, env, database)
    if (collectionPath && request.method === 'GET') return await listBackups(request, env, database)
    if (collectionPath && request.method === 'POST') return await createBackup(request, env, database)
    if (exportPath && request.method === 'GET') return await exportBackup(request, env, database)
    if (importPath && request.method === 'POST') return await importBackup(request, env, database)
    if (restoreMatch && request.method === 'POST') return await restoreBackup(request, env, database, decodeURIComponent(restoreMatch[1]))
    if (itemMatch && request.method === 'PATCH') return await updateBackup(request, env, database, decodeURIComponent(itemMatch[1]))
    if (itemMatch && request.method === 'DELETE') return await deleteBackup(request, env, database, decodeURIComponent(itemMatch[1]))
    throw new HttpError(405, 'この操作には対応していません。')
  } catch (error) {
    if (error instanceof UnauthorizedError) return jsonResponse({ error: error.message }, 401, env)
    if (error instanceof HttpError) return jsonResponse({ error: error.message }, error.status, env)
    console.error(error)
    return jsonResponse({ error: 'バックアップ処理に失敗しました。' }, 500, env)
  }
}

async function getSettings(request: Request, env: Env, database: ReturnType<typeof createDatabase>) {
  const context = await requireOrganizationContext(request, env, database)
  const permissions = await loadOrganizationPermissions(database, context.organization.organizationId)
  const canManageCreateRestore = isAdmin(context.organization.role) || permissions.employeeCanCreateRestoreBackup
  const canManageRetention = isAdmin(context.organization.role) || permissions.employeeCanManageBackupRetention
  return jsonResponse({ canManage: canManageCreateRestore || canManageRetention, canManageCreateRestore, canManageRetention, settings: await loadBackupSettings(database, context.organization.organizationId) }, 200, env)
}

async function updateSettings(request: Request, env: Env, database: ReturnType<typeof createDatabase>) {
  const context = await requireOrganizationContext(request, env, database)
  const body = await readJson(request)
  const organizationId = context.organization.organizationId
  const current = await loadBackupSettings(database, organizationId)
  const next = normalizeBackupSettings(body.settings)
  if (!isAdmin(context.organization.role)) {
    const permissions = await loadOrganizationPermissions(database, organizationId)
    const canManageCreateRestore = permissions.employeeCanCreateRestoreBackup
    const canManageRetention = permissions.employeeCanManageBackupRetention
    if (!canManageCreateRestore && hasBackupCreationSettingChange(current, next)) throw new HttpError(403, 'バックアップ作成設定の変更は組織の権限設定で許可されていません。')
    if (!canManageRetention && hasBackupRetentionSettingChange(current, next)) throw new HttpError(403, 'バックアップ保持設定の変更は組織の権限設定で許可されていません。')
  }
  return jsonResponse({ settings: await saveBackupSettings(database, organizationId, next) }, 200, env)
}

async function listBackups(request: Request, env: Env, database: ReturnType<typeof createDatabase>) {
  const context = await requireOrganizationContext(request, env, database)
  const permissions = await loadOrganizationPermissions(database, context.organization.organizationId)
  const records = await database.select().from(backupRecords).where(eq(backupRecords.organizationId, context.organization.organizationId)).orderBy(desc(backupRecords.createdAt)).all()
  const canManageCreateRestore = isAdmin(context.organization.role) || permissions.employeeCanCreateRestoreBackup
  const canManageRetention = isAdmin(context.organization.role) || permissions.employeeCanManageBackupRetention
  return jsonResponse({ canManage: canManageCreateRestore || canManageRetention, canManageCreateRestore, canManageRetention, backups: records.map(serializeBackup) }, 200, env)
}

async function createBackup(request: Request, env: Env, database: ReturnType<typeof createDatabase>) {
  const context = await requireOrganizationPermission(request, env, database, 'employeeCanCreateRestoreBackup')
  const body = await readOptionalJson(request)
  const note = normalizeBackupNote(body.note)
  const backup = await createBackupForOrganization(env, database, context.organization.organizationId, context.organization.name, { trigger: 'manual', note })
  return jsonResponse({ backup }, 201, env)
}

async function exportBackup(request: Request, env: Env, database: ReturnType<typeof createDatabase>) {
  const context = await requireOrganizationPermission(request, env, database, 'employeeCanCreateRestoreBackup')
  const storage = getStorage(env)
  const snapshot = await loadSnapshot(database, context.organization.organizationId, crypto.randomUUID(), context.organization.name)
  snapshot.note = normalizeBackupNote(new URL(request.url).searchParams.get('note'))
  const files: BackupExportFile[] = []
  let totalFileBytes = 0
  for (const file of snapshot.tables.vehicleFiles) {
    if (file.sizeBytes > maximumBackupFileBytes || totalFileBytes + file.sizeBytes > maximumBackupTotalFileBytes) throw new HttpError(413, 'バックアップ対象の添付ファイル合計が上限を超えています。')
    const response = await storage.getObject(file.objectKey)
    const bytes = await readResponseBytes(response, maximumBackupFileBytes, maximumBackupTotalFileBytes - totalFileBytes)
    totalFileBytes += bytes.byteLength
    files.push({ id: file.id, fileName: file.fileName, contentType: file.contentType, data: arrayBufferToBase64(bytes) })
  }
  return jsonResponse({ backup: { ...snapshot, files } }, 200, env)
}

async function importBackup(request: Request, env: Env, database: ReturnType<typeof createDatabase>) {
  const context = await requireOrganizationPermission(request, env, database, 'employeeCanCreateRestoreBackup')
  const body = await readJson(request, maximumBackupRequestBytes)
  const source = assertExportManifest(body.backup, context.organization.organizationId)
  const safetyBackup = await createSafetyBackup(env, database, context.organization.organizationId, context.organization.name)
  const storage = getStorage(env)
  const restoreId = crypto.randomUUID()
  const stagedKeys: string[] = []
  try {
    const filesById = new Map(source.files.map((file) => [file.id, file]))
    const vehicleFilesWithKeys = []
    for (const file of source.tables.vehicleFiles) {
      const sourceFile = filesById.get(file.id)
      if (!sourceFile) throw new HttpError(400, `添付ファイルが見つかりません: ${file.fileName}`)
      const backupObjectKey = `imports/${context.organization.organizationId}/${restoreId}/files/${file.id}`
      const contentType = assertSupportedAttachmentContentType(sourceFile.contentType)
      const fileBody = base64ToArrayBuffer(sourceFile.data, contentType)
      const objectKey = createVehicleFileObjectKey(context.organization.organizationId, file.vehicleId, file.id, file.fileName)
      const fileName = file.fileName.trim().slice(0, 120) || 'file'
      await storage.putObject({ key: backupObjectKey, body: fileBody, contentType })
      stagedKeys.push(backupObjectKey)
      vehicleFilesWithKeys.push({ ...file, objectKey, fileName, contentType, fileKind: attachmentKind(contentType), sizeBytes: fileBody.byteLength, backupObjectKey })
    }
    const manifest: BackupManifest = { ...source, tables: { ...source.tables, vehicleFiles: vehicleFilesWithKeys } }
    await restoreManifest(env, database, context.organization.organizationId, manifest)
    return jsonResponse({ restored: true, backupId: source.id, safetyBackupId: safetyBackup.id, rowCount: countRows(manifest.tables) }, 200, env)
  } catch (error) {
    await rollbackToSafetyBackup(env, database, context.organization.organizationId, safetyBackup.id)
    throw error instanceof HttpError ? error : new HttpError(500, 'PCバックアップからの復元に失敗しました。復元前の状態へ戻しました。')
  } finally {
    await deleteObjects(storage, stagedKeys)
  }
}

async function restoreBackup(request: Request, env: Env, database: ReturnType<typeof createDatabase>, id: string) {
  const context = await requireOrganizationPermission(request, env, database, 'employeeCanCreateRestoreBackup')
  const body = await readOptionalJson(request)
  if (body.confirmId !== id) throw new HttpError(400, '復元確認が一致しません。')
  const record = await database.select().from(backupRecords).where(and(eq(backupRecords.id, id), eq(backupRecords.organizationId, context.organization.organizationId))).get()
  if (!record) throw new HttpError(404, 'バックアップが見つかりません。')
  const safetyBackup = await createSafetyBackup(env, database, context.organization.organizationId, context.organization.name)
  try {
    const manifest = await readManifest(getStorage(env), record.manifestKey, context.organization.organizationId)
    await restoreManifest(env, database, context.organization.organizationId, manifest)
    return jsonResponse({ restored: true, backupId: id, safetyBackupId: safetyBackup.id, rowCount: countRows(manifest.tables) }, 200, env)
  } catch (error) {
    await rollbackToSafetyBackup(env, database, context.organization.organizationId, safetyBackup.id)
    if (error instanceof HttpError) throw error
    throw new HttpError(500, 'バックアップの復元に失敗しました。復元前の状態へ戻しました。')
  }
}

async function deleteBackup(request: Request, env: Env, database: ReturnType<typeof createDatabase>, id: string) {
  const context = await requireOrganizationPermission(request, env, database, 'employeeCanManageBackupRetention')
  const record = await database.select().from(backupRecords).where(and(eq(backupRecords.id, id), eq(backupRecords.organizationId, context.organization.organizationId))).get()
  if (!record) throw new HttpError(404, 'バックアップが見つかりません。')
  await deleteBackupRecord(database, env, record)
  return jsonResponse({ deleted: true }, 200, env)
}

async function updateBackup(request: Request, env: Env, database: ReturnType<typeof createDatabase>, id: string) {
  const context = await requireOrganizationPermission(request, env, database, 'employeeCanManageBackupRetention')
  const body = await readJson(request)
  if (typeof body.keepForever !== 'boolean') throw new HttpError(400, '永久保存の指定が不正です。')
  const record = await database.select().from(backupRecords).where(and(eq(backupRecords.id, id), eq(backupRecords.organizationId, context.organization.organizationId))).get()
  if (!record) throw new HttpError(404, 'バックアップが見つかりません。')
  const settings = await loadBackupSettings(database, context.organization.organizationId)
  const updatedAt = new Date()
  const protectedUntil = body.keepForever ? null : record.keepForever ? addDays(updatedAt, settings.retentionDays) : record.protectedUntil
  await database.update(backupRecords).set({ keepForever: body.keepForever, protectedUntil, updatedAt: updatedAt.toISOString() }).where(and(eq(backupRecords.id, id), eq(backupRecords.organizationId, context.organization.organizationId))).run()
  return jsonResponse({ updated: true, keepForever: body.keepForever, protectedUntil }, 200, env)
}

export async function runScheduledBackupMaintenance(env: Env, scheduledTime = Date.now()) {
  const database = createDatabase(env.DB)
  const now = new Date(scheduledTime)
  const organizationRows = await database.select({ id: organizations.id, name: organizations.name }).from(organizations).all()
  for (const organization of organizationRows) {
    const settings = await loadBackupSettings(database, organization.id)
    try {
      getStorage(env)
      if (settings.autoEnabled && ['b2', 'both'].includes(settings.destination)) {
        const latestAutomatic = await database.select().from(backupRecords).where(and(eq(backupRecords.organizationId, organization.id), eq(backupRecords.trigger, 'automatic'))).orderBy(desc(backupRecords.createdAt)).get()
        const intervalDays = settings.frequency === 'weekly' ? 7 : 1
        const due = !latestAutomatic || new Date(latestAutomatic.createdAt).getTime() <= now.getTime() - intervalDays * 86_400_000
        if (due) {
          await createBackupForOrganization(env, database, organization.id, organization.name, { trigger: 'automatic' })
        }
      }
      await cleanupBackups(database, env, organization.id, settings, now)
    } catch (error) {
      console.error(`[backup] scheduled backup failed for ${organization.id}`, error)
    }
    try {
      await purgeExpiredArchivedDocuments(database, organization.id, now.toISOString())
    } catch (error) {
      console.error(`[archive] scheduled purge failed for ${organization.id}`, error)
    }
  }
}

async function createSafetyBackup(env: Env, database: ReturnType<typeof createDatabase>, organizationId: string, organizationName: string) {
  return createBackupForOrganization(env, database, organizationId, organizationName, { trigger: 'pre-restore', protectedUntil: addDays(new Date(), preRestoreProtectionDays), keepForever: false })
}

async function rollbackToSafetyBackup(env: Env, database: ReturnType<typeof createDatabase>, organizationId: string, safetyBackupId: string) {
  try {
    const safety = await database.select().from(backupRecords).where(and(eq(backupRecords.id, safetyBackupId), eq(backupRecords.organizationId, organizationId))).get()
    if (!safety) return
    const manifest = await readManifest(getStorage(env), safety.manifestKey, organizationId)
    await restoreManifest(env, database, organizationId, manifest)
  } catch (rollbackError) {
    console.error(`[backup] rollback failed for ${organizationId}`, rollbackError)
  }
}

async function createBackupForOrganization(env: Env, database: ReturnType<typeof createDatabase>, organizationId: string, organizationName: string, options: { trigger: string; note?: string; protectedUntil?: string; keepForever?: boolean }) {
  const storage = getStorage(env)
  const id = crypto.randomUUID()
  const manifestKey = `backups/${organizationId}/${id}/manifest.json`
  const snapshot = await loadSnapshot(database, organizationId, id, organizationName)
  snapshot.note = options.note ?? ''
  const copiedKeys: string[] = []
  try {
    for (const file of snapshot.tables.vehicleFiles) {
      const backupObjectKey = `backups/${organizationId}/${id}/files/${file.id}`
      await storage.copyObject(file.objectKey, backupObjectKey)
      file.backupObjectKey = backupObjectKey
      copiedKeys.push(backupObjectKey)
    }
    await storage.putText(manifestKey, JSON.stringify(snapshot), 'application/json; charset=utf-8')
    await database.insert(backupRecords).values({ id, organizationId, manifestKey, fileCount: snapshot.tables.vehicleFiles.length, rowCount: countRows(snapshot.tables), status: 'completed', trigger: options.trigger, note: snapshot.note, protectedUntil: options.protectedUntil ?? null, keepForever: options.keepForever ?? false }).run()
  } catch (error) {
    await deleteObjects(storage, copiedKeys)
    try { await storage.deleteObject(manifestKey) } catch { /* manifest may not exist */ }
    throw error
  }
  return serializeBackup({ id, organizationId, manifestKey, fileCount: snapshot.tables.vehicleFiles.length, rowCount: countRows(snapshot.tables), status: 'completed', trigger: options.trigger, note: snapshot.note, protectedUntil: options.protectedUntil ?? null, keepForever: options.keepForever ?? false, createdAt: snapshot.createdAt, updatedAt: snapshot.createdAt })
}

async function restoreManifest(env: Env, database: ReturnType<typeof createDatabase>, organizationId: string, manifest: BackupManifest) {
  assertManifest(manifest, organizationId)
  const storage = getStorage(env)
  for (const file of manifest.tables.vehicleFiles) {
    if (!file.backupObjectKey) throw new HttpError(400, `バックアップ内の添付ファイル情報が不正です: ${file.fileName}`)
    await storage.copyObject(file.backupObjectKey, file.objectKey)
  }
  const currentFiles = await database.select({ objectKey: vehicleFiles.objectKey }).from(vehicleFiles).where(eq(vehicleFiles.organizationId, organizationId)).all()
  const writes = buildRestoreStatements(database, organizationId, manifest)
  await database.batch(writes as [BatchItem<'sqlite'>, ...BatchItem<'sqlite'>[]])
  const backupObjectKeys = new Set(manifest.tables.vehicleFiles.map((file) => file.objectKey))
  await deleteObjects(storage, currentFiles.map((file) => file.objectKey).filter((key) => !backupObjectKeys.has(key)))
}

async function cleanupBackups(database: ReturnType<typeof createDatabase>, env: Env, organizationId: string, settings: BackupSettings, now: Date) {
  const cutoff = new Date(now)
  cutoff.setUTCDate(cutoff.getUTCDate() - settings.retentionDays)
  const records = await database.select().from(backupRecords).where(and(eq(backupRecords.organizationId, organizationId), lte(backupRecords.createdAt, cutoff.toISOString()))).all()
  for (const record of records) {
    if (record.keepForever || (record.protectedUntil && record.protectedUntil > now.toISOString())) continue
    await deleteBackupRecord(database, env, record)
  }
}

async function deleteBackupRecord(database: ReturnType<typeof createDatabase>, env: Env, record: typeof backupRecords.$inferSelect) {
  const storage = getStorage(env)
  const manifest = await readManifest(storage, record.manifestKey, record.organizationId)
  await deleteObjects(storage, manifest.tables.vehicleFiles.map((file) => file.backupObjectKey))
  await storage.deleteObject(record.manifestKey)
  await database.delete(backupRecords).where(and(eq(backupRecords.id, record.id), eq(backupRecords.organizationId, record.organizationId))).run()
}

async function loadSnapshot(database: ReturnType<typeof createDatabase>, organizationId: string, id: string, organizationName: string): Promise<BackupManifest> {
  const [customerRows, vehicleRows, fileRows, salesRows, salesItemRows, maintenanceRows, maintenanceItemRows, paymentRows, paymentEntryRows, mileageHistoryRows, scheduleRows, sharedScheduleRows, settingsRows] = await Promise.all([
    database.select().from(customers).where(eq(customers.organizationId, organizationId)).orderBy(asc(customers.createdAt)).all(),
    database.select().from(vehicles).where(eq(vehicles.organizationId, organizationId)).orderBy(asc(vehicles.createdAt)).all(),
    database.select().from(vehicleFiles).where(eq(vehicleFiles.organizationId, organizationId)).orderBy(asc(vehicleFiles.createdAt)).all(),
    database.select().from(salesDocuments).where(eq(salesDocuments.organizationId, organizationId)).orderBy(asc(salesDocuments.createdAt)).all(),
    database.select().from(salesDocumentItems).where(eq(salesDocumentItems.organizationId, organizationId)).orderBy(asc(salesDocumentItems.sortOrder)).all(),
    database.select().from(maintenanceDocuments).where(eq(maintenanceDocuments.organizationId, organizationId)).orderBy(asc(maintenanceDocuments.createdAt)).all(),
    database.select().from(maintenanceItems).where(eq(maintenanceItems.organizationId, organizationId)).orderBy(asc(maintenanceItems.sortOrder)).all(),
    database.select().from(paymentRecords).where(eq(paymentRecords.organizationId, organizationId)).orderBy(asc(paymentRecords.createdAt)).all(),
    database.select().from(paymentEntries).where(eq(paymentEntries.organizationId, organizationId)).orderBy(asc(paymentEntries.createdAt)).all(),
    database.select().from(mileageHistories).where(eq(mileageHistories.organizationId, organizationId)).orderBy(asc(mileageHistories.createdAt)).all(),
    database.select().from(inspectionSchedules).where(eq(inspectionSchedules.organizationId, organizationId)).orderBy(asc(inspectionSchedules.createdAt)).all(),
    database.select().from(sharedSchedules).where(eq(sharedSchedules.organizationId, organizationId)).orderBy(asc(sharedSchedules.createdAt)).all(),
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
      mileageHistories: mileageHistoryRows,
      inspectionSchedules: scheduleRows,
      sharedSchedules: sharedScheduleRows,
      appSettings: settingsRows,
    },
  }
}

function buildRestoreStatements(database: ReturnType<typeof createDatabase>, organizationId: string, manifest: BackupManifest) {
  const writes: BatchItem<'sqlite'>[] = [
    database.delete(mileageHistories).where(eq(mileageHistories.organizationId, organizationId)),
    database.delete(paymentEntries).where(eq(paymentEntries.organizationId, organizationId)),
    database.delete(paymentRecords).where(eq(paymentRecords.organizationId, organizationId)),
    database.delete(sharedSchedules).where(eq(sharedSchedules.organizationId, organizationId)),
    database.delete(salesDocumentItems).where(eq(salesDocumentItems.organizationId, organizationId)),
    database.delete(maintenanceItems).where(eq(maintenanceItems.organizationId, organizationId)),
    database.delete(salesDocuments).where(eq(salesDocuments.organizationId, organizationId)),
    database.delete(maintenanceDocuments).where(eq(maintenanceDocuments.organizationId, organizationId)),
    database.delete(vehicleFiles).where(eq(vehicleFiles.organizationId, organizationId)),
    database.delete(inspectionSchedules).where(eq(inspectionSchedules.organizationId, organizationId)),
    database.delete(vehicles).where(eq(vehicles.organizationId, organizationId)),
    database.delete(customers).where(eq(customers.organizationId, organizationId)),
    database.delete(appSettings).where(eq(appSettings.organizationId, organizationId)),
  ]
  for (const row of manifest.tables.customers) writes.push(database.insert(customers).values(row))
  for (const row of manifest.tables.vehicles) writes.push(database.insert(vehicles).values(row))
  for (const row of manifest.tables.vehicleFiles) {
    const { backupObjectKey: _backupObjectKey, ...file } = row
    writes.push(database.insert(vehicleFiles).values(file))
  }
  for (const row of manifest.tables.salesDocuments) writes.push(database.insert(salesDocuments).values(row))
  for (const row of manifest.tables.salesDocumentItems) writes.push(database.insert(salesDocumentItems).values(row))
  for (const row of manifest.tables.maintenanceDocuments) writes.push(database.insert(maintenanceDocuments).values(row))
  for (const row of manifest.tables.maintenanceItems) writes.push(database.insert(maintenanceItems).values(row))
  for (const row of manifest.tables.paymentRecords) writes.push(database.insert(paymentRecords).values(row))
  for (const row of manifest.tables.paymentEntries ?? []) writes.push(database.insert(paymentEntries).values(row))
  for (const row of manifest.tables.mileageHistories ?? []) writes.push(database.insert(mileageHistories).values(row))
  for (const row of manifest.tables.inspectionSchedules) writes.push(database.insert(inspectionSchedules).values(row))
  for (const row of manifest.tables.sharedSchedules ?? []) writes.push(database.insert(sharedSchedules).values(row))
  for (const row of manifest.tables.appSettings) writes.push(database.insert(appSettings).values(row))
  return writes
}

async function readManifest(storage: ReturnType<typeof createB2Storage>, key: string, organizationId: string) {
  const response = await storage.getObject(key)
  const value: unknown = await response.json().catch(() => null)
  return assertManifest(value, organizationId, { requireBackupObjectKeys: true })
}

function assertManifest(value: unknown, organizationId: string, options: { requireBackupObjectKeys: boolean } = { requireBackupObjectKeys: true }): BackupManifest {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new HttpError(400, 'バックアップマニフェストが不正です。')
  const manifest = value as Partial<BackupManifest>
  if (manifest.version !== 1 || typeof manifest.id !== 'string' || manifest.organizationId !== organizationId || !manifest.tables || !Array.isArray(manifest.tables.customers) || !Array.isArray(manifest.tables.vehicles) || !Array.isArray(manifest.tables.vehicleFiles) || !Array.isArray(manifest.tables.salesDocuments) || !Array.isArray(manifest.tables.salesDocumentItems) || !Array.isArray(manifest.tables.maintenanceDocuments) || !Array.isArray(manifest.tables.maintenanceItems) || !Array.isArray(manifest.tables.paymentRecords) || !Array.isArray(manifest.tables.inspectionSchedules) || !Array.isArray(manifest.tables.appSettings)) {
    throw new HttpError(400, 'このバックアップは現在の組織へ復元できません。')
  }
  const tables = manifest.tables as BackupManifest['tables']
  const organizationRows = [tables.customers, tables.vehicles, tables.vehicleFiles, tables.salesDocuments, tables.salesDocumentItems, tables.maintenanceDocuments, tables.maintenanceItems, tables.paymentRecords, tables.paymentEntries ?? [], tables.mileageHistories ?? [], tables.inspectionSchedules, tables.sharedSchedules ?? [], tables.appSettings]
  if (organizationRows.some((rows) => rows.some((row) => !row || typeof row !== 'object' || row.organizationId !== organizationId))) throw new HttpError(400, 'バックアップ内の組織情報が不正です。')
  for (const file of tables.vehicleFiles) {
    if (!file || typeof file !== 'object' || typeof file.id !== 'string' || !isSafePathSegment(file.id) || typeof file.vehicleId !== 'string' || !isSafePathSegment(file.vehicleId) || typeof file.fileName !== 'string' || !file.fileName.trim() || file.fileName.length > 120 || !isSupportedAttachmentContentType(file.contentType) || file.fileKind !== attachmentKind(file.contentType) || !Number.isSafeInteger(file.sizeBytes) || file.sizeBytes < 0 || file.sizeBytes > maximumBackupFileBytes || !isVehicleFileObjectKey(file.objectKey, organizationId, file.vehicleId, file.id)) throw new HttpError(400, 'バックアップ内の添付ファイル情報が不正です。')
    if (options.requireBackupObjectKeys && (typeof file.backupObjectKey !== 'string' || !isBackupObjectKey(file.backupObjectKey, organizationId, file.id))) throw new HttpError(400, 'バックアップ内の添付ファイル保存先が不正です。')
  }
  return manifest as BackupManifest
}

function assertExportManifest(value: unknown, organizationId: string): BackupExport {
  const manifest = assertManifest(value, organizationId, { requireBackupObjectKeys: false })
  const files = value && typeof value === 'object' && !Array.isArray(value) && Array.isArray((value as { files?: unknown }).files) ? (value as { files: unknown[] }).files : []
  let totalFileBytes = 0
  if (files.length !== manifest.tables.vehicleFiles.length || files.some((file) => !file || typeof file !== 'object' || typeof (file as BackupExportFile).id !== 'string' || typeof (file as BackupExportFile).data !== 'string' || typeof (file as BackupExportFile).contentType !== 'string' || typeof (file as BackupExportFile).fileName !== 'string')) throw new HttpError(400, 'PCバックアップの添付ファイル情報が不正です。')
  const fileIds = new Set<string>()
  for (const file of files as BackupExportFile[]) {
    if (fileIds.has(file.id)) throw new HttpError(400, 'PCバックアップの添付ファイルIDが重複しています。')
    fileIds.add(file.id)
    const row = manifest.tables.vehicleFiles.find((vehicleFile) => vehicleFile.id === file.id)
    if (!row || row.contentType !== file.contentType) throw new HttpError(400, 'PCバックアップの添付ファイル種別が一致しません。')
    if (!isSupportedAttachmentContentType(file.contentType)) throw new HttpError(400, 'PCバックアップの添付ファイル種別が不正です。')
    const byteLength = base64ByteLength(file.data)
    if (byteLength > maximumBackupFileBytes || totalFileBytes + byteLength > maximumBackupTotalFileBytes) throw new HttpError(413, 'PCバックアップの添付ファイル合計が上限を超えています。')
    const bytes = base64ToArrayBuffer(file.data, file.contentType)
    if (bytes.byteLength !== byteLength) throw new HttpError(400, 'PCバックアップの添付ファイルが不正です。')
    totalFileBytes += byteLength
  }
  return { ...manifest, files: files as BackupExportFile[] }
}

function getStorage(env: Env) {
  try { return createB2Storage(env) } catch { throw new HttpError(503, 'B2のバックアップ設定がありません。') }
}

function isVehicleFileObjectKey(objectKey: string, organizationId: string, vehicleId: string, fileId: string) {
  const prefix = `organizations/${organizationId}/vehicles/${vehicleId}/${fileId}-`
  if (!objectKey.startsWith(prefix) || objectKey.length > prefix.length + 120) return false
  const fileName = objectKey.slice(prefix.length)
  return Boolean(fileName) && !fileName.includes('/') && !fileName.includes('\\') && !fileName.includes('..')
}

function isBackupObjectKey(objectKey: string, organizationId: string, fileId: string) {
  const match = objectKey.match(/^(backups|imports)\/([^/]+)\/([^/]+)\/files\/([^/]+)$/u)
  return Boolean(match && match[2] === organizationId && match[4] === fileId && isSafePathSegment(match[3]))
}

async function readResponseBytes(response: Response, maximumBytes: number, remainingTotalBytes: number) {
  const limit = Math.min(maximumBytes, remainingTotalBytes)
  const contentLengthHeader = response.headers.get('Content-Length')
  if (contentLengthHeader !== null) {
    if (!/^\d+$/u.test(contentLengthHeader)) throw new HttpError(502, 'B2のファイルサイズが不正です。')
    const contentLength = Number(contentLengthHeader)
    if (!Number.isSafeInteger(contentLength) || contentLength > limit) throw new HttpError(413, 'バックアップ対象の添付ファイル合計が上限を超えています。')
  }
  if (!response.body) throw new HttpError(502, 'B2のファイル本文を取得できません。')
  const reader = response.body.getReader()
  const chunks: Uint8Array[] = []
  let totalBytes = 0
  try {
    while (true) {
      const next = await reader.read()
      if (next.done) break
      totalBytes += next.value.byteLength
      if (totalBytes > limit) {
        await reader.cancel()
        throw new HttpError(413, 'バックアップ対象の添付ファイル合計が上限を超えています。')
      }
      chunks.push(next.value)
    }
  } finally {
    reader.releaseLock()
  }
  const bytes = new Uint8Array(totalBytes)
  let offset = 0
  for (const chunk of chunks) {
    bytes.set(chunk, offset)
    offset += chunk.byteLength
  }
  return bytes
}

async function deleteObjects(storage: ReturnType<typeof createB2Storage>, keys: string[]) {
  for (const key of keys) {
    try { await storage.deleteObject(key) } catch (error) { console.error(error) }
  }
}

function countRows(tables: BackupManifest['tables']) {
  return Object.values(tables).reduce((total, rows) => total + (rows?.length ?? 0), 0)
}

function serializeBackup(record: { id: string; organizationId: string; manifestKey: string; fileCount: number; rowCount: number; status: string; trigger: string; note: string; protectedUntil: string | null; keepForever: boolean; createdAt: string; updatedAt: string }) {
  return { id: record.id, fileCount: record.fileCount, rowCount: record.rowCount, status: record.status, trigger: record.trigger, note: record.note, protectedUntil: record.protectedUntil, keepForever: record.keepForever, createdAt: record.createdAt, updatedAt: record.updatedAt }
}

function isAdmin(role: string) {
  return role === 'owner' || role === 'admin'
}

async function readOptionalJson(request: Request) {
  if (!request.body) return {} as Record<string, unknown>
  try {
    return await readJson(request)
  } catch (error) {
    if (error instanceof HttpError && error.status === 400) return {} as Record<string, unknown>
    throw error
  }
}

function hasBackupCreationSettingChange(current: BackupSettings, next: BackupSettings) {
  return current.autoEnabled !== next.autoEnabled || current.frequency !== next.frequency || current.destination !== next.destination
}

function hasBackupRetentionSettingChange(current: BackupSettings, next: BackupSettings) {
  return current.retentionDays !== next.retentionDays || current.archiveRetentionDays !== next.archiveRetentionDays || current.pcRetentionDays !== next.pcRetentionDays
}

function arrayBufferToBase64(buffer: ArrayBuffer | Uint8Array) {
  const bytes = buffer instanceof Uint8Array ? buffer : new Uint8Array(buffer)
  let binary = ''
  const chunkSize = 0x8000
  for (let index = 0; index < bytes.length; index += chunkSize) binary += String.fromCharCode(...bytes.subarray(index, index + chunkSize))
  return btoa(binary)
}

function base64ToArrayBuffer(value: string, contentType?: SupportedAttachmentContentType) {
  let binary: string
  try { binary = atob(value) } catch { throw new HttpError(400, 'PCバックアップの添付ファイルが不正です。') }
  if (binary.length > maximumBackupFileBytes) throw new HttpError(413, 'PCバックアップの添付ファイルが上限を超えています。')
  const bytes = Uint8Array.from(binary, (character) => character.charCodeAt(0))
  if (contentType) assertAttachmentSignature(bytes, contentType)
  return bytes.buffer
}

function base64ByteLength(value: string) {
  if (!/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/u.test(value)) throw new HttpError(400, 'PCバックアップの添付ファイルが不正です。')
  const padding = value.endsWith('==') ? 2 : value.endsWith('=') ? 1 : 0
  return Math.max(0, (value.length * 3) / 4 - padding)
}

type BackupManifest = {
  version: 1
  id: string
  organizationId: string
  organizationName: string
  createdAt: string
  note?: string
  tables: {
    customers: Array<typeof customers.$inferSelect>
    vehicles: Array<typeof vehicles.$inferSelect>
    vehicleFiles: Array<typeof vehicleFiles.$inferSelect & { backupObjectKey: string }>
    salesDocuments: Array<typeof salesDocuments.$inferSelect>
    salesDocumentItems: Array<typeof salesDocumentItems.$inferSelect>
    maintenanceDocuments: Array<typeof maintenanceDocuments.$inferSelect>
    maintenanceItems: Array<typeof maintenanceItems.$inferSelect>
    paymentRecords: Array<typeof paymentRecords.$inferSelect>
    paymentEntries: Array<typeof paymentEntries.$inferSelect>
    mileageHistories: Array<typeof mileageHistories.$inferSelect>
    inspectionSchedules: Array<typeof inspectionSchedules.$inferSelect>
    sharedSchedules?: Array<typeof sharedSchedules.$inferSelect>
    appSettings: Array<typeof appSettings.$inferSelect>
  }
}

type BackupExportFile = { id: string; fileName: string; contentType: string; data: string }
type BackupExport = BackupManifest & { files: BackupExportFile[] }

function normalizeBackupNote(value: unknown) {
  if (value === null || value === undefined || value === '') return ''
  if (typeof value !== 'string') throw new HttpError(400, 'バックアップメモは文字列で入力してください。')
  const note = value.trim()
  if (note.length > 500) throw new HttpError(400, 'バックアップメモは500文字以内で入力してください。')
  return note
}
