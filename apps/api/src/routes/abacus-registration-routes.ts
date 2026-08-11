import { and, eq, inArray } from 'drizzle-orm'
import { customers, vehicleFiles, vehicles } from '@vehicle-management/database'
import { requireAdminOrganizationContext } from '../auth/organization'
import { UnauthorizedError } from '../auth/firebase'
import { createDatabase } from '../db/client'
import { assertRequestContentLength, HttpError, jsonResponse, readFormData } from '../http'
import { normalizeCalendarDate } from '../lib/date-utils'
import { assertD1BatchStatementCount } from '../lib/resource-limits'
import { assertAttachmentSignature, assertSupportedAttachmentContentType, attachmentKind, createVehicleFileObjectKey, isSafePathSegment } from '../lib/file-validation'
import { createB2Storage } from '../storage/b2'

const registrationCommitPath = '/api/import/abacus-registration/commit'
const registrationImagePath = '/api/import/abacus-registration/image'
const confirmationText = 'ABACUS登録を実行'
const maximumRegistrationBodyBytes = 12 * 1024 * 1024
const maximumManifestBytes = 1 * 1024 * 1024
const maximumCsvBytes = 5 * 1024 * 1024
const maximumRows = 5_000
const maximumAttachmentSize = 20 * 1024 * 1024
const maximumTextCharacters = 500
const maximumBatchRows = 50

const customerHeaders = ['顧客ID', '顧客番号', '顧客名', 'ふりがな', '電話番号', 'メールアドレス', '郵便番号', '住所', 'メモ', '車両台数']
const vehicleHeaders = ['車両ID', '顧客ID', '顧客名', 'メーカー', '車名', '型式', '登録番号', '車台番号', '年式', '車検満了日', '走行距離', '車体色', '排気量', 'ミッション', '記録簿', '備考']

type RegistrationManifest = {
  version?: unknown
  kind?: unknown
  status?: unknown
  summary?: {
    candidateCount?: unknown
    customerRowCount?: unknown
    vehicleRowCount?: unknown
    imageCount?: unknown
  }
  dataFiles?: unknown
  imageFiles?: unknown
}

type FileDescriptor = { relativePath: string; sizeBytes: number; sha256: string }

type CustomerRegistrationRow = {
  id: string
  customerNumber: string
  name: string
  nameKana: string | null
  phone: string | null
  email: string | null
  postalCode: string | null
  address: string | null
  memo: string | null
  vehicleCount: number
}

type VehicleRegistrationRow = {
  id: string
  customerId: string
  customerName: string
  maker: string | null
  name: string
  model: string | null
  registrationNumber: string | null
  chassisNumber: string | null
  modelYear: number | null
  inspectionDate: string | null
  mileage: number | null
  bodyColor: string | null
  displacement: number | null
  transmission: string | null
  inspectionRecordAvailable: boolean
  memo: string | null
}

type ImageAttachment = {
  customerId: string
  vehicleId: string
  imagePath: string
  imageSha256: string
  contentType: string
}

export async function handleAbacusRegistrationRoutes(request: Request, env: Env): Promise<Response | null> {
  const pathname = new URL(request.url).pathname.replace(/\/$/u, '') || '/'
  if (pathname !== registrationCommitPath && pathname !== registrationImagePath) return null

  try {
    if (request.method !== 'POST') throw new HttpError(405, 'この操作には対応していません。')
    const database = createDatabase(env.DB)
    const context = await requireAdminOrganizationContext(request, env, database)
    return pathname === registrationCommitPath
      ? await commitRegistration(request, env, database, context.organization.organizationId)
      : await uploadRegistrationImage(request, env, database, context.organization.organizationId)
  } catch (error) {
    if (error instanceof UnauthorizedError) return jsonResponse({ error: error.message }, 401, env)
    if (error instanceof HttpError) return jsonResponse({ error: error.message }, error.status, env)
    console.error(error)
    return jsonResponse({ error: 'ABACUS登録処理に失敗しました。' }, 500, env)
  }
}

async function commitRegistration(request: Request, env: Env, database: ReturnType<typeof createDatabase>, organizationId: string) {
  assertRequestContentLength(request, maximumRegistrationBodyBytes, { required: true })
  const formData = await readFormData(request, maximumRegistrationBodyBytes)
  if (textField(formData, 'confirmation') !== confirmationText) throw new HttpError(400, '登録確認文字列が一致しません。')

  const manifestFile = requiredFile(formData, 'manifest')
  const customersFile = requiredFile(formData, 'customers')
  const vehiclesFile = requiredFile(formData, 'vehicles')
  const attachmentsFile = requiredFile(formData, 'attachments')
  const requestedManifestSha256 = requiredSha256(textField(formData, 'manifestSha256'), 'マニフェストSHA-256')
  const manifestBytes = new Uint8Array(await manifestFile.arrayBuffer())
  if (manifestBytes.byteLength === 0 || manifestBytes.byteLength > maximumManifestBytes) throw new HttpError(413, 'マニフェストが大きすぎます。')
  const manifestSha256 = await sha256Bytes(manifestBytes)
  if (manifestSha256 !== requestedManifestSha256) throw new HttpError(409, 'マニフェストが画面確認後に変更されています。もう一度読み込んでください。')
  const manifest = parseManifest(await decodeUtf8(manifestBytes))
  const descriptors = validateManifest(manifest)
  await verifyManifestFile(descriptors, customersFile, 'customers.csv')
  await verifyManifestFile(descriptors, vehiclesFile, 'vehicles.csv')
  await verifyManifestFile(descriptors, attachmentsFile, 'image-attachments.json')

  const customerRows = parseCustomers(await decodeUtf8(new Uint8Array(await customersFile.arrayBuffer())))
  const vehicleRows = parseVehicles(await decodeUtf8(new Uint8Array(await vehiclesFile.arrayBuffer())), customerRows)
  validateVehicleCounts(customerRows, vehicleRows)
  const attachments = parseAttachments(await decodeUtf8(new Uint8Array(await attachmentsFile.arrayBuffer())), descriptors.imageFiles, customerRows, vehicleRows)
  validateSummary(manifest.summary, customerRows.length, vehicleRows.length, attachments.length)
  const existing = await validateExistingRows(database, organizationId, customerRows, vehicleRows)
  const statements = [
    ...createCustomerStatements(database, organizationId, customerRows, new Date().toISOString()),
    ...createVehicleStatements(database, organizationId, vehicleRows, new Date().toISOString()),
  ]
  assertD1BatchStatementCount(statements.length)
  try {
    await database.$client.batch(statements as [D1PreparedStatement, ...D1PreparedStatement[]])
  } catch (error) {
    const message = error instanceof Error ? error.message : 'D1への登録に失敗しました。'
    throw new HttpError(409, `顧客・車両を登録できませんでした。変更は反映されていません。${message.slice(0, 180)}`)
  }

  return jsonResponse({
    status: 'committed',
    manifestSha256,
    customerCount: customerRows.length,
    vehicleCount: vehicleRows.length,
    imageCount: attachments.length,
    customers: { imported: existing.newCustomerCount, updated: existing.existingCustomerCount },
    vehicles: { imported: existing.newVehicleCount, updated: existing.existingVehicleCount },
  }, 200, env)
}

async function uploadRegistrationImage(request: Request, env: Env, database: ReturnType<typeof createDatabase>, organizationId: string) {
  assertRequestContentLength(request, maximumAttachmentSize + 512 * 1024, { required: true })
  const formData = await readFormData(request, maximumAttachmentSize + 512 * 1024)
  const vehicleId = requiredIdentifier(textField(formData, 'vehicleId'), '車両ID', 'abacus-vehicle-')
  const customerId = requiredIdentifier(textField(formData, 'customerId'), '顧客ID', 'abacus-customer-group-')
  const imagePath = requiredImagePath(textField(formData, 'imagePath'))
  const expectedSha256 = requiredSha256(textField(formData, 'imageSha256'), '画像SHA-256')
  const manifestSha256 = requiredSha256(textField(formData, 'manifestSha256'), 'マニフェストSHA-256')
  if (!manifestSha256) throw new HttpError(400, 'マニフェストSHA-256がありません。')
  const fileValue = formData.get('file')
  if (!(fileValue instanceof File)) throw new HttpError(400, '画像ファイルを選択してください。')
  const contentType = assertSupportedAttachmentContentType(fileValue.type)
  if (contentType === 'application/pdf') throw new HttpError(415, 'ABACUS画像にはPNGまたはJPEGを指定してください。')
  if (fileValue.size <= 0 || fileValue.size > maximumAttachmentSize) throw new HttpError(413, '画像は20MB以下にしてください。')
  const fileBody = new Uint8Array(await fileValue.arrayBuffer())
  assertAttachmentSignature(fileBody, contentType)
  const actualSha256 = await sha256Bytes(fileBody)
  if (actualSha256 !== expectedSha256) throw new HttpError(409, `画像SHA-256が一致しません: ${imagePath}`)

  const vehicle = await database.select({ id: vehicles.id, customerId: vehicles.customerId }).from(vehicles).where(and(eq(vehicles.id, vehicleId), eq(vehicles.organizationId, organizationId))).get()
  if (!vehicle) throw new HttpError(404, '対象の車両が見つかりません。')
  if (vehicle.customerId !== customerId) throw new HttpError(409, '画像の顧客IDと車両の所有者が一致しません。')

  const fileId = await stableFileId(`${organizationId}\u0000${vehicleId}\u0000${imagePath}\u0000${expectedSha256}`)
  const sourceFileName = imagePath.split('/').pop() || 'abacus-image.png'
  const fileName = `車検証_${sourceFileName}`.slice(0, 120)
  // オブジェクトキーは既存の再試行と互換性を保つため、ABACUS由来の元ファイル名で固定します。
  const objectKey = createVehicleFileObjectKey(organizationId, vehicleId, fileId, sourceFileName)
  const existing = await database.select().from(vehicleFiles).where(and(eq(vehicleFiles.id, fileId), eq(vehicleFiles.organizationId, organizationId))).get()
  if (existing && (existing.vehicleId !== vehicleId || existing.objectKey !== objectKey || existing.sizeBytes !== fileBody.byteLength || existing.contentType !== contentType)) {
    throw new HttpError(409, '同じ画像IDに異なる添付情報が存在します。')
  }

  try {
    await createB2Storage(env).putObject({ key: objectKey, body: fileBody.buffer as ArrayBuffer, contentType })
  } catch {
    throw new HttpError(503, 'ファイル保存先を利用できません。B2の設定を確認してください。')
  }
  if (!existing) {
    try {
      await database.insert(vehicleFiles).values({ id: fileId, organizationId, vehicleId, objectKey, fileName: fileName.slice(0, 120), contentType, sizeBytes: fileBody.byteLength, fileKind: attachmentKind(contentType) }).run()
    } catch (error) {
      await createB2Storage(env).deleteObject(objectKey).catch(() => undefined)
      throw error
    }
  } else if (existing.fileName !== fileName) {
    await database.update(vehicleFiles).set({ fileName, updatedAt: new Date().toISOString() }).where(and(eq(vehicleFiles.id, fileId), eq(vehicleFiles.organizationId, organizationId))).run()
  }
  const storedFile = await database.select().from(vehicleFiles).where(and(eq(vehicleFiles.id, fileId), eq(vehicleFiles.organizationId, organizationId))).get()
  return jsonResponse({ status: existing ? 'already-uploaded' : 'uploaded', manifestSha256, file: storedFile ? serializeFile(storedFile) : null }, existing ? 200 : 201, env)
}

function validateManifest(manifest: RegistrationManifest) {
  if (manifest.version !== 1 || manifest.kind !== 'abacus-web-import-registration-package' || manifest.status !== 'registration-preview') throw new HttpError(400, 'Gate5Mの登録前パッケージではありません。')
  if (!Array.isArray(manifest.dataFiles) || !Array.isArray(manifest.imageFiles)) throw new HttpError(400, 'マニフェストのファイル一覧が不正です。')
  const dataFiles = parseDescriptors(manifest.dataFiles, false)
  const imageFiles = parseDescriptors(manifest.imageFiles, true)
  if (dataFiles.length !== 3 || imageFiles.length > maximumRows) throw new HttpError(400, 'マニフェストのファイル件数が不正です。')
  const dataByPath = new Map(dataFiles.map((descriptor) => [descriptor.relativePath, descriptor]))
  if (!dataByPath.has('customers.csv') || !dataByPath.has('vehicles.csv') || !dataByPath.has('image-attachments.json')) throw new HttpError(400, '顧客・車両・画像対応表が揃っていません。')
  return { dataFiles: dataByPath, imageFiles: new Map(imageFiles.map((descriptor) => [descriptor.relativePath, descriptor])) }
}

function parseDescriptors(value: unknown[], images: boolean) {
  const result: FileDescriptor[] = []
  const paths = new Set<string>()
  for (const item of value) {
    if (!item || typeof item !== 'object' || Array.isArray(item)) throw new HttpError(400, 'マニフェストのファイル記述が不正です。')
    const descriptor = item as Record<string, unknown>
    const relativePath = normalizePackagePath(descriptor.relativePath)
    const sizeBytes = descriptor.sizeBytes
    const sha256 = typeof descriptor.sha256 === 'string' ? descriptor.sha256.toUpperCase() : ''
    if (!relativePath || (images ? !relativePath.startsWith('images/') : relativePath.startsWith('images/')) || typeof sizeBytes !== 'number' || !Number.isSafeInteger(sizeBytes) || sizeBytes <= 0 || !/^[0-9A-F]{64}$/u.test(sha256) || paths.has(relativePath)) throw new HttpError(400, `マニフェストのファイル記述が不正です: ${relativePath || '(空欄)'}`)
    paths.add(relativePath)
    result.push({ relativePath, sizeBytes: Number(sizeBytes), sha256 })
  }
  return result
}

async function verifyManifestFile(descriptors: { dataFiles: Map<string, FileDescriptor> }, file: File, path: string) {
  const descriptor = descriptors.dataFiles.get(path)
  if (!descriptor) throw new HttpError(400, `マニフェストに${path}がありません。`)
  if (file.size !== descriptor.sizeBytes || file.size > maximumCsvBytes) throw new HttpError(409, `登録前パッケージのサイズが一致しません: ${path}`)
  const actualSha256 = await sha256Bytes(new Uint8Array(await file.arrayBuffer()))
  if (actualSha256 !== descriptor.sha256) throw new HttpError(409, `登録前パッケージのSHA-256が一致しません: ${path}`)
}

function parseCustomers(text: string) {
  const rows = parseStrictRows(text, customerHeaders, 'customers.csv')
  const ids = new Set<string>()
  const numbers = new Set<string>()
  return rows.map((row, index) => {
    const id = requiredIdentifier(row[0], '顧客ID', 'abacus-customer-group-')
    const customerNumber = requiredText(row[1], '顧客番号')
    const name = requiredText(row[2], '顧客名')
    if (ids.has(id) || numbers.has(customerNumber)) throw new HttpError(400, `customers.csv ${index + 2}行目の識別子が重複しています。`)
    ids.add(id)
    numbers.add(customerNumber)
    return {
      id,
      customerNumber,
      name,
      nameKana: nullableText(row[3], 'ふりがな'),
      phone: nullableText(row[4], '電話番号'),
      email: nullableText(row[5], 'メールアドレス'),
      postalCode: nullableText(row[6], '郵便番号'),
      address: nullableText(row[7], '住所'),
      memo: nullableText(row[8], 'メモ'),
      vehicleCount: nonNegativeInteger(row[9], '車両台数'),
    } satisfies CustomerRegistrationRow
  })
}

function parseVehicles(text: string, customersRows: CustomerRegistrationRow[]) {
  const rows = parseStrictRows(text, vehicleHeaders, 'vehicles.csv')
  const ids = new Set<string>()
  return rows.map((row, index) => {
    const id = requiredIdentifier(row[0], '車両ID', 'abacus-vehicle-')
    const customerId = requiredIdentifier(row[1], '顧客ID', 'abacus-customer-group-')
    const customer = customersRows.find((candidate) => candidate.id === customerId)
    if (!customer) throw new HttpError(400, `vehicles.csv ${index + 2}行目の顧客IDがcustomers.csvにありません。`)
    if (row[2].trim() !== customer.name) throw new HttpError(409, `vehicles.csv ${index + 2}行目の顧客名がcustomers.csvと一致しません。`)
    if (ids.has(id)) throw new HttpError(400, `vehicles.csv ${index + 2}行目の車両IDが重複しています。`)
    ids.add(id)
    return {
      id,
      customerId,
      customerName: requiredText(row[2], '顧客名'),
      maker: nullableText(row[3], 'メーカー'),
      name: requiredText(row[4], '車名'),
      model: nullableText(row[5], '型式'),
      registrationNumber: nullableText(row[6], '登録番号'),
      chassisNumber: nullableText(row[7], '車台番号'),
      modelYear: optionalInteger(row[8], '年式'),
      inspectionDate: optionalDate(row[9], '車検満了日'),
      mileage: optionalInteger(row[10], '走行距離'),
      bodyColor: nullableText(row[11], '車体色'),
      displacement: optionalInteger(row[12], '排気量'),
      transmission: nullableText(row[13], 'ミッション'),
      inspectionRecordAvailable: parseInspectionRecord(row[14]),
      memo: nullableText(row[15], '備考'),
    } satisfies VehicleRegistrationRow
  })
}

function validateVehicleCounts(customerRows: CustomerRegistrationRow[], vehicleRows: VehicleRegistrationRow[]) {
  const counts = new Map<string, number>()
  for (const row of vehicleRows) counts.set(row.customerId, (counts.get(row.customerId) ?? 0) + 1)
  for (const row of customerRows) {
    if ((counts.get(row.id) ?? 0) !== row.vehicleCount) {
      throw new HttpError(409, `顧客${row.id}の車両台数がcustomers.csvとvehicles.csvで一致しません。`)
    }
  }
}

function parseAttachments(text: string, imageFiles: Map<string, FileDescriptor>, customersRows: CustomerRegistrationRow[], vehiclesRows: VehicleRegistrationRow[]) {
  let value: unknown
  try { value = JSON.parse(text) } catch { throw new HttpError(400, 'image-attachments.jsonのJSONが不正です。') }
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new HttpError(400, 'image-attachments.jsonの形式が不正です。')
  const document = value as Record<string, unknown>
  if (document.version !== 1 || document.kind !== 'abacus-web-import-image-attachments' || document.status !== 'manual-upload-required' || !Array.isArray(document.attachments)) throw new HttpError(400, 'image-attachments.jsonの種別または添付一覧が不正です。')
  if (document.attachments.length !== imageFiles.size || document.attachments.length > maximumRows) throw new HttpError(400, '画像対応表とマニフェストの画像件数が一致しません。')
  const customerIds = new Set(customersRows.map((row) => row.id))
  const vehicleIds = new Set(vehiclesRows.map((row) => row.id))
  const paths = new Set<string>()
  return document.attachments.map((item) => {
    if (!item || typeof item !== 'object' || Array.isArray(item)) throw new HttpError(400, '画像対応表の行が不正です。')
    const row = item as Record<string, unknown>
    const customerId = requiredIdentifier(textValue(row.customerId), '画像対応表の顧客ID', 'abacus-customer-group-')
    const vehicleId = requiredIdentifier(textValue(row.vehicleId), '画像対応表の車両ID', 'abacus-vehicle-')
    const imagePath = requiredImagePath(textValue(row.imagePath))
    const imageSha256 = requiredSha256(textValue(row.imageSha256), '画像SHA-256')
    const contentType = textValue(row.contentType)
    if (!customerIds.has(customerId) || !vehicleIds.has(vehicleId) || vehiclesRows.find((vehicle) => vehicle.id === vehicleId)?.customerId !== customerId || paths.has(imagePath)) throw new HttpError(400, `画像対応表の参照先が不正です: ${imagePath}`)
    if (!imageFiles.has(imagePath) || imageFiles.get(imagePath)?.sha256 !== imageSha256 || !['image/png', 'image/jpeg'].includes(contentType)) throw new HttpError(400, `画像対応表とマニフェストが一致しません: ${imagePath}`)
    paths.add(imagePath)
    return { customerId, vehicleId, imagePath, imageSha256, contentType } satisfies ImageAttachment
  })
}

function validateSummary(summary: RegistrationManifest['summary'], customerCount: number, vehicleCount: number, imageCount: number) {
  const candidateCount = summary?.candidateCount
  const expectedCustomerCount = summary?.customerRowCount
  const expectedVehicleCount = summary?.vehicleRowCount
  const expectedImageCount = summary?.imageCount
  const values = [candidateCount, expectedCustomerCount, expectedVehicleCount, expectedImageCount]
  if (values.some((value) => value !== undefined && (typeof value !== 'number' || !Number.isSafeInteger(value) || value < 0))) throw new HttpError(400, 'マニフェストの集計値が不正です。')
  if (candidateCount !== undefined && candidateCount !== vehicleCount) throw new HttpError(409, 'マニフェストと車両CSVの候補数が一致しません。')
  if (expectedCustomerCount !== undefined && expectedCustomerCount !== customerCount) throw new HttpError(409, 'マニフェストと顧客CSVの行数が一致しません。')
  if (expectedVehicleCount !== undefined && expectedVehicleCount !== vehicleCount) throw new HttpError(409, 'マニフェストと車両CSVの行数が一致しません。')
  if (expectedImageCount !== undefined && expectedImageCount !== imageCount) throw new HttpError(409, 'マニフェストと画像対応表の件数が一致しません。')
}

async function validateExistingRows(database: ReturnType<typeof createDatabase>, organizationId: string, customerRows: CustomerRegistrationRow[], vehicleRows: VehicleRegistrationRow[]) {
  const customerIds = customerRows.map((row) => row.id)
  const customerNumbers = customerRows.map((row) => row.customerNumber)
  const vehicleIds = vehicleRows.map((row) => row.id)
  const existingCustomers = await loadCustomersByIds(database, customerIds)
  const existingCustomerNumbers = await loadByNumbers(database, customers, organizationId, customerNumbers)
  const existingVehicles = await loadVehiclesByIds(database, vehicleIds)
  const customerById = new Map(existingCustomers.map((row) => [row.id, row]))
  const numberByValue = new Map(existingCustomerNumbers.filter((row) => row.organizationId === organizationId).map((row) => [row.customerNumber, row]))
  const vehicleById = new Map(existingVehicles.map((row) => [row.id, row]))
  let existingCustomerCount = 0
  let existingVehicleCount = 0
  for (const row of customerRows) {
    const existing = customerById.get(row.id)
    if (existing && existing.organizationId !== organizationId) throw new HttpError(409, `顧客IDが別組織です: ${row.id}`)
    const sameNumber = numberByValue.get(row.customerNumber)
    if (sameNumber && sameNumber.id !== row.id) throw new HttpError(409, `顧客番号が既存顧客と競合します: ${row.customerNumber}`)
    if (existing) {
      if (!sameCustomer(existing, row)) throw new HttpError(409, `既存顧客の内容が登録前パッケージと異なります: ${row.id}`)
      existingCustomerCount += 1
    }
  }
  for (const row of vehicleRows) {
    const existing = vehicleById.get(row.id)
    if (existing && existing.organizationId !== organizationId) throw new HttpError(409, `車両IDが別組織です: ${row.id}`)
    if (existing) {
      if (!sameVehicle(existing, row)) throw new HttpError(409, `既存車両の内容が登録前パッケージと異なります: ${row.id}`)
      existingVehicleCount += 1
    }
  }
  return { newCustomerCount: customerRows.length - existingCustomerCount, existingCustomerCount, newVehicleCount: vehicleRows.length - existingVehicleCount, existingVehicleCount }
}

async function loadCustomersByIds(database: ReturnType<typeof createDatabase>, ids: string[]) {
  const rows: Array<typeof customers.$inferSelect> = []
  for (const chunk of chunked(ids, 500)) {
    if (chunk.length === 0) continue
    rows.push(...await database.select().from(customers).where(inArray(customers.id, chunk)).all())
  }
  return rows
}

async function loadVehiclesByIds(database: ReturnType<typeof createDatabase>, ids: string[]) {
  const rows: Array<typeof vehicles.$inferSelect> = []
  for (const chunk of chunked(ids, 500)) {
    if (chunk.length === 0) continue
    rows.push(...await database.select().from(vehicles).where(inArray(vehicles.id, chunk)).all())
  }
  return rows
}

async function loadByNumbers<T extends typeof customers>(database: ReturnType<typeof createDatabase>, table: T, organizationId: string, values: string[]) {
  const rows: Array<typeof customers.$inferSelect> = []
  for (const chunk of chunked(values, 500)) {
    if (chunk.length === 0) continue
    rows.push(...await database.select().from(table).where(and(eq(table.organizationId, organizationId), inArray(table.customerNumber, chunk))).all())
  }
  return rows
}

function createCustomerStatements(database: ReturnType<typeof createDatabase>, organizationId: string, rows: CustomerRegistrationRow[], updatedAt: string) {
  return chunked(rows, maximumBatchRows).map((chunk) => {
    const placeholders = chunk.map(() => '(?,?,?,?,?,?,?,?,?,?,?)').join(',')
    const values = chunk.flatMap((row) => [row.id, organizationId, row.customerNumber, row.name, row.nameKana, row.postalCode, row.address, row.phone, row.email, row.memo, updatedAt])
    return database.$client.prepare(`INSERT INTO customers (id, organization_id, customer_number, name, name_kana, postal_code, address, phone, email, memo, updated_at) VALUES ${placeholders} ON CONFLICT(id) DO UPDATE SET customer_number=excluded.customer_number, name=excluded.name, name_kana=excluded.name_kana, postal_code=excluded.postal_code, address=excluded.address, phone=excluded.phone, email=excluded.email, memo=excluded.memo, updated_at=excluded.updated_at`).bind(...values)
  })
}

function createVehicleStatements(database: ReturnType<typeof createDatabase>, organizationId: string, rows: VehicleRegistrationRow[], updatedAt: string) {
  return chunked(rows, maximumBatchRows).map((chunk) => {
    const placeholders = chunk.map(() => '(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)').join(',')
    const values = chunk.flatMap((row) => [row.id, organizationId, row.customerId, row.maker, row.name, row.model, row.chassisNumber, row.registrationNumber, row.modelYear, row.inspectionDate, row.mileage, row.bodyColor, row.displacement, row.transmission, row.inspectionRecordAvailable ? 1 : 0, row.memo, updatedAt])
    return database.$client.prepare(`INSERT INTO vehicles (id, organization_id, customer_id, maker, name, model, chassis_number, registration_number, model_year, inspection_date, mileage, body_color, displacement, transmission, inspection_record_available, memo, updated_at) VALUES ${placeholders} ON CONFLICT(id) DO UPDATE SET customer_id=excluded.customer_id, maker=excluded.maker, name=excluded.name, model=excluded.model, chassis_number=excluded.chassis_number, registration_number=excluded.registration_number, model_year=excluded.model_year, inspection_date=excluded.inspection_date, mileage=excluded.mileage, body_color=excluded.body_color, displacement=excluded.displacement, transmission=excluded.transmission, inspection_record_available=excluded.inspection_record_available, memo=excluded.memo, updated_at=excluded.updated_at`).bind(...values)
  })
}

function sameCustomer(existing: typeof customers.$inferSelect, row: CustomerRegistrationRow) {
  return existing.customerNumber === row.customerNumber && existing.name === row.name && sameNullable(existing.nameKana, row.nameKana) && sameNullable(existing.phone, row.phone) && sameNullable(existing.email, row.email) && sameNullable(existing.postalCode, row.postalCode) && sameNullable(existing.address, row.address) && sameNullable(existing.memo, row.memo)
}

function sameVehicle(existing: typeof vehicles.$inferSelect, row: VehicleRegistrationRow) {
  return existing.customerId === row.customerId && sameNullable(existing.maker, row.maker) && existing.name === row.name && sameNullable(existing.model, row.model) && sameNullable(existing.registrationNumber, row.registrationNumber) && sameNullable(existing.chassisNumber, row.chassisNumber) && existing.modelYear === row.modelYear && sameNullable(existing.inspectionDate, row.inspectionDate) && existing.mileage === row.mileage && sameNullable(existing.bodyColor, row.bodyColor) && existing.displacement === row.displacement && sameNullable(existing.transmission, row.transmission) && Boolean(existing.inspectionRecordAvailable) === row.inspectionRecordAvailable && sameNullable(existing.memo, row.memo)
}

function parseStrictRows(text: string, expectedHeaders: string[], label: string) {
  const rows = parseCsv(text, label)
  if (rows.length < 2 || rows[0].length !== expectedHeaders.length || rows[0].some((value, index) => value !== expectedHeaders[index])) throw new HttpError(400, `${label}の見出し行が登録形式と一致しません。`)
  const dataRows = rows.slice(1)
  if (dataRows.length === 0 || dataRows.length > maximumRows || dataRows.some((row) => row.length !== expectedHeaders.length)) throw new HttpError(400, `${label}の行形式が不正です。`)
  return dataRows
}

function parseCsv(text: string, label: string) {
  const rows: string[][] = []
  let row: string[] = []
  let field = ''
  let quoted = false
  for (let index = 0; index < text.length; index += 1) {
    const character = text[index]
    if (character === '"') {
      if (quoted && text[index + 1] === '"') { field += '"'; index += 1 } else quoted = !quoted
    } else if (character === ',' && !quoted) { row.push(field.trim()); field = ''
    } else if ((character === '\n' || character === '\r') && !quoted) {
      if (character === '\r' && text[index + 1] === '\n') index += 1
      row.push(field.trim())
      if (row.some((value) => value.length > 0)) rows.push(row)
      row = []
      field = ''
    } else field += character
  }
  if (quoted) throw new HttpError(400, `${label}の引用符が閉じられていません。`)
  if (field || row.length) { row.push(field.trim()); if (row.some((value) => value.length > 0)) rows.push(row) }
  return rows
}

async function decodeUtf8(bytes: Uint8Array) {
  try { return new TextDecoder('utf-8', { fatal: true, ignoreBOM: false }).decode(bytes).replace(/^\uFEFF/u, '') } catch { throw new HttpError(400, 'UTF-8で読み取れないファイルがあります。') }
}

async function sha256Bytes(bytes: Uint8Array) {
  return bytesToHex(await crypto.subtle.digest('SHA-256', bytes))
}

async function stableFileId(value: string) {
  return bytesToHex(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(value)))
}

function bytesToHex(value: ArrayBuffer) {
  return Array.from(new Uint8Array(value), (byte) => byte.toString(16).padStart(2, '0')).join('').toUpperCase()
}

function parseManifest(text: string) {
  try { return JSON.parse(text) as RegistrationManifest } catch { throw new HttpError(400, 'manifest.jsonのJSONが不正です。') }
}

function textField(formData: FormData, key: string) {
  const value = formData.get(key)
  return typeof value === 'string' ? value.trim() : ''
}

function requiredFile(formData: FormData, key: string) {
  const value = formData.get(key)
  if (!(value instanceof File) || value.size === 0) throw new HttpError(400, `${key}ファイルを選択してください。`)
  return value
}

function requiredText(value: string, label: string) {
  const normalized = value.trim()
  if (!normalized || normalized.length > maximumTextCharacters || normalized.split('').some((character) => character.charCodeAt(0) < 0x20 && character !== '\t')) throw new HttpError(400, `${label}が空欄または長すぎます。`)
  return normalized
}

function nullableText(value: string, label: string) {
  return value.trim() ? requiredText(value, label) : null
}

function requiredIdentifier(value: string, label: string, prefix: string) {
  const normalized = requiredText(value, label)
  if (!normalized.startsWith(prefix) || !isSafePathSegment(normalized)) throw new HttpError(400, `${label}の形式が不正です。`)
  return normalized
}

function requiredSha256(value: string, label: string) {
  const normalized = value.trim().toUpperCase()
  if (!/^[0-9A-F]{64}$/u.test(normalized)) throw new HttpError(400, `${label}の形式が不正です。`)
  return normalized
}

function requiredImagePath(value: string) {
  const normalized = normalizePackagePath(value)
  if (!normalized.startsWith('images/') || normalized.length > 240 || !/\.(?:png|jpe?g)$/iu.test(normalized)) throw new HttpError(400, '画像パスの形式が不正です。')
  return normalized
}

function normalizePackagePath(value: unknown) {
  if (typeof value !== 'string') return ''
  const normalized = value.trim().replaceAll('\\', '/').replace(/^\/+/, '')
  if (!normalized || normalized.includes('..') || normalized.includes(':') || normalized.split('/').some((part) => !part)) return ''
  return normalized
}

function nonNegativeInteger(value: string, label: string) {
  const parsed = optionalInteger(value, label)
  if (parsed === null) throw new HttpError(400, `${label}を入力してください。`)
  return parsed
}

function optionalInteger(value: string, label: string) {
  const normalized = value.trim().replaceAll(',', '')
  if (!normalized) return null
  if (!/^\d+$/u.test(normalized)) throw new HttpError(400, `${label}は0以上の整数で入力してください。`)
  const parsed = Number(normalized)
  if (!Number.isSafeInteger(parsed) || parsed > 2_147_483_647) throw new HttpError(400, `${label}が大きすぎます。`)
  return parsed
}

function optionalDate(value: string, label: string) {
  if (!value.trim()) return null
  const normalized = normalizeCalendarDate(value)
  if (!normalized) throw new HttpError(400, `${label}が不正です。`)
  return normalized
}

function parseInspectionRecord(value: string) {
  const normalized = value.trim().toLowerCase()
  if (!normalized) return false
  if (normalized === 'あり' || normalized === 'true') return true
  if (normalized === 'なし' || normalized === 'false') return false
  throw new HttpError(400, '記録簿が不正です。')
}

function textValue(value: unknown) {
  return typeof value === 'string' ? value.trim() : ''
}

function chunked<T>(values: T[], size: number) {
  const result: T[][] = []
  for (let index = 0; index < values.length; index += size) result.push(values.slice(index, index + size))
  return result
}

function sameNullable(left: string | null, right: string | null) {
  return (left ?? '') === (right ?? '')
}

function serializeFile(file: typeof vehicleFiles.$inferSelect) {
  return { id: file.id, name: file.fileName, type: file.fileKind, contentType: file.contentType, size: file.sizeBytes, createdAt: file.createdAt }
}
