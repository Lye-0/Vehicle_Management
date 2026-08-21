import { and, asc, desc, eq, gt, inArray, isNull, like, or, sql } from 'drizzle-orm'
import { customers, inspectionSchedules, maintenanceDocuments, mileageHistories, paymentRecords, salesDocuments, vehicleFiles, vehicles } from '@vehicle-management/database'
import { UnauthorizedError } from '../auth/firebase'
import { requireAdminOrganizationContext, requireOrganizationContext } from '../auth/organization'
import { loadBackupSettings } from '../backup-settings'
import { createDatabase } from '../db/client'
import { assertRequestContentLength, corsHeaders, HttpError, jsonResponse, readFormData, readJson } from '../http'
import { parseAbacusDocumentImportMetadata } from '../lib/abacus-document-metadata'
import { normalizeCalendarDate } from '../lib/date-utils'
import { normalizeCustomerBirthDateForStorage } from '../lib/master-sync-helpers'
import { assertAttachmentSignature, assertSupportedAttachmentContentType, attachmentKind, createVehicleFileObjectKey } from '../lib/file-validation'
import { deleteMaster, getMasterDeletionImpact, type MasterDeletionKind } from '../master-deletion'
import { createB2Storage } from '../storage/b2'

const maximumAttachmentSize = 20 * 1024 * 1024

export async function handleCustomerRoutes(request: Request, env: Env): Promise<Response | null> {
  const pathname = new URL(request.url).pathname.replace(/\/$/, '') || '/'
  const isCustomerRoute = pathname === '/api/customers' || pathname.startsWith('/api/customers/')
  const isVehicleRoute = pathname === '/api/vehicles' || pathname.startsWith('/api/vehicles/')
  if (!isCustomerRoute && !isVehicleRoute) return null

  try {
    const database = createDatabase(env.DB)
    const context = await requireOrganizationContext(request, env, database)
    const organizationId = context.organization.organizationId

    if (pathname === '/api/customers') {
      if (request.method === 'GET') return await listCustomers(request, env, database, organizationId)
      if (request.method === 'POST') return await createCustomer(request, env, database, organizationId)
      throw new HttpError(405, 'この操作には対応していません。')
    }

    if (pathname === '/api/vehicles') {
      if (request.method === 'GET') return await listVehicleSummaries(request, env, database, organizationId)
      throw new HttpError(405, 'この操作には対応していません。')
    }

    const customerDeletionImpactMatch = pathname.match(/^\/api\/customers\/([^/]+)\/deletion-impact$/)
    if (customerDeletionImpactMatch) {
      if (request.method !== 'GET') throw new HttpError(405, 'この操作には対応していません。')
      return await getDeletionImpact(request, env, database, 'customer', decodeURIComponent(customerDeletionImpactMatch[1]))
    }

    const vehicleDeletionImpactMatch = pathname.match(/^\/api\/vehicles\/([^/]+)\/deletion-impact$/)
    if (vehicleDeletionImpactMatch) {
      if (request.method !== 'GET') throw new HttpError(405, 'この操作には対応していません。')
      return await getDeletionImpact(request, env, database, 'vehicle', decodeURIComponent(vehicleDeletionImpactMatch[1]))
    }

    const vehiclelessDocumentsMatch = pathname.match(/^\/api\/customers\/([^/]+)\/vehicleless-documents$/)
    if (vehiclelessDocumentsMatch) {
      if (request.method === 'GET') return await listVehiclelessDocuments(env, database, decodeURIComponent(vehiclelessDocumentsMatch[1]), organizationId)
      throw new HttpError(405, 'この操作には対応していません。')
    }

    const customerMatch = pathname.match(/^\/api\/customers\/([^/]+)$/)
    if (customerMatch) {
      if (request.method === 'GET') return await getCustomer(env, database, customerMatch[1], organizationId)
      if (request.method === 'PATCH') return await updateCustomer(request, env, database, customerMatch[1], organizationId)
      if (request.method === 'DELETE') return await deleteMasterFromRoute(request, env, database, 'customer', decodeURIComponent(customerMatch[1]))
      throw new HttpError(405, 'この操作には対応していません。')
    }

    const vehicleCollectionMatch = pathname.match(/^\/api\/customers\/([^/]+)\/vehicles$/)
    if (vehicleCollectionMatch) {
      if (request.method === 'POST') return await createVehicle(request, env, database, vehicleCollectionMatch[1], organizationId)
      throw new HttpError(405, 'この操作には対応していません。')
    }

    const vehicleFileCollectionMatch = pathname.match(/^\/api\/vehicles\/([^/]+)\/files$/)
    if (vehicleFileCollectionMatch) {
      if (request.method === 'POST') return await uploadVehicleFile(request, env, database, vehicleFileCollectionMatch[1], organizationId)
      throw new HttpError(405, 'この操作には対応していません。')
    }

    const vehicleHistoryMatch = pathname.match(/^\/api\/vehicles\/([^/]+)\/history$/)
    if (vehicleHistoryMatch) {
      if (request.method === 'GET') return await getVehicleHistory(env, database, vehicleHistoryMatch[1], organizationId)
      throw new HttpError(405, 'この操作には対応していません。')
    }

    const vehicleFileItemMatch = pathname.match(/^\/api\/vehicles\/([^/]+)\/files\/([^/]+)$/)
    if (vehicleFileItemMatch) {
      if (request.method === 'GET') return await downloadVehicleFile(request, env, database, vehicleFileItemMatch[1], vehicleFileItemMatch[2], organizationId)
      if (request.method === 'DELETE') return await deleteVehicleFile(request, env, database, vehicleFileItemMatch[1], vehicleFileItemMatch[2], organizationId)
      throw new HttpError(405, 'この操作には対応していません。')
    }

    const vehicleMatch = pathname.match(/^\/api\/vehicles\/([^/]+)$/)
    if (vehicleMatch) {
      if (request.method === 'GET') return await getVehicle(env, database, vehicleMatch[1], organizationId)
      if (request.method === 'PATCH') return await updateVehicle(request, env, database, vehicleMatch[1], organizationId)
      if (request.method === 'DELETE') return await deleteMasterFromRoute(request, env, database, 'vehicle', decodeURIComponent(vehicleMatch[1]))
      throw new HttpError(405, 'この操作には対応していません。')
    }

    throw new HttpError(404, '顧客・車両のAPIが見つかりません。')
  } catch (error) {
    if (error instanceof UnauthorizedError) return jsonResponse({ error: error.message }, 401, env)
    if (error instanceof HttpError) return jsonResponse({ error: error.message }, error.status, env)
    console.error(error)
    return jsonResponse({ error: '顧客・車両データの処理に失敗しました。' }, 500, env)
  }
}

async function getDeletionImpact(request: Request, env: Env, database: ReturnType<typeof createDatabase>, kind: MasterDeletionKind, id: string) {
  const context = await requireAdminOrganizationContext(request, env, database)
  const impact = await getMasterDeletionImpact(database, context.organization.organizationId, kind, id)
  return jsonResponse({ impact }, 200, env)
}

async function deleteMasterFromRoute(request: Request, env: Env, database: ReturnType<typeof createDatabase>, kind: MasterDeletionKind, id: string) {
  const context = await requireAdminOrganizationContext(request, env, database)
  const body = await readJson(request)
  if (body.confirmation !== true) throw new HttpError(400, '削除確認が必要です。')
  const expectedUpdatedAt = typeof body.expectedUpdatedAt === 'string' ? body.expectedUpdatedAt : undefined
  const settings = await loadBackupSettings(database, context.organization.organizationId)
  const result = await deleteMaster(database, context.organization.organizationId, kind, id, context.user.uid, settings.archiveRetentionDays, expectedUpdatedAt)
  return jsonResponse({ deleted: true, kind, customerId: result.customerId, vehicleIds: result.vehicleIds }, 200, env)
}

async function listCustomers(request: Request, env: Env, database: ReturnType<typeof createDatabase>, organizationId: string) {
  const url = new URL(request.url)
  if (url.searchParams.get('view') === 'summary') return await listCustomerSummaries(url, env, database, organizationId)
  const records = await loadCustomerRecords(database, organizationId)
  const query = url.searchParams.get('q')?.trim().toLocaleLowerCase()
  const filteredRecords = query ? records.filter((customer) => JSON.stringify(customer).toLocaleLowerCase().includes(query)) : records
  return jsonResponse({ customers: filteredRecords }, 200, env)
}

type CustomerSummary = { id: string; customerNumber: string; name: string; nameKana: string | null; phone: string | null; updatedAt: string; matchedVehicle?: { id: string; maker: string | null; name: string; registrationNumber: string | null; chassisNumber: string | null } | null }

async function listCustomerSummaries(url: URL, env: Env, database: ReturnType<typeof createDatabase>, organizationId: string) {
  const limit = Math.min(Math.max(Number(url.searchParams.get('limit') ?? 30) || 30, 1), 100)
  const query = url.searchParams.get('q')?.trim() ?? ''
  const field = url.searchParams.get('field')?.trim() ?? 'すべて'
  const cursor = decodeCustomerCursor(url.searchParams.get('cursor'))
  const conditions = [eq(customers.organizationId, organizationId), isNull(customers.deletedAt)]
  if (cursor) conditions.push(or(gt(customers.name, cursor.name), and(eq(customers.name, cursor.name), gt(customers.id, cursor.id)))!)

  const pattern = `%${query}%`
  if (query) {
    const customerSearch = field === '顧客名' ? like(customers.name, pattern)
      : field === 'ふりがな' ? like(customers.nameKana, pattern)
        : field === 'メールアドレス' ? like(customers.email, pattern)
          : field === '電話番号' ? like(customers.phone, pattern)
            : field === '住所' ? like(customers.address, pattern)
              : field === '車名' ? sql`EXISTS (SELECT 1 FROM vehicles search_vehicle WHERE search_vehicle.customer_id = ${customers.id} AND search_vehicle.organization_id = ${organizationId} AND search_vehicle.deleted_at IS NULL AND search_vehicle.name LIKE ${pattern})`
                : field === '登録番号' ? sql`EXISTS (SELECT 1 FROM vehicles search_vehicle WHERE search_vehicle.customer_id = ${customers.id} AND search_vehicle.organization_id = ${organizationId} AND search_vehicle.deleted_at IS NULL AND search_vehicle.registration_number LIKE ${pattern})`
                  : field === '車台番号' ? sql`EXISTS (SELECT 1 FROM vehicles search_vehicle WHERE search_vehicle.customer_id = ${customers.id} AND search_vehicle.organization_id = ${organizationId} AND search_vehicle.deleted_at IS NULL AND search_vehicle.chassis_number LIKE ${pattern})`
                    : or(like(customers.name, pattern), like(customers.nameKana, pattern), like(customers.phone, pattern), like(customers.email, pattern), like(customers.address, pattern), sql`EXISTS (SELECT 1 FROM vehicles search_vehicle WHERE search_vehicle.customer_id = ${customers.id} AND search_vehicle.organization_id = ${organizationId} AND search_vehicle.deleted_at IS NULL AND (search_vehicle.name LIKE ${pattern} OR search_vehicle.registration_number LIKE ${pattern} OR search_vehicle.chassis_number LIKE ${pattern}))`)!
    conditions.push(customerSearch)
  }

  const rows = await database.select({ id: customers.id, customerNumber: customers.customerNumber, name: customers.name, nameKana: customers.nameKana, phone: customers.phone, updatedAt: customers.updatedAt })
    .from(customers)
    .where(and(...conditions))
    .orderBy(asc(customers.name), asc(customers.id))
    .limit(limit + 1)
    .all()
  const hasMore = rows.length > limit
  const items = rows.slice(0, limit)
  const last = items.at(-1)
  return jsonResponse({ customers: items, nextCursor: hasMore && last ? encodeCustomerCursor({ name: last.name, id: last.id }) : null, hasMore }, 200, env)
}

function encodeCustomerCursor(value: { name: string; id: string }) {
  return btoa(JSON.stringify(value)).replaceAll('+', '-').replaceAll('/', '_').replace(/=+$/, '')
}

function decodeCustomerCursor(value: string | null): { name: string; id: string } | null {
  if (!value) return null
  try {
    const padded = value.replaceAll('-', '+').replaceAll('_', '/') + '='.repeat((4 - value.length % 4) % 4)
    const parsed = JSON.parse(atob(padded)) as { name?: unknown; id?: unknown }
    return typeof parsed.name === 'string' && typeof parsed.id === 'string' ? { name: parsed.name, id: parsed.id } : null
  } catch { return null }
}

async function loadCustomerRecords(database: ReturnType<typeof createDatabase>, organizationId: string) {
  const [customerRows, vehicleRows, fileRows] = await Promise.all([
    database.select().from(customers).where(and(eq(customers.organizationId, organizationId), isNull(customers.deletedAt))).orderBy(asc(customers.name)).all(),
    database.select().from(vehicles).where(and(eq(vehicles.organizationId, organizationId), isNull(vehicles.deletedAt))).orderBy(asc(vehicles.name)).all(),
    database.select().from(vehicleFiles).where(eq(vehicleFiles.organizationId, organizationId)).orderBy(asc(vehicleFiles.createdAt)).all(),
  ])
  const filesByVehicle = groupBy(fileRows, (file) => file.vehicleId)
  const vehiclesByCustomer = groupBy(vehicleRows, (vehicle) => vehicle.customerId)
  const records = customerRows.map((customer) => ({
    id: customer.id,
    customerNumber: customer.customerNumber,
    name: customer.name,
    nameKana: customer.nameKana,
    phone: customer.phone,
    email: customer.email,
    postalCode: customer.postalCode,
    address: customer.address,
    birthDate: normalizeStoredBirthDate(customer.birthDate),
    employer: normalizeStoredEmployer(customer.employer),
    memo: customer.memo,
    updatedAt: customer.updatedAt,
    vehicles: (vehiclesByCustomer.get(customer.id) ?? []).map((vehicle) => ({
      id: vehicle.id,
      maker: vehicle.maker,
      name: vehicle.name,
      model: vehicle.model,
      registrationNumber: vehicle.registrationNumber,
      chassisNumber: vehicle.chassisNumber,
      modelYear: vehicle.modelYear,
      inspectionDate: vehicle.inspectionDate,
      mileage: vehicle.mileage,
      bodyColor: vehicle.bodyColor,
      displacement: vehicle.displacement,
      transmission: vehicle.transmission,
      inspectionRecordAvailable: vehicle.inspectionRecordAvailable,
      memo: vehicle.memo,
      modelType: vehicle.model,
      freeItem1: vehicle.freeItem1,
      freeItem2: vehicle.freeItem2,
      freeItem3: vehicle.freeItem3,
      updatedAt: vehicle.updatedAt,
      files: (filesByVehicle.get(vehicle.id) ?? []).map(serializeFile),
    })),
  }))
  return records
}

async function createCustomer(request: Request, env: Env, database: ReturnType<typeof createDatabase>, organizationId: string) {
  const body = await readJson(request)
  const name = stringValue(body, 'name')
  if (!name) throw new HttpError(400, '顧客名は必須です。')
  const id = crypto.randomUUID()
  await database.insert(customers).values({
    id,
    organizationId,
    customerNumber: `C-${crypto.randomUUID().slice(0, 8).toUpperCase()}`,
    name,
    nameKana: nullableString(body, 'nameKana'),
    phone: nullableString(body, 'phone'),
    email: nullableString(body, 'email'),
    postalCode: nullableString(body, 'postalCode'),
    address: nullableString(body, 'address'),
    birthDate: nullableDateString(body, 'birthDate'),
    employer: nullableCustomerEmployer(body),
    memo: nullableString(body, 'memo'),
  }).run()
  return jsonResponse({ customer: await loadCustomerRecordById(database, id, organizationId) }, 201, env)
}

async function updateCustomer(request: Request, env: Env, database: ReturnType<typeof createDatabase>, customerId: string, organizationId: string) {
  const current = await database.select({ id: customers.id, updatedAt: customers.updatedAt }).from(customers).where(and(eq(customers.id, customerId), eq(customers.organizationId, organizationId), isNull(customers.deletedAt))).get()
  if (!current) throw new HttpError(404, '顧客が見つかりません。')
  const body = await readJson(request)
  const name = stringValue(body, 'name')
  if (!name) throw new HttpError(400, '顧客名は必須です。')
  const expectedUpdatedAt = typeof body.expectedUpdatedAt === 'string' ? body.expectedUpdatedAt : undefined
  if (expectedUpdatedAt && expectedUpdatedAt !== current.updatedAt) throw new HttpError(409, '顧客情報が他の端末で更新されています。再読み込みしてください。')
  const result = await database.update(customers).set({
    name,
    nameKana: nullableString(body, 'nameKana'),
    phone: nullableString(body, 'phone'),
    email: nullableString(body, 'email'),
    postalCode: nullableString(body, 'postalCode'),
    address: nullableString(body, 'address'),
    birthDate: nullableDateString(body, 'birthDate'),
    employer: nullableCustomerEmployer(body),
    memo: nullableString(body, 'memo'),
    updatedAt: new Date().toISOString(),
  }).where(expectedUpdatedAt ? and(eq(customers.id, customerId), eq(customers.organizationId, organizationId), eq(customers.updatedAt, expectedUpdatedAt)) : and(eq(customers.id, customerId), eq(customers.organizationId, organizationId))).run()
  if (expectedUpdatedAt && result.meta.changes !== 1) throw new HttpError(409, '顧客情報が他の端末で更新されています。再読み込みしてください。')
  return jsonResponse({ customer: await loadCustomerRecordById(database, customerId, organizationId) }, 200, env)
}

async function createVehicle(request: Request, env: Env, database: ReturnType<typeof createDatabase>, customerId: string, organizationId: string) {
  if (!await database.select({ id: customers.id }).from(customers).where(and(eq(customers.id, customerId), eq(customers.organizationId, organizationId), isNull(customers.deletedAt))).get()) throw new HttpError(404, '顧客が見つかりません。')
  const body = await readJson(request)
  const maker = stringValue(body, 'maker')
  const name = stringValue(body, 'model') || stringValue(body, 'name')
  if (!maker || !name) throw new HttpError(400, 'メーカーと車名は必須です。')
  const id = crypto.randomUUID()
  await database.insert(vehicles).values({
    id,
    organizationId,
    customerId,
    maker,
    name,
    model: nullableString(body, 'modelType'),
    registrationNumber: nullableString(body, 'registrationNumber'),
    chassisNumber: nullableString(body, 'chassisNumber'),
    modelYear: nonNegativeInteger(body, 'modelYear'),
    inspectionDate: nullableCalendarDate(body, 'inspectionDate'),
    mileage: nonNegativeInteger(body, 'mileage'),
    bodyColor: nullableString(body, 'bodyColor'),
    displacement: nonNegativeInteger(body, 'displacement'),
    transmission: nullableString(body, 'transmission'),
    memo: nullableString(body, 'memo'),
    freeItem1: nullableString(body, 'freeItem1'),
    freeItem2: nullableString(body, 'freeItem2'),
    freeItem3: nullableString(body, 'freeItem3'),
  }).run()
  return jsonResponse({ customer: await loadCustomerRecordById(database, customerId, organizationId), vehicleId: id }, 201, env)
}

async function updateVehicle(request: Request, env: Env, database: ReturnType<typeof createDatabase>, vehicleId: string, organizationId: string) {
  const current = await database.select({ id: vehicles.id, customerId: vehicles.customerId, updatedAt: vehicles.updatedAt }).from(vehicles).where(and(eq(vehicles.id, vehicleId), eq(vehicles.organizationId, organizationId), isNull(vehicles.deletedAt))).get()
  if (!current) throw new HttpError(404, '車両が見つかりません。')
  const body = await readJson(request)
  const maker = stringValue(body, 'maker')
  const name = stringValue(body, 'model') || stringValue(body, 'name')
  if (!maker || !name) throw new HttpError(400, 'メーカーと車名は必須です。')
  const expectedUpdatedAt = typeof body.expectedUpdatedAt === 'string' ? body.expectedUpdatedAt : undefined
  if (expectedUpdatedAt && expectedUpdatedAt !== current.updatedAt) throw new HttpError(409, '車両情報が他の端末で更新されています。再読み込みしてください。')
  const result = await database.update(vehicles).set({
    maker,
    name,
    model: nullableString(body, 'modelType'),
    registrationNumber: nullableString(body, 'registrationNumber'),
    chassisNumber: nullableString(body, 'chassisNumber'),
    modelYear: nonNegativeInteger(body, 'modelYear'),
    inspectionDate: nullableCalendarDate(body, 'inspectionDate'),
    mileage: nonNegativeInteger(body, 'mileage'),
    bodyColor: nullableString(body, 'bodyColor'),
    displacement: nonNegativeInteger(body, 'displacement'),
    transmission: nullableString(body, 'transmission'),
    memo: nullableString(body, 'memo'),
    freeItem1: nullableString(body, 'freeItem1'),
    freeItem2: nullableString(body, 'freeItem2'),
    freeItem3: nullableString(body, 'freeItem3'),
    updatedAt: new Date().toISOString(),
  }).where(expectedUpdatedAt ? and(eq(vehicles.id, vehicleId), eq(vehicles.organizationId, organizationId), eq(vehicles.updatedAt, expectedUpdatedAt)) : and(eq(vehicles.id, vehicleId), eq(vehicles.organizationId, organizationId))).run()
  if (expectedUpdatedAt && result.meta.changes !== 1) throw new HttpError(409, '車両情報が他の端末で更新されています。再読み込みしてください。')
  return jsonResponse({ customer: await loadCustomerRecordById(database, current.customerId, organizationId), vehicleId }, 200, env)
}

async function uploadVehicleFile(request: Request, env: Env, database: ReturnType<typeof createDatabase>, vehicleId: string, organizationId: string) {
  if (!await database.select({ id: vehicles.id }).from(vehicles).where(and(eq(vehicles.id, vehicleId), eq(vehicles.organizationId, organizationId), isNull(vehicles.deletedAt))).get()) throw new HttpError(404, '車両が見つかりません。')
  assertRequestContentLength(request, maximumAttachmentSize + 1024 * 1024, { required: true })
  const formData = await readFormData(request, maximumAttachmentSize + 1024 * 1024)
  const file = formData.get('file')
  if (!(file instanceof File)) throw new HttpError(400, 'ファイルを選択してください。')
  const contentType = assertSupportedAttachmentContentType(file.type)
  if (file.size > maximumAttachmentSize) throw new HttpError(413, '添付ファイルは20MB以下にしてください。')
  const fileBody = new Uint8Array(await file.arrayBuffer())
  assertAttachmentSignature(fileBody, contentType)

  const fileId = crypto.randomUUID()
  const objectKey = createVehicleFileObjectKey(organizationId, vehicleId, fileId, file.name)
  try {
    await createB2Storage(env).putObject({ key: objectKey, body: fileBody.buffer as ArrayBuffer, contentType })
  } catch {
    throw new HttpError(503, 'ファイル保存先を利用できません。B2の設定を確認してください。')
  }

  try {
    await database.insert(vehicleFiles).values({ id: fileId, organizationId, vehicleId, objectKey, fileName: file.name.slice(0, 120), contentType, sizeBytes: fileBody.byteLength, fileKind: attachmentKind(contentType) }).run()
  } catch (error) {
    await createB2Storage(env).deleteObject(objectKey).catch(() => undefined)
    throw error
  }
  const storedFile = await database.select().from(vehicleFiles).where(and(eq(vehicleFiles.id, fileId), eq(vehicleFiles.organizationId, organizationId))).get()
  return jsonResponse({ file: storedFile ? serializeFile(storedFile) : null }, 201, env)
}

async function deleteVehicleFile(_request: Request, env: Env, database: ReturnType<typeof createDatabase>, vehicleId: string, fileId: string, organizationId: string) {
  const file = await database.select().from(vehicleFiles).where(and(eq(vehicleFiles.id, fileId), eq(vehicleFiles.vehicleId, vehicleId), eq(vehicleFiles.organizationId, organizationId))).get()
  if (!file) throw new HttpError(404, '添付ファイルが見つかりません。')
  try {
    await createB2Storage(env).deleteObject(file.objectKey)
  } catch {
    throw new HttpError(503, 'ファイル保存先を利用できません。')
  }
  await database.delete(vehicleFiles).where(and(eq(vehicleFiles.id, fileId), eq(vehicleFiles.organizationId, organizationId))).run()
  return jsonResponse({ deleted: true }, 200, env)
}

async function downloadVehicleFile(_request: Request, env: Env, database: ReturnType<typeof createDatabase>, vehicleId: string, fileId: string, organizationId: string) {
  const file = await database.select().from(vehicleFiles).where(and(eq(vehicleFiles.id, fileId), eq(vehicleFiles.vehicleId, vehicleId), eq(vehicleFiles.organizationId, organizationId))).get()
  if (!file) throw new HttpError(404, '添付ファイルが見つかりません。')
  let response: Response
  try {
    response = await createB2Storage(env).getObject(file.objectKey)
  } catch {
    throw new HttpError(503, 'ファイル保存先を利用できません。')
  }
  const headers = new Headers(corsHeaders(env))
  const contentType = file.contentType === 'application/pdf' || file.contentType === 'image/jpeg' || file.contentType === 'image/png' ? file.contentType : 'application/octet-stream'
  headers.set('Content-Type', contentType)
  headers.set('Content-Length', String(file.sizeBytes))
  headers.set('Content-Disposition', contentType === 'application/octet-stream' ? `attachment; filename*=UTF-8''${encodeURIComponent(file.fileName)}` : `inline; filename*=UTF-8''${encodeURIComponent(file.fileName)}`)
  headers.set('X-Content-Type-Options', 'nosniff')
  headers.set('Cache-Control', 'private, max-age=300')
  return new Response(response.body, { status: 200, headers })
}

async function getCustomer(env: Env, database: ReturnType<typeof createDatabase>, customerId: string, organizationId: string) {
  const customer = await loadCustomerRecordById(database, decodeURIComponent(customerId), organizationId)
  if (!customer) throw new HttpError(404, '顧客が見つかりません。')
  return jsonResponse({ customer }, 200, env)
}

async function getVehicle(env: Env, database: ReturnType<typeof createDatabase>, vehicleId: string, organizationId: string) {
  const vehicle = await database.select().from(vehicles).where(and(eq(vehicles.id, decodeURIComponent(vehicleId)), eq(vehicles.organizationId, organizationId), isNull(vehicles.deletedAt))).get()
  if (!vehicle) throw new HttpError(404, '車両が見つかりません。')
  const [customer, files] = await Promise.all([
    database.select().from(customers).where(and(eq(customers.id, vehicle.customerId), eq(customers.organizationId, organizationId), isNull(customers.deletedAt))).get(),
    database.select().from(vehicleFiles).where(and(eq(vehicleFiles.vehicleId, vehicle.id), eq(vehicleFiles.organizationId, organizationId))).orderBy(asc(vehicleFiles.createdAt)).all(),
  ])
  return jsonResponse({ vehicle: serializeVehicle(vehicle, files), customer: customer ? { id: customer.id, name: customer.name, nameKana: customer.nameKana, phone: customer.phone, updatedAt: customer.updatedAt } : null }, 200, env)
}

async function listVehicleSummaries(request: Request, env: Env, database: ReturnType<typeof createDatabase>, organizationId: string) {
  const url = new URL(request.url)
  const limit = Math.min(Math.max(Number(url.searchParams.get('limit') ?? 500) || 500, 1), 500)
  const q = url.searchParams.get('q')?.trim() ?? ''
  const field = url.searchParams.get('field')?.trim() ?? 'すべて'
  const conditions = [eq(vehicles.organizationId, organizationId), isNull(vehicles.deletedAt), isNull(customers.deletedAt), sql`${vehicles.inspectionDate} IS NOT NULL AND ${vehicles.inspectionDate} <> ''`]
  if (q) {
    const pattern = `%${q}%`
    const search = field === '顧客名' ? like(customers.name, pattern)
      : field === '車名' ? like(vehicles.name, pattern)
        : field === '登録番号' ? like(vehicles.registrationNumber, pattern)
          : field === '車台番号' ? like(vehicles.chassisNumber, pattern)
            : or(like(customers.name, pattern), like(vehicles.name, pattern), like(vehicles.registrationNumber, pattern), like(vehicles.chassisNumber, pattern))!
    conditions.push(search)
  }
  const rows = await database.select({ id: vehicles.id, customerId: vehicles.customerId, customerName: customers.name, maker: vehicles.maker, name: vehicles.name, registrationNumber: vehicles.registrationNumber, chassisNumber: vehicles.chassisNumber, inspectionDate: vehicles.inspectionDate })
    .from(vehicles)
    .leftJoin(customers, and(eq(customers.id, vehicles.customerId), eq(customers.organizationId, organizationId)))
    .where(and(...conditions))
    .orderBy(asc(vehicles.inspectionDate), asc(vehicles.id))
    .limit(limit + 1)
    .all()
  const hasMore = rows.length > limit
  return jsonResponse({ vehicles: rows.slice(0, limit).map((row) => ({ id: row.id, customerId: row.customerId, customerName: row.customerName ?? '', vehicleName: [row.maker, row.name].filter(Boolean).join(' '), plate: row.registrationNumber ?? '', vin: row.chassisNumber ?? '', inspectionDate: row.inspectionDate ?? '' })), hasMore }, 200, env)
}

async function loadCustomerRecordById(database: ReturnType<typeof createDatabase>, customerId: string, organizationId: string) {
  const customer = await database.select().from(customers).where(and(eq(customers.id, customerId), eq(customers.organizationId, organizationId), isNull(customers.deletedAt))).get()
  if (!customer) return null
  const vehicleRows = await database.select().from(vehicles).where(and(eq(vehicles.customerId, customerId), eq(vehicles.organizationId, organizationId), isNull(vehicles.deletedAt))).orderBy(asc(vehicles.name)).all()
  const fileRows = vehicleRows.length > 0 ? await database.select().from(vehicleFiles).where(and(eq(vehicleFiles.organizationId, organizationId), inArray(vehicleFiles.vehicleId, vehicleRows.map((vehicle) => vehicle.id)))).orderBy(asc(vehicleFiles.createdAt)).all() : []
  const filesByVehicle = groupBy(fileRows.filter((file) => vehicleRows.some((vehicle) => vehicle.id === file.vehicleId)), (file) => file.vehicleId)
  return {
    id: customer.id,
    customerNumber: customer.customerNumber,
    name: customer.name,
    nameKana: customer.nameKana,
    phone: customer.phone,
    email: customer.email,
    postalCode: customer.postalCode,
    address: customer.address,
    birthDate: normalizeStoredBirthDate(customer.birthDate),
    employer: normalizeStoredEmployer(customer.employer),
    memo: customer.memo,
    updatedAt: customer.updatedAt,
    vehicles: vehicleRows.map((vehicle) => serializeVehicle(vehicle, filesByVehicle.get(vehicle.id) ?? [])),
  }
}

function serializeVehicle(vehicle: typeof vehicles.$inferSelect, files: Array<typeof vehicleFiles.$inferSelect>) {
  return {
    id: vehicle.id,
    maker: vehicle.maker,
    name: vehicle.name,
    model: vehicle.model,
    registrationNumber: vehicle.registrationNumber,
    chassisNumber: vehicle.chassisNumber,
    modelYear: vehicle.modelYear,
    inspectionDate: vehicle.inspectionDate,
    mileage: vehicle.mileage,
    bodyColor: vehicle.bodyColor,
    displacement: vehicle.displacement,
    transmission: vehicle.transmission,
    inspectionRecordAvailable: vehicle.inspectionRecordAvailable,
    memo: vehicle.memo,
    modelType: vehicle.model,
    freeItem1: vehicle.freeItem1,
    freeItem2: vehicle.freeItem2,
    freeItem3: vehicle.freeItem3,
    updatedAt: vehicle.updatedAt,
    files: files.map(serializeFile),
  }
}

type VehiclelessDocumentSummary = {
  id: string
  kind: 'sales' | 'maintenance'
  number: string
  type: string
  category: string | null
  status: string
  issuedAt: string
  total: number
  sourceLocation: string
}

async function listVehiclelessDocuments(env: Env, database: ReturnType<typeof createDatabase>, customerId: string, organizationId: string) {
  const customer = await database.select({ id: customers.id }).from(customers).where(and(eq(customers.id, customerId), eq(customers.organizationId, organizationId), isNull(customers.deletedAt))).get()
  if (!customer) throw new HttpError(404, '顧客が見つかりません。')

  const [salesRows, maintenanceRows] = await Promise.all([
    database.select().from(salesDocuments).where(and(eq(salesDocuments.customerId, customerId), eq(salesDocuments.organizationId, organizationId), isNull(salesDocuments.vehicleId), isNull(salesDocuments.archivedAt))).orderBy(desc(salesDocuments.issuedAt), desc(salesDocuments.number)).all(),
    database.select().from(maintenanceDocuments).where(and(eq(maintenanceDocuments.customerId, customerId), eq(maintenanceDocuments.organizationId, organizationId), isNull(maintenanceDocuments.vehicleId), isNull(maintenanceDocuments.archivedAt))).orderBy(desc(maintenanceDocuments.issuedAt), desc(maintenanceDocuments.number)).all(),
  ])
  const documents = [
    ...salesRows.flatMap((document) => {
      const summary = toVehiclelessDocumentSummary(document, 'sales')
      return summary ? [summary] : []
    }),
    ...maintenanceRows.flatMap((document) => {
      const summary = toVehiclelessDocumentSummary(document, 'maintenance')
      return summary ? [summary] : []
    }),
  ].sort(compareVehiclelessDocuments)

  return jsonResponse({
    customerId,
    salesCount: documents.filter((document) => document.kind === 'sales').length,
    maintenanceCount: documents.filter((document) => document.kind === 'maintenance').length,
    documents,
  }, 200, env)
}

function toVehiclelessDocumentSummary(document: typeof salesDocuments.$inferSelect | typeof maintenanceDocuments.$inferSelect, kind: VehiclelessDocumentSummary['kind']): VehiclelessDocumentSummary | null {
  const abacusImport = parseAbacusDocumentImportMetadata(document.detailsJson)
  if (!abacusImport?.vehicleless) return null
  return {
    id: document.id,
    kind,
    number: document.number,
    type: document.type,
    category: kind === 'maintenance' ? (document as typeof maintenanceDocuments.$inferSelect).category : null,
    status: document.status,
    issuedAt: document.issuedAt,
    total: document.total,
    sourceLocation: abacusImport.sourceLocation,
  }
}

function compareVehiclelessDocuments(left: VehiclelessDocumentSummary, right: VehiclelessDocumentSummary) {
  const dateCompare = right.issuedAt.localeCompare(left.issuedAt)
  if (dateCompare !== 0) return dateCompare
  const kindCompare = left.kind.localeCompare(right.kind)
  if (kindCompare !== 0) return kindCompare
  return left.number.localeCompare(right.number, 'ja-JP', { numeric: true })
}

async function getVehicleHistory(env: Env, database: ReturnType<typeof createDatabase>, vehicleId: string, organizationId: string) {
  const vehicle = await database.select().from(vehicles).where(and(eq(vehicles.id, vehicleId), eq(vehicles.organizationId, organizationId), isNull(vehicles.deletedAt))).get()
  if (!vehicle) throw new HttpError(404, '車両が見つかりません。')

  const [customer, sales, maintenance, schedules, files, mileageHistoryRows] = await Promise.all([
    database.select().from(customers).where(and(eq(customers.id, vehicle.customerId), eq(customers.organizationId, organizationId), isNull(customers.deletedAt))).get(),
    database.select().from(salesDocuments).where(and(eq(salesDocuments.vehicleId, vehicleId), eq(salesDocuments.organizationId, organizationId), isNull(salesDocuments.archivedAt))).orderBy(desc(salesDocuments.issuedAt)).all(),
    database.select().from(maintenanceDocuments).where(and(eq(maintenanceDocuments.vehicleId, vehicleId), eq(maintenanceDocuments.organizationId, organizationId), isNull(maintenanceDocuments.archivedAt))).orderBy(desc(maintenanceDocuments.issuedAt)).all(),
    database.select().from(inspectionSchedules).where(and(eq(inspectionSchedules.vehicleId, vehicleId), eq(inspectionSchedules.organizationId, organizationId), isNull(inspectionSchedules.deletionBatchId))).orderBy(desc(inspectionSchedules.dueDate)).all(),
    database.select().from(vehicleFiles).where(and(eq(vehicleFiles.vehicleId, vehicleId), eq(vehicleFiles.organizationId, organizationId))).orderBy(desc(vehicleFiles.createdAt)).all(),
    database.select({ mileage: mileageHistories.mileage, maintenanceDocumentId: mileageHistories.maintenanceDocumentId }).from(mileageHistories).innerJoin(maintenanceDocuments, and(eq(mileageHistories.maintenanceDocumentId, maintenanceDocuments.id), eq(maintenanceDocuments.organizationId, organizationId), isNull(maintenanceDocuments.archivedAt))).where(and(eq(mileageHistories.vehicleId, vehicleId), eq(mileageHistories.organizationId, organizationId))).all(),
  ])
  const documentIds = [...sales.map((document) => document.id), ...maintenance.map((document) => document.id)]
  const payments = documentIds.length > 0 ? await database.select().from(paymentRecords).where(and(eq(paymentRecords.organizationId, organizationId), inArray(paymentRecords.documentId, documentIds))).orderBy(desc(paymentRecords.paymentDate), desc(paymentRecords.updatedAt)).all() : []
  const documentKeys = new Set([...sales.map((document) => `販売請求書:${document.id}`), ...maintenance.map((document) => `整備請求書:${document.id}`)])
  const relatedPayments = payments.filter((payment) => documentKeys.has(`${payment.documentType}:${payment.documentId}`))
  const salesById = new Map(sales.map((document) => [document.id, document]))
  const maintenanceById = new Map(maintenance.map((document) => [document.id, document]))
  const mileageByDocumentId = new Map(mileageHistoryRows.map((row) => [row.maintenanceDocumentId, row.mileage]))

  return jsonResponse({
    vehicle: {
      id: vehicle.id,
      customerId: vehicle.customerId,
      customerName: customer?.name ?? '',
      maker: vehicle.maker,
      name: vehicle.name,
      modelType: vehicle.model,
      registrationNumber: vehicle.registrationNumber,
      chassisNumber: vehicle.chassisNumber,
      modelYear: vehicle.modelYear,
      inspectionDate: vehicle.inspectionDate,
      mileage: vehicle.mileage,
      bodyColor: vehicle.bodyColor,
      displacement: vehicle.displacement,
      transmission: vehicle.transmission,
      memo: vehicle.memo,
      freeItem1: vehicle.freeItem1,
      freeItem2: vehicle.freeItem2,
      freeItem3: vehicle.freeItem3,
    },
    sales: sales.map((document) => ({ id: document.id, number: document.number, type: document.type, status: document.status, issuedAt: document.issuedAt, dueDate: document.dueDate, total: document.total })),
    maintenance: maintenance.map((document) => {
      const abacusImport = parseAbacusDocumentImportMetadata(document.detailsJson)
      return {
        id: document.id,
        number: document.number,
        type: document.type,
        category: document.category,
        status: document.status,
        issuedAt: document.issuedAt,
        intakeDate: document.intakeDate,
        completionDate: document.completionDate,
        total: document.total,
        // ABACUS書類に走行距離の入力がない場合、現在の車両マスタ値を履歴へ流用しない。
        // 後日入力した走行距離によって、過去書類の記録まで変わらないようにする。
        recordedMileage: mileageByDocumentId.get(document.id) ?? extractMileageFromDetailsJson(document.detailsJson) ?? (abacusImport ? null : vehicle.mileage),
      }
    }),
    inspections: schedules.map((schedule) => ({ id: schedule.id, inspectionType: schedule.inspectionType, dueDate: schedule.dueDate, status: schedule.status, note: schedule.note, notifiedAt: schedule.notifiedAt })),
    payments: relatedPayments.map((payment) => ({ id: payment.id, documentType: payment.documentType, documentId: payment.documentId, documentNumber: payment.documentType === '販売請求書' ? salesById.get(payment.documentId)?.number ?? '' : maintenanceById.get(payment.documentId)?.number ?? '', paidAmount: payment.paidAmount, paymentDate: payment.paymentDate, method: payment.method, note: payment.note })),
    attachments: files.map(serializeFile),
  }, 200, env)
}

function extractMileageFromDetailsJson(detailsJson: string | null): number | null {
  if (!detailsJson) return null
  try {
    const parsed = JSON.parse(detailsJson)
    if (!parsed || typeof parsed !== 'object') return null
    const vehicleOverride = parsed.vehicleOverride
    if (!vehicleOverride || typeof vehicleOverride !== 'object') return null
    const mileage = vehicleOverride.mileage
    if (typeof mileage !== 'string') return null
    const digits = mileage.replace(/[^0-9]/g, '')
    if (!digits) return null
    const parsed2 = Number(digits)
    return Number.isFinite(parsed2) ? parsed2 : null
  } catch {
    return null
  }
}

function groupBy<T>(items: T[], getKey: (item: T) => string) {
  const grouped = new Map<string, T[]>()
  for (const item of items) {
    const key = getKey(item)
    const current = grouped.get(key) ?? []
    current.push(item)
    grouped.set(key, current)
  }
  return grouped
}

function serializeFile(file: typeof vehicleFiles.$inferSelect) {
  return { id: file.id, name: file.fileName, type: file.fileKind, contentType: file.contentType, size: file.sizeBytes, createdAt: file.createdAt }
}

function stringValue(body: Record<string, unknown>, key: string) {
  return typeof body[key] === 'string' ? body[key].trim() : ''
}

function nullableString(body: Record<string, unknown>, key: string) {
  const value = stringValue(body, key)
  return value || null
}

function nullableDateString(body: Record<string, unknown>, key: string) {
  const value = stringValue(body, key)
  return normalizeStoredBirthDate(value)
}

function nullableCustomerEmployer(body: Record<string, unknown>, key = 'employer') {
  return normalizeStoredEmployer(nullableString(body, key))
}

function normalizeStoredBirthDate(value: string | null | undefined) {
  return normalizeCustomerBirthDateForStorage(value) || null
}

function normalizeStoredEmployer(value: string | null | undefined) {
  const normalized = typeof value === 'string' ? value.normalize('NFKC').trim() : ''
  return normalized && normalized !== 'employer' ? normalized : null
}

function nonNegativeInteger(body: Record<string, unknown>, key: string) {
  const value = body[key]
  if (value === null || value === undefined || value === '') return null
  const number = typeof value === 'number' ? value : Number(value)
  if (!Number.isFinite(number) || number < 0) throw new HttpError(400, `${key}は0以上の整数で入力してください。`)
  return Math.round(number)
}

function nullableCalendarDate(body: Record<string, unknown>, key: string) {
  const value = stringValue(body, key)
  if (!value) return null
  const normalized = normalizeCalendarDate(value)
  if (!normalized) throw new HttpError(400, `${key}を正しい日付で入力してください。`)
  return normalized
}

