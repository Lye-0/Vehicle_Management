import { and, eq, inArray } from 'drizzle-orm'
import { customers, maintenanceDocuments, maintenanceItems, salesDocumentItems, salesDocuments, vehicleFiles, vehicles } from '@vehicle-management/database'
import { requireAdminOrganizationContext } from '../auth/organization'
import { UnauthorizedError } from '../auth/firebase'
import { createDatabase } from '../db/client'
import { assertRequestContentLength, HttpError, jsonResponse, readFormData } from '../http'
import { normalizeCalendarDate } from '../lib/date-utils'
import { assertD1BatchStatementCount } from '../lib/resource-limits'
import { assertAttachmentSignature, assertSupportedAttachmentContentType, attachmentKind, createVehicleFileObjectKey, isSafePathSegment } from '../lib/file-validation'
import { createB2Storage } from '../storage/b2'

const registrationCommitPath = '/api/import/abacus-registration/commit'
const registrationPreviewPath = '/api/import/abacus-registration/preview'
const registrationImagePath = '/api/import/abacus-registration/image'
const confirmationText = 'ABACUS登録を実行'
const maximumRegistrationBodyBytes = 64 * 1024 * 1024
const maximumManifestBytes = 1 * 1024 * 1024
const maximumCsvBytes = 64 * 1024 * 1024
const maximumRows = 5_000
const maximumAttachmentSize = 20 * 1024 * 1024
const maximumTextCharacters = 500
// Cloudflare D1の1クエリあたりのバインド変数上限は100。組織IDなどの固定条件分を残すため90件単位にする。
const maximumQueryValues = 90
// INSERTも1クエリ100変数以内に収める。余裕を持たせて90変数以下とする。
const maximumCustomerBatchRows = 8 // 11変数/行
const maximumVehicleBatchRows = 5 // 17変数/行
const maximumSalesDocumentBatchRows = 5 // 17変数/行
const maximumMaintenanceDocumentRows = 4 // 21変数/行
const maximumSalesItemBatchRows = 6 // 13変数/行
const maximumMaintenanceItemBatchRows = 7 // 12変数/行

const customerHeaders = ['顧客ID', '顧客番号', '顧客名', 'ふりがな', '電話番号', 'メールアドレス', '郵便番号', '住所', 'メモ', '車両台数']
const vehicleHeaders = ['車両ID', '顧客ID', '顧客名', 'メーカー', '車名', '型式', '登録番号', '車台番号', '年式', '車検満了日', '走行距離', '車体色', '排気量', 'ミッション', '記録簿', '備考']
const finalSalesHeaders = ['書類ID', '書類番号', '書類種別', 'ステータス', '顧客名', '車名', '登録番号', '発行日', '支払期限', '税率', '小計', '消費税', '合計', '明細', '備考', '明細詳細']
const finalMaintenanceHeaders = ['書類ID', '書類番号', '書類種別', '入庫区分', 'ステータス', '顧客名', '車名', '登録番号', '入庫日', '出庫予定日', '支払期限', '税率', '小計', '消費税', '合計', '明細', '備考', '明細詳細']

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

type GraphFinalManifest = {
  version?: unknown
  kind?: unknown
  status?: unknown
  summary?: Record<string, unknown>
  dataFiles?: unknown
  imageFiles?: unknown
  groups?: unknown
  documents?: unknown
  excludedDocumentKeys?: unknown
}

type GraphFinalDocumentLink = {
  documentKey: string
  documentId: string
  documentKind: '販売書類' | '整備書類'
  documentNumber: string
  customerId: string
  customerName: string
  vehicleId: string | null
  vehicleName: string | null
  vehicleless: boolean
  sourceLocation: string
  warning: string
}

type AbacusDetailLine = {
  description: string | null
  quantity: number | null
  unit: string | null
  unitPrice: number | null
  partAmount: number | null
  technicalFees: number | null
  summary: string | null
  sourceRowIndex: number
}

type ImportedDetailLine = {
  line: AbacusDetailLine
  itemType: string
  taxCategory: string
}

type AbacusDetailFinancialLine = {
  description: string
  itemType: string
  taxCategory: string
  amount: number
  sourceRowIndex: number
}

type AbacusDetailPayload = {
  version: 1
  kind: 'abacus-detail-lines'
  sourceFile: string
  recordIdHex: string
  documentNumber: string
  customerName: string
  vehicleName: string
  registrationNumber: string
  chassisNumber: string
  lines: AbacusDetailLine[]
  financialLines: AbacusDetailFinancialLine[]
  partsSubtotal: number | null
  technicalSubtotal: number | null
  abacusSubtotal: number | null
  abacusTotal: number | null
  abacusTax: number | null
  abacusTaxRate: number | null
  detailAmount: number
  excludedDetailCount: number
  amountOnlyRowCount: number
  matchStatus: 'matched' | 'review' | 'unmatched'
  warning: string
}

type AbacusDetailReport = {
  isAbacusMigration: true
  amountOnlyRowCount: number
  excludedDetailCount: number
  detailAmount: number
  detailSubtotalDifference: number | null
  detailTotalDifference: number | null
  warning: string | null
}

type FinalSalesRow = {
  id: string
  number: string
  type: string
  status: string
  customerName: string
  vehicleName: string
  registrationNumber: string
  issuedAt: string
  dueDate: string | null
  taxRate: number
  subtotal: number
  tax: number
  total: number
  itemDescription: string
  note: string | null
  details: string
  detailPayload: AbacusDetailPayload | null
  detailReport: AbacusDetailReport | null
  amountDefaulted: boolean
}

type FinalMaintenanceRow = {
  id: string
  number: string
  type: string
  category: string
  status: string
  customerName: string
  vehicleName: string
  registrationNumber: string
  intakeDate: string | null
  plannedReleaseDate: string | null
  dueDate: string | null
  issuedAt: string
  taxRate: number
  subtotal: number
  tax: number
  total: number
  itemDescription: string
  note: string | null
  details: string
  detailPayload: AbacusDetailPayload | null
  detailReport: AbacusDetailReport | null
  amountDefaulted: boolean
}

export async function handleAbacusRegistrationRoutes(request: Request, env: Env): Promise<Response | null> {
  const pathname = new URL(request.url).pathname.replace(/\/$/u, '') || '/'
  if (pathname !== registrationCommitPath && pathname !== registrationPreviewPath && pathname !== registrationImagePath) return null

  try {
    if (request.method !== 'POST') throw new HttpError(405, 'この操作には対応していません。')
    const database = createDatabase(env.DB)
    const context = await requireAdminOrganizationContext(request, env, database)
    if (pathname === registrationCommitPath) return await commitRegistration(request, env, database, context.organization.organizationId)
    if (pathname === registrationPreviewPath) return await previewRegistration(request, env, database, context.organization.organizationId)
    return await uploadRegistrationImage(request, env, database, context.organization.organizationId)
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
  const requestedManifestSha256 = requiredSha256(textField(formData, 'manifestSha256'), 'マニフェストSHA-256')
  const manifestBytes = new Uint8Array(await manifestFile.arrayBuffer())
  if (manifestBytes.byteLength === 0 || manifestBytes.byteLength > maximumManifestBytes) throw new HttpError(413, 'マニフェストが大きすぎます。')
  const manifestSha256 = await sha256Bytes(manifestBytes)
  if (manifestSha256 !== requestedManifestSha256) throw new HttpError(409, 'マニフェストが画面確認後に変更されています。もう一度読み込んでください。')
  const manifest = parseManifest(await decodeUtf8(manifestBytes))
  if (isGraphFinalManifest(manifest)) {
    if (formData.get('packageManifest') instanceof File || formData.get('readyManifest') instanceof File) await validateReadyEnvelope(formData, manifest)
    return commitGraphFinalRegistration(formData, env, database, organizationId, manifest, manifestSha256)
  }

  const customersFile = requiredFile(formData, 'customers')
  const vehiclesFile = requiredFile(formData, 'vehicles')
  const attachmentsFile = requiredFile(formData, 'attachments')
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

async function previewRegistration(request: Request, env: Env, database: ReturnType<typeof createDatabase>, organizationId: string) {
  void database
  void organizationId
  assertRequestContentLength(request, maximumRegistrationBodyBytes, { required: true })
  const formData = await readFormData(request, maximumRegistrationBodyBytes)
  const manifestFile = requiredFile(formData, 'manifest')
  const requestedManifestSha256 = requiredSha256(textField(formData, 'manifestSha256'), 'マニフェストSHA-256')
  const manifestBytes = new Uint8Array(await manifestFile.arrayBuffer())
  if (manifestBytes.byteLength === 0 || manifestBytes.byteLength > maximumManifestBytes) throw new HttpError(413, 'マニフェストが大きすぎます。')
  const manifestSha256 = await sha256Bytes(manifestBytes)
  if (manifestSha256 !== requestedManifestSha256) throw new HttpError(409, 'マニフェストSHA-256が一致しません。もう一度読み込んでください。')
  const manifest = parseManifest(await decodeUtf8(manifestBytes))
  if (!isGraphFinalManifest(manifest)) throw new HttpError(400, 'Gate 17のグラフ確定パッケージではありません。')
  const readyEnvelope = await validateReadyEnvelope(formData, manifest)
  const files = await readGraphFinalFiles(formData, manifest)
  const customerRows = await normalizeFinalCustomerNumbers(parseFinalCustomers(await decodeUtf8(files.customers.bytes)))
  const vehicleRows = parseFinalVehicles(await decodeUtf8(files.vehicles.bytes), customerRows)
  validateVehicleCounts(customerRows, vehicleRows)
  const salesRows = parseFinalSales(await decodeUtf8(files.sales.bytes))
  const maintenanceRows = parseFinalMaintenance(await decodeUtf8(files.maintenance.bytes))
  const links = parseFinalDocumentLinks(await decodeUtf8(files.links.bytes))
  const imageAttachments = files.imageAttachments
    ? parseGraphFinalImageAttachments(await decodeUtf8(files.imageAttachments.bytes), files.imageDescriptors)
    : []
  const customerIds = new Set(customerRows.map((row) => row.id))
  const vehiclesById = new Map(vehicleRows.map((row) => [row.id, row]))
  for (const attachment of imageAttachments) {
    const vehicle = vehiclesById.get(attachment.vehicleId)
    if (!customerIds.has(attachment.customerId) || !vehicle || vehicle.customerId !== attachment.customerId) throw new HttpError(400, `画像対応表の顧客・車両参照が不正です: ${attachment.imagePath}`)
  }
  const normalizedDocuments = normalizeGraphFinalDocumentNumbers(salesRows, maintenanceRows, links.documents)
  const normalizedLinks = { documents: normalizedDocuments.links, excludedDocumentKeys: links.excludedDocumentKeys }
  validateFinalPackage(manifest, customerRows, vehicleRows, normalizedDocuments.salesRows, normalizedDocuments.maintenanceRows, normalizedLinks)
  const detailSummary = summarizeAbacusDetails(normalizedDocuments.salesRows, normalizedDocuments.maintenanceRows)
  if (manifest.summary?.imageCount !== undefined && manifest.summary.imageCount !== imageAttachments.length) throw new HttpError(409, `マニフェストの画像件数が一致しません: ${manifest.summary.imageCount} / ${imageAttachments.length}`)
  return jsonResponse({
    status: 'preview',
    manifestSha256,
    customerCount: customerRows.length,
    vehicleCount: vehicleRows.length,
    salesCount: normalizedDocuments.salesRows.length,
    maintenanceCount: normalizedDocuments.maintenanceRows.length,
    vehiclelessDocumentCount: normalizedLinks.documents.filter((link) => link.vehicleless).length,
    excludedDocumentCount: normalizedLinks.excludedDocumentKeys.length,
    abacusDetailDocumentCount: detailSummary.mappedDocumentCount,
    abacusDetailReviewDocumentCount: detailSummary.reviewDocumentCount,
    abacusDetailUnsupportedDocumentCount: detailSummary.unsupportedDocumentCount,
    abacusDetailExcludedRowCount: detailSummary.excludedRowCount,
    abacusAmountOnlyRowCount: detailSummary.amountOnlyRowCount,
    abacusDetailMismatchDocumentCount: detailSummary.mismatchDocumentCount,
    imageCount: imageAttachments.length,
    checkedReadyFileCount: readyEnvelope.checkedReadyFileCount,
    errors: [],
  }, 200, env)
}

async function commitGraphFinalRegistration(
  formData: FormData,
  env: Env,
  database: ReturnType<typeof createDatabase>,
  organizationId: string,
  manifest: GraphFinalManifest,
  manifestSha256: string,
) {
  const files = await readGraphFinalFiles(formData, manifest)
  const customerRows = await normalizeFinalCustomerNumbers(parseFinalCustomers(await decodeUtf8(files.customers.bytes)))
  const vehicleRows = parseFinalVehicles(await decodeUtf8(files.vehicles.bytes), customerRows)
  validateVehicleCounts(customerRows, vehicleRows)
  const salesRows = parseFinalSales(await decodeUtf8(files.sales.bytes))
  const maintenanceRows = parseFinalMaintenance(await decodeUtf8(files.maintenance.bytes))
  const links = parseFinalDocumentLinks(await decodeUtf8(files.links.bytes))
  const imageAttachments = files.imageAttachments
    ? parseGraphFinalImageAttachments(await decodeUtf8(files.imageAttachments.bytes), files.imageDescriptors)
    : []
  const customerIds = new Set(customerRows.map((row) => row.id))
  const vehiclesById = new Map(vehicleRows.map((row) => [row.id, row]))
  for (const attachment of imageAttachments) {
    const vehicle = vehiclesById.get(attachment.vehicleId)
    if (!customerIds.has(attachment.customerId) || !vehicle || vehicle.customerId !== attachment.customerId) throw new HttpError(400, `画像対応表の顧客・車両参照が不正です: ${attachment.imagePath}`)
  }
  const normalizedDocuments = normalizeGraphFinalDocumentNumbers(salesRows, maintenanceRows, links.documents)
  const normalizedLinks = { documents: normalizedDocuments.links, excludedDocumentKeys: links.excludedDocumentKeys }
  validateFinalPackage(manifest, customerRows, vehicleRows, normalizedDocuments.salesRows, normalizedDocuments.maintenanceRows, normalizedLinks)
  const detailSummary = summarizeAbacusDetails(normalizedDocuments.salesRows, normalizedDocuments.maintenanceRows)
  if (manifest.summary && manifest.summary.imageCount !== undefined && manifest.summary.imageCount !== imageAttachments.length) throw new HttpError(409, `マニフェストの画像件数が一致しません: ${manifest.summary.imageCount} / ${imageAttachments.length}`)

  const existingRows = await validateExistingRows(database, organizationId, customerRows, vehicleRows)
  const documentRows = buildFinalDocumentRows(normalizedDocuments.salesRows, normalizedDocuments.maintenanceRows, normalizedLinks.documents)
  const existingDocuments = await validateExistingFinalDocuments(database, organizationId, documentRows)
  const now = new Date().toISOString()
  const statements = [
    ...createCustomerStatements(database, organizationId, customerRows, now),
    ...createVehicleStatements(database, organizationId, vehicleRows, now),
    ...(await createFinalDocumentStatements(database, organizationId, documentRows, now)),
  ]
  assertD1BatchStatementCount(statements.length)
  try {
    await database.$client.batch(statements as [D1PreparedStatement, ...D1PreparedStatement[]])
  } catch (error) {
    const message = error instanceof Error ? error.message : 'D1への登録に失敗しました。'
    throw new HttpError(409, `ABACUSの顧客・車両・書類を登録できませんでした。変更は反映されていません。${message.slice(0, 180)}`)
  }

  return jsonResponse({
    status: 'committed',
    manifestSha256,
    customerCount: customerRows.length,
    vehicleCount: vehicleRows.length,
    salesCount: salesRows.length,
    maintenanceCount: maintenanceRows.length,
    vehiclelessDocumentCount: normalizedLinks.documents.filter((link) => link.vehicleless).length,
    excludedDocumentCount: Array.isArray(manifest.excludedDocumentKeys) ? manifest.excludedDocumentKeys.length : 0,
    numberAdjustedDocumentCount: normalizedDocuments.numberAdjustedDocumentCount,
    amountDefaultedDocumentCount: normalizedDocuments.amountDefaultedDocumentCount,
    abacusDetailDocumentCount: detailSummary.mappedDocumentCount,
    abacusDetailReviewDocumentCount: detailSummary.reviewDocumentCount,
    abacusDetailUnsupportedDocumentCount: detailSummary.unsupportedDocumentCount,
    abacusDetailExcludedRowCount: detailSummary.excludedRowCount,
    abacusAmountOnlyRowCount: detailSummary.amountOnlyRowCount,
    abacusDetailMismatchDocumentCount: detailSummary.mismatchDocumentCount,
    imageCount: imageAttachments.length,
    customers: { imported: existingRows.newCustomerCount, updated: existingRows.existingCustomerCount },
    vehicles: { imported: existingRows.newVehicleCount, updated: existingRows.existingVehicleCount },
    documents: { imported: existingDocuments.newDocumentCount, existing: existingDocuments.existingDocumentCount },
  }, 200, env)
}

async function readGraphFinalFiles(formData: FormData, manifest: GraphFinalManifest) {
  if (!Array.isArray(manifest.dataFiles) || (manifest.dataFiles.length !== 5 && manifest.dataFiles.length !== 6)) throw new HttpError(400, 'Gate 8Aパッケージのファイル一覧が不正です。')
  const descriptors = new Map<string, FileDescriptor>()
  for (const value of manifest.dataFiles) {
    if (!value || typeof value !== 'object' || Array.isArray(value)) throw new HttpError(400, 'Gate 8Aパッケージのファイル記述が不正です。')
    const descriptor = value as Record<string, unknown>
    const fileName = normalizePackagePath(descriptor.fileName)
    const sizeBytes = descriptor.sizeBytes
    const sha256 = typeof descriptor.sha256 === 'string' ? descriptor.sha256.toUpperCase() : ''
    if (!['customers.csv', 'vehicles.csv', 'sales.csv', 'maintenance.csv', 'document-links.json', 'image-attachments.json'].includes(fileName) || descriptors.has(fileName) || typeof sizeBytes !== 'number' || !Number.isSafeInteger(sizeBytes) || sizeBytes <= 0 || !/^[0-9A-F]{64}$/u.test(sha256)) throw new HttpError(400, `Gate 8Aパッケージのファイル記述が不正です: ${fileName || '(空欄)'}`)
    descriptors.set(fileName, { relativePath: fileName, sizeBytes, sha256 })
  }
  const imageDescriptors = parseGraphFinalImageDescriptors(manifest.imageFiles)
  const hasImageAttachments = descriptors.has('image-attachments.json')
  if (hasImageAttachments !== (imageDescriptors.length > 0)) throw new HttpError(400, '画像対応表とマニフェストの画像一覧が一致しません。')
  const result = {} as Record<'customers' | 'vehicles' | 'sales' | 'maintenance' | 'links', { file: File; bytes: Uint8Array }> & { imageAttachments?: { file: File; bytes: Uint8Array }; imageDescriptors: Map<string, FileDescriptor> }
  const names = { customers: ['customers', 'customers.csv'], vehicles: ['vehicles', 'vehicles.csv'], sales: ['sales', 'sales.csv'], maintenance: ['maintenance', 'maintenance.csv'], links: ['documentLinks', 'document-links.json'] } as const
  for (const [key, [formKey, path]] of Object.entries(names) as Array<[keyof typeof names, readonly [string, string]]>) {
    const file = requiredFile(formData, formKey)
    const descriptor = descriptors.get(path)
    if (!descriptor || file.size !== descriptor.sizeBytes || file.size > maximumCsvBytes) throw new HttpError(409, `登録前パッケージのサイズが一致しません: ${path}`)
    const bytes = new Uint8Array(await file.arrayBuffer())
    if (await sha256Bytes(bytes) !== descriptor.sha256) throw new HttpError(409, `登録前パッケージのSHA-256が一致しません: ${path}`)
    result[key] = { file, bytes }
  }
  if (hasImageAttachments) {
    const file = requiredFile(formData, 'imageAttachments')
    const descriptor = descriptors.get('image-attachments.json')
    if (!descriptor || file.size !== descriptor.sizeBytes || file.size > maximumManifestBytes) throw new HttpError(409, '画像対応表のサイズが一致しません。')
    const bytes = new Uint8Array(await file.arrayBuffer())
    if (await sha256Bytes(bytes) !== descriptor.sha256) throw new HttpError(409, '画像対応表のSHA-256が一致しません。')
    result.imageAttachments = { file, bytes }
  }
  result.imageDescriptors = new Map(imageDescriptors.map((descriptor) => [descriptor.relativePath, descriptor]))
  return result
}

async function validateReadyEnvelope(formData: FormData, manifest: GraphFinalManifest) {
  const packageManifestFile = requiredFile(formData, 'packageManifest')
  const readyManifestFile = requiredFile(formData, 'readyManifest')
  if (packageManifestFile.size > maximumManifestBytes || readyManifestFile.size > maximumManifestBytes) throw new HttpError(413, 'ABACUS-Importマニフェストが大きすぎます。')
  const requestedPackageManifestSha256 = requiredSha256(textField(formData, 'packageManifestSha256'), 'abacus-import.json SHA-256')
  const requestedReadyManifestSha256 = requiredSha256(textField(formData, 'readyManifestSha256'), 'ready/manifest.json SHA-256')
  if (await sha256Bytes(new Uint8Array(await packageManifestFile.arrayBuffer())) !== requestedPackageManifestSha256) throw new HttpError(409, 'abacus-import.jsonのSHA-256が一致しません。')
  if (await sha256Bytes(new Uint8Array(await readyManifestFile.arrayBuffer())) !== requestedReadyManifestSha256) throw new HttpError(409, 'ready/manifest.jsonのSHA-256が一致しません。')
  let packageManifest: Record<string, unknown>
  let readyManifest: Record<string, unknown>
  try {
    packageManifest = JSON.parse(await decodeUtf8(new Uint8Array(await packageManifestFile.arrayBuffer()))) as Record<string, unknown>
    readyManifest = JSON.parse(await decodeUtf8(new Uint8Array(await readyManifestFile.arrayBuffer()))) as Record<string, unknown>
  } catch {
    throw new HttpError(400, 'ABACUS-ImportマニフェストのJSONが不正です。')
  }
  if (packageManifest.version !== 1 || packageManifest.kind !== 'abacus-import' || packageManifest.status !== 'ready' || packageManifest.readyPath !== 'ready' || packageManifest.readyManifest !== 'ready/manifest.json' || packageManifest.imageAcquisitionMethod !== 'fp5-vehicle-record' || typeof packageManifest.packageId !== 'string' || !packageManifest.packageId) throw new HttpError(400, 'abacus-import.jsonの完成状態または画像取得方式が不正です。')
  if (readyManifest.version !== 1 || readyManifest.kind !== 'abacus-import-ready' || readyManifest.status !== 'ready' || readyManifest.packageId !== packageManifest.packageId || readyManifest.imageAcquisitionMethod !== 'fp5-vehicle-record' || !Array.isArray(readyManifest.files) || !readyManifest.summary || typeof readyManifest.summary !== 'object') throw new HttpError(400, 'ready/manifest.jsonの完成状態が不正です。')

  const readyDescriptors = parseReadyEnvelopeDescriptors(readyManifest.files)
  const descriptorsByPath = new Map(readyDescriptors.map((descriptor) => [descriptor.path, descriptor]))
  const allowedReadyPaths = new Set([
    'data/customers.csv',
    'data/vehicles.csv',
    'data/sales-documents.csv',
    'data/maintenance-documents.csv',
    'mappings/customer-merges.json',
    'mappings/document-links.json',
    'mappings/image-attachments.json',
    'reports/excluded-documents.json',
    'reports/unresolved-items.json',
    'reports/image-acquisition-report.json',
    'reports/fp5-vehicle-image-mapping-report.json',
  ])
  for (const descriptor of readyDescriptors) {
    if (!descriptor.path.startsWith('images/') && !allowedReadyPaths.has(descriptor.path)) throw new HttpError(400, `readyマニフェストのファイルパスが許可されていません: ${descriptor.path}`)
    if (descriptor.path.startsWith('images/') && (descriptor.path.split('/').length !== 2 || !/\.(?:png|jpe?g)$/iu.test(descriptor.path))) throw new HttpError(400, `readyマニフェストの画像パスが不正です: ${descriptor.path}`)
  }
  const fieldPaths = [
    ['readyCustomers', 'data/customers.csv'],
    ['readyVehicles', 'data/vehicles.csv'],
    ['readySales', 'data/sales-documents.csv'],
    ['readyMaintenance', 'data/maintenance-documents.csv'],
    ['readyDocumentLinks', 'mappings/document-links.json'],
    ['readyImageAttachments', 'mappings/image-attachments.json'],
    ['readyCustomerMerges', 'mappings/customer-merges.json'],
    ['readyExcludedDocuments', 'reports/excluded-documents.json'],
    ['readyUnresolvedItems', 'reports/unresolved-items.json'],
    ['readyImageAcquisitionReport', 'reports/image-acquisition-report.json'],
    ['readyFp5MappingReport', 'reports/fp5-vehicle-image-mapping-report.json'],
  ] as const
  let checkedReadyFileCount = 0
  for (const [field, path] of fieldPaths) {
    const descriptor = descriptorsByPath.get(path)
    const value = formData.get(field)
    if (!descriptor) {
      if (value instanceof File) throw new HttpError(400, `readyマニフェストにないファイルが送信されました: ${path}`)
      continue
    }
    if (!(value instanceof File) || value.size === 0) throw new HttpError(400, `readyファイルが送信されていません: ${path}`)
    if (value.size !== descriptor.sizeBytes || value.size > maximumCsvBytes) throw new HttpError(409, `readyファイルのサイズが一致しません: ${path}`)
    const actual = await sha256Bytes(new Uint8Array(await value.arrayBuffer()))
    if (actual !== descriptor.sha256) throw new HttpError(409, `readyファイルのSHA-256が一致しません: ${path}`)
    checkedReadyFileCount += 1
  }
  const imageDescriptors = readyDescriptors.filter((descriptor) => descriptor.path.startsWith('images/'))
  const graphImageDescriptors = parseGraphFinalImageDescriptors(manifest.imageFiles)
  if (imageDescriptors.length !== graphImageDescriptors.length || imageDescriptors.some((descriptor) => {
    const graph = graphImageDescriptors.find((candidate) => candidate.relativePath === descriptor.path)
    return !graph || graph.sizeBytes !== descriptor.sizeBytes || graph.sha256 !== descriptor.sha256
  })) throw new HttpError(409, 'readyマニフェストとグラフ確定マニフェストの画像一覧が一致しません。')
  const imageDescriptorText = textField(formData, 'readyImageDescriptors')
  if (!imageDescriptorText) throw new HttpError(400, 'ready画像一覧がありません。')
  try {
    const listed = parseReadyEnvelopeDescriptors(JSON.parse(imageDescriptorText) as unknown[])
    if (listed.length !== imageDescriptors.length || listed.some((descriptor) => !imageDescriptors.some((candidate) => candidate.path === descriptor.path && candidate.sizeBytes === descriptor.sizeBytes && candidate.sha256 === descriptor.sha256))) throw new HttpError(409, '送信されたready画像一覧が一致しません。')
  } catch (error) {
    if (error instanceof HttpError) throw error
    throw new HttpError(400, 'ready画像一覧のJSONが不正です。')
  }

  const graphDataPaths = new Map([
    ['customers.csv', 'data/customers.csv'],
    ['vehicles.csv', 'data/vehicles.csv'],
    ['sales.csv', 'data/sales-documents.csv'],
    ['maintenance.csv', 'data/maintenance-documents.csv'],
    ['document-links.json', 'mappings/document-links.json'],
    ['image-attachments.json', 'mappings/image-attachments.json'],
  ])
  if (!Array.isArray(manifest.dataFiles)) throw new HttpError(400, 'グラフ確定マニフェストのファイル一覧が不正です。')
  for (const value of manifest.dataFiles) {
    if (!value || typeof value !== 'object' || Array.isArray(value)) throw new HttpError(400, 'グラフ確定マニフェストのファイル記述が不正です。')
    const descriptor = value as Record<string, unknown>
    const graphPath = normalizePackagePath(descriptor.fileName)
    const readyPath = graphDataPaths.get(graphPath)
    const ready = readyPath ? descriptorsByPath.get(readyPath) : undefined
    if (!ready || descriptor.sizeBytes !== ready.sizeBytes || textValue(descriptor.sha256).toUpperCase() !== ready.sha256) throw new HttpError(409, `グラフ確定マニフェストとreadyファイルが一致しません: ${graphPath}`)
  }
  const summary = readyManifest.summary as Record<string, unknown>
  const summaryPairs = [
    ['customerCount', manifest.summary?.customerRowCount],
    ['vehicleCount', manifest.summary?.vehicleRowCount],
    ['salesDocumentCount', manifest.summary?.salesRowCount],
    ['maintenanceDocumentCount', manifest.summary?.maintenanceRowCount],
    ['vehiclelessDocumentCount', manifest.summary?.vehiclelessDocumentCount],
    ['excludedDocumentCount', manifest.summary?.excludedDocumentCount],
    ['imageCount', manifest.summary?.imageCount],
  ] as const
  for (const [readyKey, graphValue] of summaryPairs) if (summary[readyKey] !== graphValue) throw new HttpError(409, `readyマニフェストの集計が一致しません: ${readyKey}`)
  return { checkedReadyFileCount: checkedReadyFileCount + imageDescriptors.length }
}

function parseReadyEnvelopeDescriptors(value: unknown): Array<{ path: string; sizeBytes: number; sha256: string }> {
  if (!Array.isArray(value) || value.length > maximumRows + 100) throw new HttpError(400, 'readyマニフェストのファイル一覧が不正です。')
  const paths = new Set<string>()
  return value.map((item, index) => {
    if (!item || typeof item !== 'object' || Array.isArray(item)) throw new HttpError(400, `readyマニフェストのファイル${index + 1}件目が不正です。`)
    const row = item as Record<string, unknown>
    const path = normalizePackagePath(row.path)
    const sizeBytes = row.sizeBytes
    const sha256 = textValue(row.sha256).toUpperCase()
    if (!path || paths.has(path) || typeof sizeBytes !== 'number' || !Number.isSafeInteger(sizeBytes) || sizeBytes <= 0 || !/^[0-9A-F]{64}$/u.test(sha256)) throw new HttpError(400, `readyマニフェストのファイル${index + 1}件目が不正です。`)
    paths.add(path)
    return { path, sizeBytes, sha256 }
  })
}

async function normalizeFinalCustomerNumbers(rows: CustomerRegistrationRow[]) {
  const counts = new Map<string, number>()
  for (const row of rows) counts.set(row.customerNumber, (counts.get(row.customerNumber) ?? 0) + 1)
  const used = new Set<string>()
  return Promise.all(rows.map(async (row) => {
    const needsGeneratedNumber = (counts.get(row.customerNumber) ?? 0) > 1 || /^ABACUS-CUSTOMER-NUMBER-?$/u.test(row.customerNumber)
    let customerNumber = needsGeneratedNumber ? `ABACUS-GRAPH-${(await stableFileId(row.id)).slice(0, 24)}` : row.customerNumber
    let suffix = 1
    while (used.has(customerNumber)) customerNumber = `${customerNumber.slice(0, 48)}-${suffix++}`
    used.add(customerNumber)
    return { ...row, customerNumber }
  }))
}

function parseFinalCustomers(text: string) {
  const rows = parseFinalRows(text, customerHeaders, 'customers.csv', maximumRows, false)
  const ids = new Set<string>()
  return rows.map((row, index) => {
    const id = requiredFinalIdentifier(row[0], '顧客ID', ['abacus-customer-', 'merge-preview:'])
    const customerNumber = requiredText(row[1], '顧客番号')
    const name = requiredText(row[2], '顧客名')
    if (ids.has(id)) throw new HttpError(400, `customers.csv ${index + 2}行目の顧客IDが重複しています。`)
    ids.add(id)
    return { id, customerNumber, name, nameKana: nullableText(row[3], 'ふりがな'), phone: nullableText(row[4], '電話番号'), email: nullableText(row[5], 'メールアドレス'), postalCode: nullableText(row[6], '郵便番号'), address: nullableText(row[7], '住所'), memo: nullableText(row[8], 'メモ'), vehicleCount: nonNegativeInteger(row[9], '車両台数') } satisfies CustomerRegistrationRow
  })
}

function parseFinalVehicles(text: string, customersRows: CustomerRegistrationRow[]) {
  const rows = parseFinalRows(text, vehicleHeaders, 'vehicles.csv', maximumRows, true)
  const ids = new Set<string>()
  return rows.map((row, index) => {
    const id = requiredFinalIdentifier(row[0], '車両ID', ['abacus-vehicle-'])
    const customerId = requiredFinalIdentifier(row[1], '顧客ID', ['abacus-customer-', 'merge-preview:'])
    const customer = customersRows.find((candidate) => candidate.id === customerId)
    if (!customer) throw new HttpError(400, `vehicles.csv ${index + 2}行目の顧客IDがcustomers.csvにありません。`)
    if (row[2].trim() !== customer.name) throw new HttpError(409, `vehicles.csv ${index + 2}行目の顧客名がcustomers.csvと一致しません。`)
    if (ids.has(id)) throw new HttpError(400, `vehicles.csv ${index + 2}行目の車両IDが重複しています。`)
    ids.add(id)
    return { id, customerId, customerName: requiredText(row[2], '顧客名'), maker: nullableText(row[3], 'メーカー'), name: requiredText(row[4], '車名'), model: nullableText(row[5], '型式'), registrationNumber: nullableText(row[6], '登録番号'), chassisNumber: nullableText(row[7], '車台番号'), modelYear: optionalInteger(row[8], '年式'), inspectionDate: optionalDate(row[9], '車検満了日'), mileage: optionalInteger(row[10], '走行距離'), bodyColor: nullableText(row[11], '車体色'), displacement: optionalInteger(row[12], '排気量'), transmission: nullableText(row[13], 'ミッション'), inspectionRecordAvailable: parseInspectionRecord(row[14]), memo: nullableText(row[15], '備考') } satisfies VehicleRegistrationRow
  })
}

function parseFinalSales(text: string) {
  const rows = parseFinalRows(text, finalSalesHeaders, 'sales.csv', maximumRows, true)
  const ids = new Set<string>()
  return rows.map((row, index) => {
    const id = requiredFinalIdentifier(row[0], '販売書類ID', ['abacus-sales-'])
    const number = requiredText(row[1], '販売書類番号')
    if (ids.has(id)) throw new HttpError(400, `sales.csv ${index + 2}行目の書類IDが重複しています。`)
    ids.add(id)
    const detailPayload = parseAbacusDetailPayload(row[15])
    const amounts = applyAbacusDetailAmounts(normalizeFinalDocumentAmounts(row[10], row[11], row[12], row[15]), detailPayload)
    return { id, number, type: requiredText(row[2], '書類種別'), status: normalizeImportedStatus(row[3]), customerName: requiredText(row[4], '顧客名'), vehicleName: row[5].trim(), registrationNumber: row[6].trim(), issuedAt: requiredDate(row[7], '発行日'), dueDate: optionalDate(row[8], '支払期限'), taxRate: detailPayload?.abacusTaxRate ?? nonNegativeInteger(row[9], '税率'), ...amounts, detailPayload, itemDescription: nullableText(row[13], '明細') ?? '', note: nullableText(row[14], '備考') } satisfies FinalSalesRow
  })
}

function parseFinalMaintenance(text: string) {
  const rows = parseFinalRows(text, finalMaintenanceHeaders, 'maintenance.csv', maximumRows, true)
  const ids = new Set<string>()
  return rows.map((row, index) => {
    const id = requiredFinalIdentifier(row[0], '整備書類ID', ['abacus-maintenance-'])
    const number = requiredText(row[1], '整備書類番号')
    if (ids.has(id)) throw new HttpError(400, `maintenance.csv ${index + 2}行目の書類IDが重複しています。`)
    ids.add(id)
    const intakeDate = optionalDate(row[8], '入庫日')
    const detailPayload = parseAbacusDetailPayload(row[17])
    const amounts = applyAbacusDetailAmounts(normalizeFinalDocumentAmounts(row[12], row[13], row[14], row[17]), detailPayload)
    return { id, number, type: requiredText(row[2], '書類種別'), category: normalizeMaintenanceCategory(row[3]), status: normalizeImportedStatus(row[4]), customerName: requiredText(row[5], '顧客名'), vehicleName: row[6].trim(), registrationNumber: row[7].trim(), intakeDate, plannedReleaseDate: optionalDate(row[9], '出庫予定日'), dueDate: optionalDate(row[10], '支払期限'), issuedAt: intakeDate ?? '1970-01-01', taxRate: nonNegativeInteger(row[11], '税率'), ...amounts, detailPayload, itemDescription: nullableText(row[15], '明細') ?? '', note: nullableText(row[16], '備考') } satisfies FinalMaintenanceRow
  })
}

function normalizeFinalDocumentAmounts(subtotalValue: string, taxValue: string, totalValue: string, detailsValue: string) {
  const subtotal = optionalInteger(subtotalValue, '小計')
  const tax = nonNegativeInteger(taxValue, '消費税')
  const total = optionalInteger(totalValue, '合計')
  const missing = [subtotal === null ? '小計' : '', total === null ? '合計' : ''].filter(Boolean)
  if (missing.length === 0) return { subtotal: subtotal as number, tax, total: total as number, details: detailsValue.trim(), amountDefaulted: false }

  const sourceDetails = detailsValue.trim()
  return {
    subtotal: subtotal ?? 0,
    tax,
    total: total ?? (subtotal ?? 0) + tax,
    details: sourceDetails,
    amountDefaulted: true,
  }
}

function parseAbacusDetailPayload(value: string): AbacusDetailPayload | null {
  const source = value.trim()
  if (!source) return null
  let parsed: unknown
  try { parsed = JSON.parse(source) } catch { return null }
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return null
  const record = parsed as Record<string, unknown>
  if (record.kind !== 'abacus-detail-lines') return null
  if (record.version !== 1 || !Array.isArray(record.lines) || record.lines.length > 100) throw new HttpError(400, 'ABACUS明細詳細JSONの形式が不正です。')
  const lines = record.lines.map((item, index) => {
    if (!item || typeof item !== 'object' || Array.isArray(item)) throw new HttpError(400, `ABACUS明細詳細JSONの${index + 1}行目が不正です。`)
    const line = item as Record<string, unknown>
    const sourceRowIndex = integerValue(line.sourceRowIndex)
    if (sourceRowIndex === null || sourceRowIndex < 1 || sourceRowIndex > 100) throw new HttpError(400, `ABACUS明細詳細JSONの元行番号が不正です: ${index + 1}`)
    return {
      description: nullableJsonText(line.description, 500),
      quantity: nullableJsonNumber(line.quantity),
      unit: nullableJsonText(line.unit, 50),
      unitPrice: nullableJsonInteger(line.unitPrice),
      partAmount: nullableJsonInteger(line.partAmount),
      technicalFees: nullableJsonInteger(line.technicalFees),
      summary: nullableJsonText(line.summary, 500),
      sourceRowIndex,
    }
  })
  const financialLines = Array.isArray(record.financialLines)
    ? record.financialLines.map((item, index) => {
      if (!item || typeof item !== 'object' || Array.isArray(item)) throw new HttpError(400, `ABACUS金額内訳JSONの${index + 1}行目が不正です。`)
      const line = item as Record<string, unknown>
      const sourceRowIndex = integerValue(line.sourceRowIndex)
      const amount = nullableJsonInteger(line.amount)
      if (sourceRowIndex === null || sourceRowIndex < 1 || sourceRowIndex > 100) throw new HttpError(400, `ABACUS金額内訳JSONの元行番号が不正です: ${index + 1}`)
      if (amount === null) throw new HttpError(400, `ABACUS金額内訳JSONの金額がありません: ${index + 1}`)
      const description = jsonText(line.description, 500)
      const itemType = jsonText(line.itemType, 100)
      const taxCategory = jsonText(line.taxCategory, 50)
      if (!description || !itemType || !taxCategory) throw new HttpError(400, `ABACUS金額内訳JSONの項目名・種別・税区分がありません: ${index + 1}`)
      return { description, itemType, taxCategory, amount, sourceRowIndex }
    })
    : []
  if (new Set([...lines.map((line) => line.sourceRowIndex), ...financialLines.map((line) => line.sourceRowIndex)]).size !== lines.length + financialLines.length) {
    throw new HttpError(400, 'ABACUS明細詳細JSONの元行番号が重複しています。')
  }
  if (new Set(lines.map((line) => line.sourceRowIndex)).size !== lines.length) throw new HttpError(400, 'ABACUS明細詳細JSONの元行番号が重複しています。')
  const matchStatus = record.matchStatus
  if (matchStatus !== 'matched' && matchStatus !== 'review' && matchStatus !== 'unmatched') throw new HttpError(400, 'ABACUS明細詳細JSONの対応付け状態が不正です。')
  const documentNumber = jsonText(record.documentNumber, 200)
  if (matchStatus === 'matched' && !documentNumber) throw new HttpError(400, 'ABACUS明細詳細JSONの書類番号がありません。')
  const detailAmount = nullableJsonInteger(record.detailAmount) ?? lines.reduce((sum, line) => sum + (line.partAmount ?? 0) + (line.technicalFees ?? 0), 0)
  return {
    version: 1,
    kind: 'abacus-detail-lines',
    sourceFile: jsonText(record.sourceFile, 200),
    recordIdHex: jsonText(record.recordIdHex, 200),
    documentNumber,
    customerName: jsonText(record.customerName, 200),
    vehicleName: jsonText(record.vehicleName, 200),
    registrationNumber: jsonText(record.registrationNumber, 100),
    chassisNumber: jsonText(record.chassisNumber, 100),
    lines,
    financialLines,
    partsSubtotal: nullableJsonInteger(record.partsSubtotal),
    technicalSubtotal: nullableJsonInteger(record.technicalSubtotal),
    abacusSubtotal: nullableJsonInteger(record.abacusSubtotal),
    abacusTotal: nullableJsonInteger(record.abacusTotal),
    abacusTax: nullableJsonInteger(record.abacusTax),
    abacusTaxRate: nullableJsonInteger(record.abacusTaxRate),
    detailAmount,
    excludedDetailCount: Math.max(0, integerValue(record.excludedDetailCount) ?? 0),
    amountOnlyRowCount: Math.max(0, integerValue(record.amountOnlyRowCount) ?? lines.filter((line) => !line.description && (line.partAmount !== null || line.unitPrice !== null || line.technicalFees !== null)).length),
    matchStatus,
    warning: jsonText(record.warning, 500),
  }
}

function applyAbacusDetailAmounts<T extends { subtotal: number; tax: number; total: number; details: string; amountDefaulted: boolean }>(amounts: T, detailPayload: AbacusDetailPayload | null) {
  if (!detailPayload) return { ...amounts, detailReport: null as AbacusDetailReport | null }
  if (detailPayload.matchStatus !== 'matched') return {
    ...amounts,
    detailReport: {
      isAbacusMigration: true,
      amountOnlyRowCount: detailPayload.amountOnlyRowCount,
      excludedDetailCount: detailPayload.excludedDetailCount,
      detailAmount: detailPayload.detailAmount,
      detailSubtotalDifference: null,
      detailTotalDifference: null,
      warning: detailPayload.warning || (detailPayload.matchStatus === 'review' ? 'UCS明細が複数候補のため、明細を登録せず要確認にしています。' : 'UCS明細が対応付けできないため、明細を登録せず要確認にしています。'),
    } satisfies AbacusDetailReport,
  }
  // 整備UCSの「税・単P」は税抜明細、販売UCSのabacusTax付き値は税抜小計です。
  // 税額が取得できない旧整備レコードの値で、CSV側の税抜補正を上書きしないようにします。
  const subtotal = detailPayload.abacusTax !== null
    ? (detailPayload.abacusSubtotal ?? amounts.subtotal)
    : amounts.subtotal
  const tax = detailPayload.abacusTax ?? amounts.tax
  const total = detailPayload.abacusTotal ?? amounts.total
  const rawDetailSubtotalDifference = detailPayload.abacusSubtotal === null ? null : detailPayload.detailAmount - detailPayload.abacusSubtotal
  // 販売のUCS明細は税込行、ABACUS小計は税抜なので、税額分の差は仕様上の一致として扱います。
  const detailSubtotalDifference = rawDetailSubtotalDifference !== null && detailPayload.abacusTax !== null && rawDetailSubtotalDifference === detailPayload.abacusTax
    ? 0
    : rawDetailSubtotalDifference
  const detailTotalDifference = detailPayload.abacusTotal === null ? null : detailPayload.detailAmount - detailPayload.abacusTotal
  const warnings = [
    detailSubtotalDifference !== null && detailSubtotalDifference !== 0 ? `明細合計とABACUS小計の差額=${detailSubtotalDifference}` : '',
    detailTotalDifference !== null && detailTotalDifference !== 0 ? `明細合計とABACUS合計の差額=${detailTotalDifference}` : '',
    amounts.amountDefaulted ? 'ABACUS側の小計・合計未入力項目を補完しました。' : '',
  ].filter(Boolean)
  return {
    ...amounts,
    subtotal,
    tax,
    total,
    detailReport: {
      isAbacusMigration: true,
      amountOnlyRowCount: detailPayload.amountOnlyRowCount,
      excludedDetailCount: detailPayload.excludedDetailCount,
      detailAmount: detailPayload.detailAmount,
      detailSubtotalDifference,
      detailTotalDifference,
      warning: warnings.length > 0 ? warnings.join(' / ') : null,
    } satisfies AbacusDetailReport,
  }
}

function jsonText(value: unknown, maximum: number) {
  return typeof value === 'string' ? value.normalize('NFKC').trim().slice(0, maximum) : ''
}

function nullableJsonText(value: unknown, maximum: number) {
  const text = jsonText(value, maximum)
  return text || null
}

function nullableJsonNumber(value: unknown) {
  if (value === null || value === undefined || value === '') return null
  const number = typeof value === 'number' ? value : Number(value)
  return Number.isFinite(number) ? number : null
}

function nullableJsonInteger(value: unknown) {
  const number = nullableJsonNumber(value)
  return number === null ? null : Math.round(number)
}

function integerValue(value: unknown) {
  const number = nullableJsonNumber(value)
  return number === null ? null : Math.round(number)
}

function normalizeGraphFinalDocumentNumbers(
  salesRows: FinalSalesRow[],
  maintenanceRows: FinalMaintenanceRow[],
  links: GraphFinalDocumentLink[],
) {
  const documentNumbers = new Map<string, string>()
  let numberAdjustedDocumentCount = 0
  const amountDefaultedDocumentCount = [...salesRows, ...maintenanceRows].filter((row) => row.amountDefaulted).length

  const normalizeRows = <T extends { id: string; number: string; details: string; detailPayload: AbacusDetailPayload | null; detailReport: AbacusDetailReport | null }>(rows: T[]) => {
    const used = new Set<string>()
    return rows.map((row) => {
      const originalNumber = row.number
      let number = originalNumber
      let suffix = 2
      while (used.has(number)) number = `${originalNumber}-${suffix++}`
      used.add(number)
      documentNumbers.set(row.id, number)
      if (number === originalNumber) return row
      numberAdjustedDocumentCount += 1
      if (row.detailPayload) {
        const detailReport = row.detailReport
          ? { ...row.detailReport, warning: [row.detailReport.warning, `ABACUS原書類番号=${originalNumber}`].filter(Boolean).join(' / ') }
          : {
              isAbacusMigration: true as const,
              amountOnlyRowCount: row.detailPayload.amountOnlyRowCount,
              excludedDetailCount: row.detailPayload.excludedDetailCount,
              detailAmount: row.detailPayload.detailAmount,
              detailSubtotalDifference: null,
              detailTotalDifference: null,
              warning: `ABACUS原書類番号=${originalNumber}`,
            }
        return { ...row, number, detailReport }
      }
      const sourceDetails = row.details.trim()
      const details = sourceDetails ? `${sourceDetails}\nABACUS原書類番号=${originalNumber}` : `ABACUS原書類番号=${originalNumber}`
      return { ...row, number, details }
    })
  }

  const normalizedSalesRows = normalizeRows(salesRows)
  const normalizedMaintenanceRows = normalizeRows(maintenanceRows)
  const normalizedLinks = links.map((link) => {
    const number = documentNumbers.get(link.documentId)
    return number && number !== link.documentNumber ? { ...link, documentNumber: number } : link
  })
  return { salesRows: normalizedSalesRows, maintenanceRows: normalizedMaintenanceRows, links: normalizedLinks, numberAdjustedDocumentCount, amountDefaultedDocumentCount }
}

function parseFinalRows(text: string, expectedHeaders: string[], label: string, maximum: number, allowEmpty: boolean) {
  const rows = parseCsv(text, label)
  if (rows.length === 0 || rows[0].length !== expectedHeaders.length || rows[0].some((value, index) => value !== expectedHeaders[index])) throw new HttpError(400, `${label}の見出し行がGate 8A形式と一致しません。`)
  const dataRows = rows.slice(1).filter((row) => row.some((value) => value.trim()))
  if ((!allowEmpty && dataRows.length === 0) || dataRows.length > maximum || dataRows.some((row) => row.length !== expectedHeaders.length)) throw new HttpError(400, `${label}の行形式が不正です。`)
  return dataRows
}

function parseFinalDocumentLinks(text: string) {
  let value: unknown
  try { value = JSON.parse(text) } catch { throw new HttpError(400, 'document-links.jsonのJSONが不正です。') }
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new HttpError(400, 'document-links.jsonの形式が不正です。')
  const document = value as Record<string, unknown>
  if (document.version !== 1 || document.kind !== 'abacus-export-import-document-links' || document.status !== 'finalization-preview' || !Array.isArray(document.documents) || !Array.isArray(document.excludedDocumentKeys)) throw new HttpError(400, 'document-links.jsonの種別または一覧が不正です。')
  const documents = document.documents.map((item, index) => {
    if (!item || typeof item !== 'object' || Array.isArray(item)) throw new HttpError(400, `document-links.json ${index + 1}件目が不正です。`)
    const row = item as Record<string, unknown>
    const documentKind = textValue(row.documentKind)
    if (documentKind !== '販売書類' && documentKind !== '整備書類') throw new HttpError(400, `document-links.json ${index + 1}件目の種別が不正です。`)
    return {
      documentKey: requiredText(textValue(row.documentKey), '書類キー'),
      documentId: requiredFinalIdentifier(textValue(row.documentId), '書類ID', documentKind === '販売書類' ? ['abacus-sales-'] : ['abacus-maintenance-']),
      documentKind,
      documentNumber: requiredText(textValue(row.documentNumber), '書類番号'),
      customerId: requiredFinalIdentifier(textValue(row.customerId), '顧客ID', ['abacus-customer-', 'merge-preview:']),
      customerName: requiredText(textValue(row.customerName), '顧客名'),
      vehicleId: textValue(row.vehicleId) ? requiredFinalIdentifier(textValue(row.vehicleId), '車両ID', ['abacus-vehicle-']) : null,
      vehicleName: textValue(row.vehicleName) || null,
      vehicleless: row.vehicleless === true,
      sourceLocation: requiredText(textValue(row.sourceLocation), '出典'),
      warning: nullableText(textValue(row.warning), '警告') ?? '',
    } satisfies GraphFinalDocumentLink
  })
  const excludedDocumentKeys = document.excludedDocumentKeys.map((item) => requiredText(textValue(item), '除外書類キー'))
  if (new Set(documents.map((item) => item.documentKey)).size !== documents.length) throw new HttpError(400, '書類キーが重複しています。')
  if (new Set(documents.map((item) => item.documentId)).size !== documents.length) throw new HttpError(400, '書類IDが重複しています。')
  if (new Set(excludedDocumentKeys).size !== excludedDocumentKeys.length) throw new HttpError(400, '除外書類キーが重複しています。')
  return { documents, excludedDocumentKeys }
}

function validateFinalPackage(
  manifest: GraphFinalManifest,
  customersRows: CustomerRegistrationRow[],
  vehicleRows: VehicleRegistrationRow[],
  salesRows: FinalSalesRow[],
  maintenanceRows: FinalMaintenanceRow[],
  links: { documents: GraphFinalDocumentLink[]; excludedDocumentKeys: string[] },
) {
  if (!isGraphFinalManifest(manifest)) throw new HttpError(400, 'Gate 8Aの登録前パッケージではありません。')
  const summary = manifest.summary
  const expectedSummary = {
    customerRowCount: customersRows.length,
    vehicleRowCount: vehicleRows.length,
    salesRowCount: salesRows.length,
    maintenanceRowCount: maintenanceRows.length,
    vehiclelessDocumentCount: links.documents.filter((document) => document.vehicleless).length,
    excludedDocumentCount: links.excludedDocumentKeys.length,
  }
  for (const [key, expected] of Object.entries(expectedSummary)) if (summary?.[key] !== expected) throw new HttpError(409, `マニフェストの集計が一致しません: ${key}`)
  if (!Array.isArray(manifest.groups) || manifest.groups.length !== customersRows.length) throw new HttpError(400, 'マニフェストの顧客グループがcustomers.csvと一致しません。')
  if (!Array.isArray(manifest.documents) || manifest.documents.length !== links.documents.length) throw new HttpError(400, 'マニフェストの書類一覧が対応表と一致しません。')
  const customerIds = new Set(customersRows.map((row) => row.id))
  const customerNames = new Map(customersRows.map((row) => [row.id, row.name]))
  const vehicleById = new Map(vehicleRows.map((row) => [row.id, row]))
  const documentIds = new Set<string>()
  for (const row of [...salesRows, ...maintenanceRows]) documentIds.add(row.id)
  const linkIds = new Set<string>()
  for (const link of links.documents) {
    if (linkIds.has(link.documentId) || !documentIds.has(link.documentId) || !customerIds.has(link.customerId) || customerNames.get(link.customerId) !== link.customerName) throw new HttpError(409, `書類対応表の顧客またはIDがCSVと一致しません: ${link.documentId}`)
    const csvRow = [...salesRows, ...maintenanceRows].find((row) => row.id === link.documentId)
    if (!csvRow || link.vehicleless !== (!csvRow.vehicleName && !csvRow.registrationNumber) || link.vehicleName !== (csvRow.vehicleName || null)) throw new HttpError(409, `車両なし判定とCSVの車両欄が一致しません: ${link.documentId}`)
    if (link.vehicleless !== !link.vehicleId) throw new HttpError(409, `車両なし判定と車両IDが一致しません: ${link.documentId}`)
    if (link.vehicleId) {
      const vehicle = vehicleById.get(link.vehicleId)
      if (!vehicle || vehicle.customerId !== link.customerId) throw new HttpError(409, `書類対応表の車両と顧客が一致しません: ${link.documentId}`)
    }
    linkIds.add(link.documentId)
  }
  if (linkIds.size !== documentIds.size) throw new HttpError(409, '販売・整備CSVと書類対応表の件数が一致しません。')
  const groupIds = new Set<string>()
  for (const group of manifest.groups) {
    if (!group || typeof group !== 'object' || Array.isArray(group)) throw new HttpError(400, 'マニフェストの顧客グループが不正です。')
    const row = group as Record<string, unknown>
    const customerId = textValue(row.customerId)
    if (!customerIds.has(customerId) || row.approved !== true || groupIds.has(customerId) || !Array.isArray(row.sourceCustomerIds) || row.sourceCustomerIds.length === 0 || row.sourceCustomerIds.some((value) => typeof value !== 'string' || !value.trim())) throw new HttpError(400, 'マニフェストの顧客グループが不正です。')
    groupIds.add(customerId)
  }
  if (groupIds.size !== customerIds.size) throw new HttpError(400, 'マニフェストの顧客グループがcustomers.csvを網羅していません。')
  const manifestExcluded = Array.isArray(manifest.excludedDocumentKeys) ? manifest.excludedDocumentKeys.map((value) => textValue(value)).sort() : []
  if (manifestExcluded.join('\u0000') !== links.excludedDocumentKeys.slice().sort().join('\u0000')) throw new HttpError(409, 'マニフェストと書類対応表の除外一覧が一致しません。')
  const manifestDocumentIds = new Set<string>()
  for (const item of manifest.documents) {
    if (!item || typeof item !== 'object' || Array.isArray(item)) throw new HttpError(400, 'マニフェストの書類一覧が不正です。')
    const row = item as Record<string, unknown>
    const id = textValue(row.documentId)
    const link = links.documents.find((candidate) => candidate.documentId === id)
    if (!link || textValue(row.documentKey) !== link.documentKey || textValue(row.kind) !== link.documentKind || textValue(row.customerId) !== link.customerId || textValue(row.sourceLocation) !== link.sourceLocation || Boolean(row.vehicleless) !== link.vehicleless || textValue(row.vehicleId) !== (link.vehicleId ?? '')) throw new HttpError(409, `マニフェストと書類対応表が一致しません: ${id}`)
    manifestDocumentIds.add(id)
  }
  if (manifestDocumentIds.size !== links.documents.length) throw new HttpError(409, 'マニフェストの書類IDが重複または不足しています。')
}

function buildFinalDocumentRows(salesRows: FinalSalesRow[], maintenanceRows: FinalMaintenanceRow[], links: GraphFinalDocumentLink[]) {
  const linksById = new Map(links.map((link) => [link.documentId, link]))
  const result: Array<{ kind: 'sales' | 'maintenance'; link: GraphFinalDocumentLink; row: FinalSalesRow | FinalMaintenanceRow }> = []
  for (const row of salesRows) {
    const link = linksById.get(row.id)
    if (!link || link.documentKind !== '販売書類' || link.documentNumber !== row.number || link.customerName !== row.customerName) throw new HttpError(409, `販売書類と対応表が一致しません: ${row.id}`)
    result.push({ kind: 'sales', link, row })
  }
  for (const row of maintenanceRows) {
    const link = linksById.get(row.id)
    if (!link || link.documentKind !== '整備書類' || link.documentNumber !== row.number || link.customerName !== row.customerName) throw new HttpError(409, `整備書類と対応表が一致しません: ${row.id}`)
    result.push({ kind: 'maintenance', link, row })
  }
  return result
}

function summarizeAbacusDetails(salesRows: FinalSalesRow[], maintenanceRows: FinalMaintenanceRow[]) {
  const rows = [...salesRows, ...maintenanceRows]
  return {
    mappedDocumentCount: rows.filter((row) => row.detailPayload?.matchStatus === 'matched').length,
    reviewDocumentCount: rows.filter((row) => row.detailPayload?.matchStatus === 'review').length,
    unsupportedDocumentCount: rows.filter((row) => row.detailPayload?.matchStatus === 'unmatched').length,
    excludedRowCount: rows.reduce((sum, row) => sum + (row.detailPayload?.excludedDetailCount ?? 0), 0),
    amountOnlyRowCount: rows.reduce((sum, row) => sum + (row.detailPayload?.amountOnlyRowCount ?? 0), 0),
    mismatchDocumentCount: rows.filter((row) => Boolean(row.detailReport?.warning)).length,
  }
}

async function validateExistingFinalDocuments(database: ReturnType<typeof createDatabase>, organizationId: string, rows: Array<{ kind: 'sales' | 'maintenance'; link: GraphFinalDocumentLink; row: FinalSalesRow | FinalMaintenanceRow }>) {
  const sales = rows.filter((row): row is { kind: 'sales'; link: GraphFinalDocumentLink; row: FinalSalesRow } => row.kind === 'sales')
  const maintenance = rows.filter((row): row is { kind: 'maintenance'; link: GraphFinalDocumentLink; row: FinalMaintenanceRow } => row.kind === 'maintenance')
  const existingSales: Array<typeof salesDocuments.$inferSelect> = []
  const existingMaintenance: Array<typeof maintenanceDocuments.$inferSelect> = []
  const existingSalesByNumber: Array<typeof salesDocuments.$inferSelect> = []
  const existingMaintenanceByNumber: Array<typeof maintenanceDocuments.$inferSelect> = []
  for (const chunk of chunked(sales.map((row) => row.row.id), maximumQueryValues)) if (chunk.length > 0) existingSales.push(...await database.select().from(salesDocuments).where(inArray(salesDocuments.id, chunk)).all())
  for (const chunk of chunked(maintenance.map((row) => row.row.id), maximumQueryValues)) if (chunk.length > 0) existingMaintenance.push(...await database.select().from(maintenanceDocuments).where(inArray(maintenanceDocuments.id, chunk)).all())
  for (const chunk of chunked(sales.map((row) => row.row.number), maximumQueryValues)) if (chunk.length > 0) existingSalesByNumber.push(...await database.select().from(salesDocuments).where(and(eq(salesDocuments.organizationId, organizationId), inArray(salesDocuments.number, chunk))).all())
  for (const chunk of chunked(maintenance.map((row) => row.row.number), maximumQueryValues)) if (chunk.length > 0) existingMaintenanceByNumber.push(...await database.select().from(maintenanceDocuments).where(and(eq(maintenanceDocuments.organizationId, organizationId), inArray(maintenanceDocuments.number, chunk))).all())
  let existingDocumentCount = 0
  for (const item of sales) {
    const existing = existingSales.find((candidate) => candidate.id === item.row.id)
    if (existing && existing.organizationId !== organizationId) throw new HttpError(409, `販売書類IDが別組織です: ${item.row.id}`)
    const sameNumber = existingSalesByNumber.find((candidate) => candidate.number === item.row.number)
    if (sameNumber && sameNumber.id !== item.row.id) throw new HttpError(409, `販売書類番号が既存書類と競合します: ${item.row.number}`)
    if (existing) {
      if (existing.customerId !== item.link.customerId || existing.vehicleId !== item.link.vehicleId || existing.number !== item.row.number || existing.issuedAt !== item.row.issuedAt || existing.subtotal !== item.row.subtotal || existing.tax !== item.row.tax || existing.total !== item.row.total) throw new HttpError(409, `既存販売書類の内容が登録前パッケージと異なります: ${item.row.id}`)
      existingDocumentCount += 1
    }
  }
  for (const item of maintenance) {
    const existing = existingMaintenance.find((candidate) => candidate.id === item.row.id)
    if (existing && existing.organizationId !== organizationId) throw new HttpError(409, `整備書類IDが別組織です: ${item.row.id}`)
    const sameNumber = existingMaintenanceByNumber.find((candidate) => candidate.number === item.row.number)
    if (sameNumber && sameNumber.id !== item.row.id) throw new HttpError(409, `整備書類番号が既存書類と競合します: ${item.row.number}`)
    if (existing) {
      if (existing.customerId !== item.link.customerId || existing.vehicleId !== item.link.vehicleId || existing.number !== item.row.number || existing.issuedAt !== item.row.issuedAt || existing.subtotal !== item.row.subtotal || existing.tax !== item.row.tax || existing.total !== item.row.total) throw new HttpError(409, `既存整備書類の内容が登録前パッケージと異なります: ${item.row.id}`)
      existingDocumentCount += 1
    }
  }
  return { existingDocumentCount, newDocumentCount: rows.length - existingDocumentCount }
}

async function createFinalDocumentStatements(database: ReturnType<typeof createDatabase>, organizationId: string, rows: Array<{ kind: 'sales' | 'maintenance'; link: GraphFinalDocumentLink; row: FinalSalesRow | FinalMaintenanceRow }>, now: string) {
  const statements: D1PreparedStatement[] = []
  const salesRows = rows.filter((row): row is { kind: 'sales'; link: GraphFinalDocumentLink; row: FinalSalesRow } => row.kind === 'sales')
  const maintenanceRows = rows.filter((row): row is { kind: 'maintenance'; link: GraphFinalDocumentLink; row: FinalMaintenanceRow } => row.kind === 'maintenance')
  for (const chunk of chunked(salesRows, maximumSalesDocumentBatchRows)) {
    const placeholders = chunk.map(() => '(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)').join(',')
    const values = chunk.flatMap(({ link, row }) => [row.id, organizationId, row.number, row.type, row.status, link.customerId, link.vehicleId, row.issuedAt, row.dueDate, row.taxRate, '切り捨て', row.subtotal, row.tax, row.total, row.note, JSON.stringify(buildAbacusDetailsEnvelope(link, row)), now])
    statements.push(database.$client.prepare(`INSERT INTO sales_documents (id, organization_id, number, type, status, customer_id, vehicle_id, issued_at, due_date, tax_rate, tax_rounding, subtotal, tax, total, note, details_json, updated_at) VALUES ${placeholders} ON CONFLICT(id) DO NOTHING`).bind(...values))
  }
  for (const chunk of chunked(maintenanceRows, maximumMaintenanceDocumentRows)) {
    const placeholders = chunk.map(() => '(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)').join(',')
    const values = chunk.flatMap(({ link, row }) => [row.id, organizationId, row.number, row.type, row.category, row.status, link.customerId, link.vehicleId, row.intakeDate, row.plannedReleaseDate, null, row.issuedAt, row.dueDate, row.taxRate, '切り捨て', row.subtotal, row.tax, row.total, row.note, JSON.stringify(buildAbacusDetailsEnvelope(link, row)), now])
    statements.push(database.$client.prepare(`INSERT INTO maintenance_documents (id, organization_id, number, type, category, status, customer_id, vehicle_id, intake_date, planned_release_date, completion_date, issued_at, due_date, tax_rate, tax_rounding, subtotal, tax, total, note, details_json, updated_at) VALUES ${placeholders} ON CONFLICT(id) DO NOTHING`).bind(...values))
  }
  const items = await Promise.all(rows.flatMap(({ kind, link, row }) => detailLinesForRow(kind, row).map((line) => ({ kind, link, row, line }))).map(async (item) => ({ ...item, id: await stableFileId(`${organizationId}\u0000${item.row.id}\u0000detail\u0000${item.line.line.sourceRowIndex}`) })))
  for (const chunk of chunked(items, Math.min(maximumSalesItemBatchRows, maximumMaintenanceItemBatchRows))) {
    const salesItems = chunk.filter((item) => item.kind === 'sales')
    if (salesItems.length > 0) {
      const placeholders = salesItems.map(() => '(?,?,?,?,?,?,?,?,?,?,?,?,?)').join(',')
      const values = salesItems.flatMap((item) => { const row = item.row as FinalSalesRow; const normalized = salesDetailValues(item.line.line); return [item.id, organizationId, row.id, item.line.itemType, normalized.description, normalized.quantity, normalized.unit, normalized.unitPrice, item.line.taxCategory, normalized.otherAmount, normalized.summary, normalized.amount, normalized.sourceRowIndex] })
      statements.push(database.$client.prepare(`INSERT INTO sales_document_items (id, organization_id, document_id, item_type, description, quantity, unit, unit_price, tax_category, other_amount, summary, amount, sort_order) VALUES ${placeholders} ON CONFLICT(id) DO NOTHING`).bind(...values))
    }
    const maintenanceItemsForChunk = chunk.filter((item) => item.kind === 'maintenance')
    if (maintenanceItemsForChunk.length > 0) {
      const placeholders = maintenanceItemsForChunk.map(() => '(?,?,?,?,?,?,?,?,?,?,?,?)').join(',')
      const values = maintenanceItemsForChunk.flatMap((item) => { const row = item.row as FinalMaintenanceRow; const normalized = maintenanceDetailValues(item.line.line); return [item.id, organizationId, row.id, normalized.kind, normalized.description, normalized.quantity, normalized.unit, normalized.unitPrice, normalized.technicalFee, normalized.summary, normalized.amount, normalized.sourceRowIndex] })
      statements.push(database.$client.prepare(`INSERT INTO maintenance_items (id, organization_id, document_id, item_type, description, quantity, unit, unit_price, technical_fee, summary, amount, sort_order) VALUES ${placeholders} ON CONFLICT(id) DO NOTHING`).bind(...values))
    }
  }
  return statements
}

function buildAbacusDetailsEnvelope(link: GraphFinalDocumentLink, row: FinalSalesRow | FinalMaintenanceRow) {
  return {
    version: 2,
    abacusImport: { documentKey: link.documentKey, sourceLocation: link.sourceLocation, vehicleless: link.vehicleless },
    sourceDetails: row.details,
    abacusDetails: row.detailPayload,
    abacusDetailReport: row.detailReport,
    abacusAmounts: { subtotal: row.subtotal, tax: row.tax, total: row.total },
    amountDefaulted: row.amountDefaulted,
    amountWarning: row.amountDefaulted ? 'ABACUS金額未設定（小計・合計を補完して登録）' : null,
  }
}

function detailLinesForRow(kind: 'sales' | 'maintenance', row: FinalSalesRow | FinalMaintenanceRow): ImportedDetailLine[] {
  if (row.detailPayload) {
    // UCSに対応付けできなかった書類へ、内容不明の0円行を作らないようにします。
    if (row.detailPayload.matchStatus !== 'matched') return []
    const detailLines = row.detailPayload.lines.map((line) => ({
      line,
      itemType: kind === 'sales' ? '付属品・特別仕様' : '作業',
      taxCategory: '課税',
    }))
    if (kind === 'sales') {
      const financialLines = row.detailPayload.financialLines.map((financialLine) => ({
        line: {
          description: financialLine.description,
          quantity: 1,
          unit: '式',
          unitPrice: financialLine.amount,
          partAmount: financialLine.amount,
          technicalFees: null,
          summary: null,
          sourceRowIndex: financialLine.sourceRowIndex,
        },
        itemType: financialLine.itemType,
        taxCategory: financialLine.taxCategory,
      }))
      return [...detailLines, ...financialLines]
    }
    return detailLines
  }
  return [{
    line: {
      description: row.itemDescription || `ABACUS${kind === 'sales' ? '販売' : '整備'}書類 #${row.number}`,
      quantity: 1,
      unit: '式',
      unitPrice: row.subtotal,
      partAmount: row.subtotal,
      technicalFees: kind === 'maintenance' ? 0 : null,
      summary: null,
      sourceRowIndex: 0,
    },
    itemType: kind === 'sales' ? 'その他' : '作業',
    taxCategory: '課税',
  }]
}

function salesDetailValues(line: AbacusDetailLine) {
  const quantity = line.quantity ?? 1
  const unitPrice = line.unitPrice ?? 0
  const base = Math.round(quantity * unitPrice)
  const amount = line.partAmount ?? base
  const otherAmount = amount - base + (line.technicalFees ?? 0)
  return {
    description: line.description ?? '',
    quantity,
    unit: line.unit ?? '式',
    unitPrice,
    otherAmount,
    summary: line.summary ?? '',
    amount: base + otherAmount,
    sourceRowIndex: line.sourceRowIndex,
  }
}

function maintenanceDetailValues(line: AbacusDetailLine) {
  const quantity = line.quantity ?? 1
  const unitPrice = line.unitPrice ?? (line.partAmount ?? 0)
  const technicalFee = line.technicalFees ?? 0
  const amount = line.partAmount ?? Math.round(quantity * unitPrice)
  return {
    kind: '作業',
    description: line.description ?? '',
    quantity,
    unit: line.unit ?? '式',
    unitPrice,
    technicalFee,
    summary: line.summary ?? '',
    amount: amount + technicalFee,
    sourceRowIndex: line.sourceRowIndex,
  }
}

function isGraphFinalManifest(value: unknown): value is GraphFinalManifest {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false
  const manifest = value as Record<string, unknown>
  return manifest.version === 1 && manifest.kind === 'abacus-export-import-final-package' && manifest.status === 'registration-preview'
}

async function uploadRegistrationImage(request: Request, env: Env, database: ReturnType<typeof createDatabase>, organizationId: string) {
  assertRequestContentLength(request, maximumAttachmentSize + 512 * 1024, { required: true })
  const formData = await readFormData(request, maximumAttachmentSize + 512 * 1024)
  const vehicleId = requiredIdentifier(textField(formData, 'vehicleId'), '車両ID', 'abacus-vehicle-')
  const customerId = requiredGraphOrLegacyCustomerIdentifier(textField(formData, 'customerId'), '顧客ID')
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

function parseGraphFinalImageDescriptors(value: unknown): FileDescriptor[] {
  if (value === undefined) return []
  if (!Array.isArray(value) || value.length > maximumRows) throw new HttpError(400, 'グラフ確定パッケージの画像一覧が不正です。')
  const paths = new Set<string>()
  return value.map((item, index) => {
    if (!item || typeof item !== 'object' || Array.isArray(item)) throw new HttpError(400, `グラフ確定パッケージの画像記述${index + 1}件目が不正です。`)
    const descriptor = item as Record<string, unknown>
    const relativePath = requiredImagePath(textValue(descriptor.fileName))
    const sizeBytes = descriptor.sizeBytes
    const sha256 = typeof descriptor.sha256 === 'string' ? descriptor.sha256.toUpperCase() : ''
    if (typeof sizeBytes !== 'number' || !Number.isSafeInteger(sizeBytes) || sizeBytes <= 0 || sizeBytes > maximumAttachmentSize || !/^[0-9A-F]{64}$/u.test(sha256) || paths.has(relativePath)) throw new HttpError(400, `グラフ確定パッケージの画像記述${index + 1}件目が不正です。`)
    paths.add(relativePath)
    return { relativePath, sizeBytes, sha256 }
  })
}

function parseGraphFinalImageAttachments(text: string, imageFiles: Map<string, FileDescriptor>): ImageAttachment[] {
  let value: unknown
  try { value = JSON.parse(text) } catch { throw new HttpError(400, 'グラフ確定パッケージのimage-attachments.jsonが不正です。') }
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new HttpError(400, 'グラフ確定パッケージのimage-attachments.jsonが不正です。')
  const document = value as Record<string, unknown>
  if (document.version !== 1 || document.kind !== 'abacus-web-import-image-attachments' || document.status !== 'manual-upload-required' || !Array.isArray(document.attachments)) throw new HttpError(400, 'グラフ確定パッケージの画像対応表の種別が不正です。')
  if (document.attachments.length !== imageFiles.size || document.attachments.length > maximumRows) throw new HttpError(400, 'グラフ確定パッケージの画像件数が一致しません。')
  const paths = new Set<string>()
  return document.attachments.map((item, index) => {
    if (!item || typeof item !== 'object' || Array.isArray(item)) throw new HttpError(400, `グラフ確定パッケージの画像対応表${index + 1}件目が不正です。`)
    const row = item as Record<string, unknown>
    const customerId = requiredText(textValue(row.customerId), '画像対応表の顧客ID')
    const vehicleId = requiredText(textValue(row.vehicleId), '画像対応表の車両ID')
    const imagePath = requiredImagePath(textValue(row.imagePath))
    const imageSha256 = requiredSha256(textValue(row.imageSha256), '画像SHA-256')
    const contentType = textValue(row.contentType)
    if (!imageFiles.has(imagePath) || imageFiles.get(imagePath)?.sha256 !== imageSha256 || (contentType !== 'image/png' && contentType !== 'image/jpeg') || paths.has(imagePath)) throw new HttpError(400, `グラフ確定パッケージの画像対応表参照が不正です: ${imagePath}`)
    paths.add(imagePath)
    return { customerId, vehicleId, imagePath, imageSha256, contentType }
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
  for (const chunk of chunked(ids, maximumQueryValues)) {
    if (chunk.length === 0) continue
    rows.push(...await database.select().from(customers).where(inArray(customers.id, chunk)).all())
  }
  return rows
}

async function loadVehiclesByIds(database: ReturnType<typeof createDatabase>, ids: string[]) {
  const rows: Array<typeof vehicles.$inferSelect> = []
  for (const chunk of chunked(ids, maximumQueryValues)) {
    if (chunk.length === 0) continue
    rows.push(...await database.select().from(vehicles).where(inArray(vehicles.id, chunk)).all())
  }
  return rows
}

async function loadByNumbers<T extends typeof customers>(database: ReturnType<typeof createDatabase>, table: T, organizationId: string, values: string[]) {
  const rows: Array<typeof customers.$inferSelect> = []
  for (const chunk of chunked(values, maximumQueryValues)) {
    if (chunk.length === 0) continue
    rows.push(...await database.select().from(table).where(and(eq(table.organizationId, organizationId), inArray(table.customerNumber, chunk))).all())
  }
  return rows
}

function createCustomerStatements(database: ReturnType<typeof createDatabase>, organizationId: string, rows: CustomerRegistrationRow[], updatedAt: string) {
  return chunked(rows, maximumCustomerBatchRows).map((chunk) => {
    const placeholders = chunk.map(() => '(?,?,?,?,?,?,?,?,?,?,?)').join(',')
    const values = chunk.flatMap((row) => [row.id, organizationId, row.customerNumber, row.name, row.nameKana, row.postalCode, row.address, row.phone, row.email, row.memo, updatedAt])
    return database.$client.prepare(`INSERT INTO customers (id, organization_id, customer_number, name, name_kana, postal_code, address, phone, email, memo, updated_at) VALUES ${placeholders} ON CONFLICT(id) DO UPDATE SET customer_number=excluded.customer_number, name=excluded.name, name_kana=excluded.name_kana, postal_code=excluded.postal_code, address=excluded.address, phone=excluded.phone, email=excluded.email, memo=excluded.memo, updated_at=excluded.updated_at`).bind(...values)
  })
}

function createVehicleStatements(database: ReturnType<typeof createDatabase>, organizationId: string, rows: VehicleRegistrationRow[], updatedAt: string) {
  return chunked(rows, maximumVehicleBatchRows).map((chunk) => {
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

function parseManifest(text: string): RegistrationManifest | GraphFinalManifest {
  try { return JSON.parse(text) as RegistrationManifest | GraphFinalManifest } catch { throw new HttpError(400, 'manifest.jsonのJSONが不正です。') }
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

function requiredGraphOrLegacyCustomerIdentifier(value: string, label: string) {
  const normalized = requiredText(value, label)
  if (normalized.length > 200 || !['abacus-customer-group-', 'abacus-customer-', 'merge-preview:'].some((prefix) => normalized.startsWith(prefix)) || /[\\/\u0000]/u.test(normalized) || normalized.includes('..')) throw new HttpError(400, `${label}の形式が不正です。`)
  return normalized
}

function requiredFinalIdentifier(value: string, label: string, prefixes: string[]) {
  const normalized = requiredText(value, label)
  if (normalized.length > 200 || !prefixes.some((prefix) => normalized.startsWith(prefix)) || /[\\/\u0000]/u.test(normalized) || normalized.includes('..')) throw new HttpError(400, `${label}の形式が不正です。`)
  return normalized
}

function requiredDate(value: string, label: string) {
  const normalized = optionalDate(value, label)
  if (!normalized) throw new HttpError(400, `${label}を入力してください。`)
  return normalized
}

function normalizeImportedStatus(value: string) {
  const normalized = requiredText(value, 'ステータス')
  if (normalized === '発行済み' || normalized === '受付中' || normalized === '作業中') return normalized === '発行済み' ? '完了' : '下書き'
  if (!['下書き', '入金待ち', '完了', 'アーカイブ済み'].includes(normalized)) throw new HttpError(400, `ステータスが不正です: ${normalized}`)
  return normalized
}

function normalizeMaintenanceCategory(value: string) {
  const normalized = requiredText(value, '入庫区分')
  if (normalized === '車検' || normalized === '板金' || normalized === '一般整備') return normalized
  return '一般整備'
}

function requiredSha256(value: string, label: string) {
  const normalized = value.trim().toUpperCase()
  if (!/^[0-9A-F]{64}$/u.test(normalized)) throw new HttpError(400, `${label}の形式が不正です。`)
  return normalized
}

function requiredImagePath(value: string) {
  const normalized = normalizePackagePath(value)
  if (!normalized.startsWith('images/') || normalized.split('/').length !== 2 || normalized.length > 240 || !/\.(?:png|jpe?g)$/iu.test(normalized)) throw new HttpError(400, '画像パスの形式が不正です。')
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
