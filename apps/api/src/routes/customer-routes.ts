import { and, asc, eq } from 'drizzle-orm'
import { customers, vehicleFiles, vehicles } from '@vehicle-management/database'
import { UnauthorizedError } from '../auth/firebase'
import { requireOrganizationContext } from '../auth/organization'
import { createDatabase } from '../db/client'
import { corsHeaders, HttpError, jsonResponse, readJson } from '../http'
import { createB2Storage } from '../storage/b2'

const maximumAttachmentSize = 20 * 1024 * 1024
const allowedContentTypes = new Set(['application/pdf', 'image/jpeg', 'image/png'])

export async function handleCustomerRoutes(request: Request, env: Env): Promise<Response | null> {
  const pathname = new URL(request.url).pathname.replace(/\/$/, '') || '/'
  const isCustomerRoute = pathname === '/api/customers' || pathname.startsWith('/api/customers/')
  const isVehicleRoute = pathname.startsWith('/api/vehicles/')
  if (!isCustomerRoute && !isVehicleRoute) return null

  try {
    const database = createDatabase(env.DB)
    const context = await requireOrganizationContext(request, env, database)
    const organizationId = context.organization.organizationId

    if (pathname === '/api/customers') {
      if (request.method === 'GET') return listCustomers(request, env, database, organizationId)
      if (request.method === 'POST') return createCustomer(request, env, database, organizationId)
      throw new HttpError(405, 'この操作には対応していません。')
    }

    const customerMatch = pathname.match(/^\/api\/customers\/([^/]+)$/)
    if (customerMatch) {
      if (request.method === 'PATCH') return updateCustomer(request, env, database, customerMatch[1], organizationId)
      throw new HttpError(405, 'この操作には対応していません。')
    }

    const vehicleCollectionMatch = pathname.match(/^\/api\/customers\/([^/]+)\/vehicles$/)
    if (vehicleCollectionMatch) {
      if (request.method === 'POST') return createVehicle(request, env, database, vehicleCollectionMatch[1], organizationId)
      throw new HttpError(405, 'この操作には対応していません。')
    }

    const vehicleFileCollectionMatch = pathname.match(/^\/api\/vehicles\/([^/]+)\/files$/)
    if (vehicleFileCollectionMatch) {
      if (request.method === 'POST') return uploadVehicleFile(request, env, database, vehicleFileCollectionMatch[1], organizationId)
      throw new HttpError(405, 'この操作には対応していません。')
    }

    const vehicleFileItemMatch = pathname.match(/^\/api\/vehicles\/([^/]+)\/files\/([^/]+)$/)
    if (vehicleFileItemMatch) {
      if (request.method === 'GET') return downloadVehicleFile(request, env, database, vehicleFileItemMatch[1], vehicleFileItemMatch[2], organizationId)
      if (request.method === 'DELETE') return deleteVehicleFile(request, env, database, vehicleFileItemMatch[1], vehicleFileItemMatch[2], organizationId)
      throw new HttpError(405, 'この操作には対応していません。')
    }

    const vehicleMatch = pathname.match(/^\/api\/vehicles\/([^/]+)$/)
    if (vehicleMatch) {
      if (request.method === 'PATCH') return updateVehicle(request, env, database, vehicleMatch[1], organizationId)
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

async function listCustomers(request: Request, env: Env, database: ReturnType<typeof createDatabase>, organizationId: string) {
  const records = await loadCustomerRecords(database, organizationId)
  const query = new URL(request.url).searchParams.get('q')?.trim().toLocaleLowerCase()
  const filteredRecords = query ? records.filter((customer) => JSON.stringify(customer).toLocaleLowerCase().includes(query)) : records
  return jsonResponse({ customers: filteredRecords }, 200, env)
}

async function loadCustomerRecords(database: ReturnType<typeof createDatabase>, organizationId: string) {
  const [customerRows, vehicleRows, fileRows] = await Promise.all([
    database.select().from(customers).where(eq(customers.organizationId, organizationId)).orderBy(asc(customers.name)).all(),
    database.select().from(vehicles).where(eq(vehicles.organizationId, organizationId)).orderBy(asc(vehicles.name)).all(),
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
    memo: customer.memo,
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
      memo: vehicle.memo,
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
    memo: nullableString(body, 'memo'),
  }).run()
  return jsonResponse({ customer: await findCustomer(database, id, organizationId) }, 201, env)
}

async function updateCustomer(request: Request, env: Env, database: ReturnType<typeof createDatabase>, customerId: string, organizationId: string) {
  if (!await database.select({ id: customers.id }).from(customers).where(and(eq(customers.id, customerId), eq(customers.organizationId, organizationId))).get()) throw new HttpError(404, '顧客が見つかりません。')
  const body = await readJson(request)
  const name = stringValue(body, 'name')
  if (!name) throw new HttpError(400, '顧客名は必須です。')
  await database.update(customers).set({
    name,
    nameKana: nullableString(body, 'nameKana'),
    phone: nullableString(body, 'phone'),
    email: nullableString(body, 'email'),
    postalCode: nullableString(body, 'postalCode'),
    address: nullableString(body, 'address'),
    memo: nullableString(body, 'memo'),
    updatedAt: new Date().toISOString(),
  }).where(and(eq(customers.id, customerId), eq(customers.organizationId, organizationId))).run()
  return jsonResponse({ customer: await findCustomer(database, customerId, organizationId) }, 200, env)
}

async function createVehicle(request: Request, env: Env, database: ReturnType<typeof createDatabase>, customerId: string, organizationId: string) {
  if (!await database.select({ id: customers.id }).from(customers).where(and(eq(customers.id, customerId), eq(customers.organizationId, organizationId))).get()) throw new HttpError(404, '顧客が見つかりません。')
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
    modelYear: nullableInteger(body, 'modelYear'),
    inspectionDate: nullableString(body, 'inspectionDate'),
    mileage: nullableInteger(body, 'mileage'),
    bodyColor: nullableString(body, 'bodyColor'),
  }).run()
  return jsonResponse({ customer: await findCustomer(database, customerId, organizationId), vehicleId: id }, 201, env)
}

async function updateVehicle(request: Request, env: Env, database: ReturnType<typeof createDatabase>, vehicleId: string, organizationId: string) {
  if (!await database.select({ id: vehicles.id }).from(vehicles).where(and(eq(vehicles.id, vehicleId), eq(vehicles.organizationId, organizationId))).get()) throw new HttpError(404, '車両が見つかりません。')
  const body = await readJson(request)
  const maker = stringValue(body, 'maker')
  const name = stringValue(body, 'model') || stringValue(body, 'name')
  if (!maker || !name) throw new HttpError(400, 'メーカーと車名は必須です。')
  await database.update(vehicles).set({
    maker,
    name,
    registrationNumber: nullableString(body, 'registrationNumber'),
    chassisNumber: nullableString(body, 'chassisNumber'),
    modelYear: nullableInteger(body, 'modelYear'),
    inspectionDate: nullableString(body, 'inspectionDate'),
    mileage: nullableInteger(body, 'mileage'),
    bodyColor: nullableString(body, 'bodyColor'),
    updatedAt: new Date().toISOString(),
  }).where(and(eq(vehicles.id, vehicleId), eq(vehicles.organizationId, organizationId))).run()
  return jsonResponse({ vehicleId }, 200, env)
}

async function uploadVehicleFile(request: Request, env: Env, database: ReturnType<typeof createDatabase>, vehicleId: string, organizationId: string) {
  if (!await database.select({ id: vehicles.id }).from(vehicles).where(and(eq(vehicles.id, vehicleId), eq(vehicles.organizationId, organizationId))).get()) throw new HttpError(404, '車両が見つかりません。')
  const formData = await request.formData()
  const file = formData.get('file')
  if (!(file instanceof File)) throw new HttpError(400, 'ファイルを選択してください。')
  if (!allowedContentTypes.has(file.type)) throw new HttpError(415, 'JPEG・PNG・PDFのみ添付できます。')
  if (file.size > maximumAttachmentSize) throw new HttpError(413, '添付ファイルは20MB以下にしてください。')

  const fileId = crypto.randomUUID()
  const safeName = file.name.replace(/[^a-zA-Z0-9._-]/g, '_').slice(0, 120) || 'file'
  const objectKey = `organizations/${organizationId}/vehicles/${vehicleId}/${fileId}-${safeName}`
  try {
    await createB2Storage(env).putObject({ key: objectKey, body: await file.arrayBuffer(), contentType: file.type })
  } catch {
    throw new HttpError(503, 'ファイル保存先を利用できません。B2の設定を確認してください。')
  }

  try {
    await database.insert(vehicleFiles).values({ id: fileId, organizationId, vehicleId, objectKey, fileName: file.name, contentType: file.type, sizeBytes: file.size, fileKind: getFileKind(file.type) }).run()
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
  headers.set('Content-Type', file.contentType)
  headers.set('Content-Length', String(file.sizeBytes))
  headers.set('Content-Disposition', `inline; filename*=UTF-8''${encodeURIComponent(file.fileName)}`)
  headers.set('Cache-Control', 'private, max-age=300')
  return new Response(response.body, { status: 200, headers })
}

async function findCustomer(database: ReturnType<typeof createDatabase>, customerId: string, organizationId: string) {
  const records = await loadCustomerRecords(database, organizationId)
  return records.find((customer) => customer.id === customerId) ?? null
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

function nullableInteger(body: Record<string, unknown>, key: string) {
  const value = body[key]
  if (value === null || value === undefined || value === '') return null
  const number = typeof value === 'number' ? value : Number(value)
  return Number.isFinite(number) ? Math.round(number) : null
}

function getFileKind(contentType: string): 'image' | 'pdf' | 'other' {
  if (contentType === 'application/pdf') return 'pdf'
  if (contentType.startsWith('image/')) return 'image'
  return 'other'
}
