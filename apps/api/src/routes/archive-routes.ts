import { and, desc, eq } from 'drizzle-orm'
import { customers, maintenanceDocuments, salesDocuments, vehicles } from '@vehicle-management/database'
import { UnauthorizedError } from '../auth/firebase'
import { requireAdminOrganizationContext, requireOrganizationContext } from '../auth/organization'
import { loadBackupSettings } from '../backup-settings'
import { archiveDocument, permanentlyDeleteArchivedDocument, restoreArchivedDocument, setArchiveKeepForever, type ArchivedDocumentKind } from '../document-archive'
import { createDatabase } from '../db/client'
import { HttpError, jsonResponse, readJson } from '../http'

export async function handleArchiveRoutes(request: Request, env: Env): Promise<Response | null> {
  const pathname = new URL(request.url).pathname.replace(/\/$/, '') || '/'
  const collectionPath = pathname === '/api/archives'
  const itemMatch = pathname.match(/^\/api\/archives\/(sales|maintenance)\/([^/]+)$/)
  const restoreMatch = pathname.match(/^\/api\/archives\/(sales|maintenance)\/([^/]+)\/restore$/)
  if (!collectionPath && !itemMatch && !restoreMatch) return null

  try {
    const database = createDatabase(env.DB)
    if (collectionPath && request.method === 'GET') return await listArchives(request, env, database)
    if (restoreMatch && request.method === 'POST') return await restoreArchive(request, env, database, restoreMatch[1] as ArchivedDocumentKind, decodeURIComponent(restoreMatch[2]))
    if (itemMatch && request.method === 'PATCH') return await updateArchive(request, env, database, itemMatch[1] as ArchivedDocumentKind, decodeURIComponent(itemMatch[2]))
    if (itemMatch && request.method === 'DELETE') return await deleteArchive(request, env, database, itemMatch[1] as ArchivedDocumentKind, decodeURIComponent(itemMatch[2]))
    throw new HttpError(405, 'この操作には対応していません。')
  } catch (error) {
    if (error instanceof UnauthorizedError) return jsonResponse({ error: error.message }, 401, env)
    if (error instanceof HttpError) return jsonResponse({ error: error.message }, error.status, env)
    console.error(error)
    return jsonResponse({ error: 'アーカイブの処理に失敗しました。' }, 500, env)
  }
}

async function listArchives(request: Request, env: Env, database: ReturnType<typeof createDatabase>) {
  const context = await requireOrganizationContext(request, env, database)
  const query = new URL(request.url).searchParams.get('q')?.trim().toLocaleLowerCase() ?? ''
  const [salesRows, maintenanceRows, customerRows, vehicleRows] = await Promise.all([
    database.select().from(salesDocuments).where(and(eq(salesDocuments.organizationId, context.organization.organizationId), eq(salesDocuments.status, 'アーカイブ済み'))).orderBy(desc(salesDocuments.archivedAt)).all(),
    database.select().from(maintenanceDocuments).where(and(eq(maintenanceDocuments.organizationId, context.organization.organizationId), eq(maintenanceDocuments.status, 'アーカイブ済み'))).orderBy(desc(maintenanceDocuments.archivedAt)).all(),
    database.select().from(customers).where(eq(customers.organizationId, context.organization.organizationId)).all(),
    database.select().from(vehicles).where(eq(vehicles.organizationId, context.organization.organizationId)).all(),
  ])
  const customersById = new Map(customerRows.map((customer) => [customer.id, customer.name]))
  const vehiclesById = new Map(vehicleRows.map((vehicle) => [vehicle.id, `${vehicle.maker ?? ''} ${vehicle.name}`.trim()]))
  const records = [
    ...salesRows.map((document) => ({ id: document.id, kind: 'sales' as const, number: document.number, type: document.type, category: '', status: document.status, customerName: customersById.get(document.customerId) ?? '', vehicle: document.vehicleId ? vehiclesById.get(document.vehicleId) ?? '' : '', issuedAt: document.issuedAt, archivedAt: document.archivedAt, purgeAt: document.purgeAt, keepForever: document.keepForever, total: document.total })),
    ...maintenanceRows.map((document) => ({ id: document.id, kind: 'maintenance' as const, number: document.number, type: document.type, category: document.category, status: document.status, customerName: customersById.get(document.customerId) ?? '', vehicle: vehiclesById.get(document.vehicleId) ?? '', issuedAt: document.issuedAt, archivedAt: document.archivedAt, purgeAt: document.purgeAt, keepForever: document.keepForever, total: document.total })),
  ].filter((record) => {
    if (!query) return true
    return `${record.number} ${record.type} ${record.category} ${record.customerName} ${record.vehicle}`.toLocaleLowerCase().includes(query)
  }).sort((left, right) => (right.archivedAt ?? '').localeCompare(left.archivedAt ?? ''))
  return jsonResponse({ canManage: context.organization.role === 'owner' || context.organization.role === 'admin', archives: records }, 200, env)
}

async function restoreArchive(request: Request, env: Env, database: ReturnType<typeof createDatabase>, kind: ArchivedDocumentKind, documentId: string) {
  const context = await requireOrganizationContext(request, env, database)
  const restored = await restoreArchivedDocument(database, kind, documentId, context.organization.organizationId)
  if (!restored) throw new HttpError(404, 'アーカイブ済み書類が見つかりません。')
  return jsonResponse({ restored: true }, 200, env)
}

async function updateArchive(request: Request, env: Env, database: ReturnType<typeof createDatabase>, kind: ArchivedDocumentKind, documentId: string) {
  const context = await requireOrganizationContext(request, env, database)
  const body = await readJson(request)
  if (typeof body.keepForever !== 'boolean') throw new HttpError(400, '永久保存の指定が不正です。')
  const settings = await loadBackupSettings(database, context.organization.organizationId)
  const updated = await setArchiveKeepForever(database, kind, documentId, context.organization.organizationId, body.keepForever, settings.archiveRetentionDays)
  if (!updated.updated) throw new HttpError(404, 'アーカイブ済み書類が見つかりません。')
  return jsonResponse({ updated: true, keepForever: body.keepForever, purgeAt: updated.purgeAt }, 200, env)
}

async function deleteArchive(request: Request, env: Env, database: ReturnType<typeof createDatabase>, kind: ArchivedDocumentKind, documentId: string) {
  const context = await requireAdminOrganizationContext(request, env, database)
  const deleted = await permanentlyDeleteArchivedDocument(database, kind, documentId, context.organization.organizationId)
  if (!deleted) throw new HttpError(404, 'アーカイブ済み書類が見つかりません。')
  return jsonResponse({ deleted: true }, 200, env)
}

export async function archiveDocumentFromRoute(database: ReturnType<typeof createDatabase>, kind: ArchivedDocumentKind, documentId: string, organizationId: string, userId: string) {
  const settings = await loadBackupSettings(database, organizationId)
  return archiveDocument(database, kind, documentId, organizationId, userId, settings.archiveRetentionDays)
}
