export type GraphFinalFileDescriptor = {
  fileName: string
  sizeBytes: number
  sha256: string
}

export type GraphFinalGroup = {
  groupKey: string
  origin: string
  approved: boolean
  sourceCustomerIds: string[]
  customerId: string
  customerName: string
}

export type GraphFinalManifestDocument = {
  documentKey: string
  documentId: string
  kind: string
  customerId: string
  vehicleId?: string | null
  sourceLocation: string
  vehicleless: boolean
}

export type GraphFinalManifest = {
  version: number
  kind: 'abacus-export-import-final-package'
  status: 'registration-preview'
  source?: {
    candidatePackagePath?: string
    candidateManifestSha256?: string
  }
  summary: {
    customerRowCount: number
    vehicleRowCount: number
    salesRowCount: number
    maintenanceRowCount: number
    vehiclelessDocumentCount: number
    excludedDocumentCount: number
    imageCount?: number
  }
  dataFiles: GraphFinalFileDescriptor[]
  imageFiles?: GraphFinalFileDescriptor[]
  warnings: string[]
  groups: GraphFinalGroup[]
  documents: GraphFinalManifestDocument[]
  excludedDocumentKeys: string[]
}

export type GraphFinalDocumentLink = {
  documentKey: string
  documentId: string
  documentKind: '販売書類' | '整備書類'
  documentNumber: string
  customerId: string
  customerName: string
  vehicleId?: string | null
  vehicleName?: string | null
  vehicleless: boolean
  sourceLocation: string
  warning: string
}

export type GraphFinalImageAttachment = {
  customerId: string
  vehicleId: string
  imagePath: string
  imageSha256: string
  contentType: 'image/png' | 'image/jpeg'
}

export type GraphFinalPackageValidation = {
  format: 'graph-final'
  manifest: GraphFinalManifest
  files: Map<string, File>
  customerRows: string[][]
  vehicleRows: string[][]
  salesRows: string[][]
  maintenanceRows: string[][]
  documents: GraphFinalDocumentLink[]
  imageAttachments: GraphFinalImageAttachment[]
  excludedDocumentKeys: string[]
  checkedFileCount: number
  checkedImageCount: number
  manifestSha256: string
  abacusImport?: AbacusImportPackageContext
}

export type AbacusImportReadyFileDescriptor = {
  path: string
  sizeBytes: number
  sha256: string
}

export type AbacusImportPackageContext = {
  rootManifestFile: File
  readyManifestFile: File
  rootManifestSha256: string
  readyManifestSha256: string
  readyFiles: Map<string, File>
  readyDescriptors: AbacusImportReadyFileDescriptor[]
}

type AbacusImportRootManifest = {
  version: number
  kind: 'abacus-import'
  status: 'ready'
  packageId: string
  readyPath: string
  readyManifest: string
  imageAcquisitionMethod: 'fp5-vehicle-record'
}

type AbacusImportReadyManifest = {
  version: number
  kind: 'abacus-import-ready'
  status: 'ready'
  packageId: string
  imageAcquisitionMethod: 'fp5-vehicle-record'
  summary: {
    customerCount: number
    vehicleCount: number
    salesDocumentCount: number
    maintenanceDocumentCount: number
    vehiclelessDocumentCount: number
    excludedDocumentCount: number
    imageCount: number
  }
  files: AbacusImportReadyFileDescriptor[]
}

type ValidationFailure = Error & { details?: string[] }
type PackageFileMap = Map<string, File>

const customerHeaders = ['顧客ID', '顧客番号', '顧客名', 'ふりがな', '電話番号', 'メールアドレス', '郵便番号', '住所', 'メモ', '車両台数']
const vehicleHeaders = ['車両ID', '顧客ID', '顧客名', 'メーカー', '車名', '型式', '登録番号', '車台番号', '年式', '車検満了日', '走行距離', '車体色', '排気量', 'ミッション', '記録簿', '備考']
const salesHeaders = ['書類ID', '書類番号', '書類種別', 'ステータス', '顧客名', '車名', '登録番号', '発行日', '支払期限', '税率', '小計', '消費税', '合計', '明細', '備考', '明細詳細']
const maintenanceHeaders = ['書類ID', '書類番号', '書類種別', '入庫区分', 'ステータス', '顧客名', '車名', '登録番号', '入庫日', '出庫予定日', '支払期限', '税率', '小計', '消費税', '合計', '明細', '備考', '明細詳細']
const requiredDataFiles = ['customers.csv', 'vehicles.csv', 'sales.csv', 'maintenance.csv', 'document-links.json']
const optionalImageDataFile = 'image-attachments.json'
const maximumManifestBytes = 2 * 1024 * 1024
const maximumDataFileBytes = 64 * 1024 * 1024
const maximumImageBytes = 20 * 1024 * 1024
const maximumPackageBytes = 1024 * 1024 * 1024
const maximumCustomers = 10_000
const maximumVehicles = 10_000
const maximumDocuments = 20_000

export function isGraphFinalManifest(value: unknown): value is Pick<GraphFinalManifest, 'version' | 'kind' | 'status'> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false
  const manifest = value as { version?: unknown; kind?: unknown; status?: unknown }
  return manifest.version === 1 && manifest.kind === 'abacus-export-import-final-package' && manifest.status === 'registration-preview'
}

export async function validateGraphFinalPackage(selectedFiles: File[]): Promise<GraphFinalPackageValidation> {
  return validateGraphFinalFiles(collectFiles(selectedFiles))
}

async function validateGraphFinalFiles(files: PackageFileMap): Promise<GraphFinalPackageValidation> {
  const manifestFile = requireFile(files, 'manifest.json')
  if (manifestFile.size <= 0 || manifestFile.size > maximumManifestBytes) throw validationError('manifest.jsonのサイズが不正です。')
  const manifest = parseJson<GraphFinalManifest>(await readUtf8(manifestFile), 'manifest.json')
  validateManifestShape(manifest)

  const descriptors = manifest.dataFiles
  const imageDescriptors = manifest.imageFiles ?? []
  const descriptorPaths = new Set<string>()
  let totalBytes = manifestFile.size
  for (const descriptor of descriptors) {
    const path = await verifyDescriptor(descriptor, files, descriptorPaths)
    totalBytes += descriptor.sizeBytes
    if (!requiredDataFiles.includes(path) && path !== optionalImageDataFile) throw validationError(`登録前パッケージに許可されていないファイルがあります: ${path}`)
  }
  if (descriptors.length !== requiredDataFiles.length + (imageDescriptors.length > 0 ? 1 : 0) || requiredDataFiles.some((path) => !descriptorPaths.has(path)) || (imageDescriptors.length > 0) !== descriptorPaths.has(optionalImageDataFile)) {
    throw validationError('Gate 8Aの登録前パッケージには5つのデータファイルが必要です。')
  }
  const verifiedImageDescriptors = await verifyImageDescriptors(imageDescriptors, files, new Set<string>(), totalBytes)
  totalBytes += verifiedImageDescriptors.totalBytes
  if (files.size !== descriptors.length + verifiedImageDescriptors.descriptors.length + 1) throw validationError('登録前パッケージにマニフェストへ記載されていないファイルがあります。')
  if (totalBytes > maximumPackageBytes) throw validationError('パッケージの合計サイズが上限を超えています。')

  const customersFile = requireFile(files, 'customers.csv')
  const vehiclesFile = requireFile(files, 'vehicles.csv')
  const salesFile = requireFile(files, 'sales.csv')
  const maintenanceFile = requireFile(files, 'maintenance.csv')
  const linksFile = requireFile(files, 'document-links.json')
  const customerRows = parseRows(await readUtf8(customersFile), customerHeaders, 'customers.csv', maximumCustomers)
  const vehicleRows = parseRows(await readUtf8(vehiclesFile), vehicleHeaders, 'vehicles.csv', maximumVehicles)
  const salesRows = parseRows(await readUtf8(salesFile), salesHeaders, 'sales.csv', maximumDocuments)
  const maintenanceRows = parseRows(await readUtf8(maintenanceFile), maintenanceHeaders, 'maintenance.csv', maximumDocuments)
  const parsedLinks = parseDocumentLinks(parseJson<unknown>(await readUtf8(linksFile), 'document-links.json'))
  const documentLinks = parsedLinks.documents
  const imageAttachmentsFile = files.get(optionalImageDataFile)

  const customerIds = validateCustomers(customerRows)
  const vehicleIndex = validateVehicles(vehicleRows, customerIds)
  if (verifiedImageDescriptors.descriptors.length > 0 && !imageAttachmentsFile) throw validationError('画像対応表がありません。')
  const imageAttachments = verifiedImageDescriptors.descriptors.length > 0
    ? parseGraphFinalImageAttachments(
        parseJson<unknown>(await readUtf8(imageAttachmentsFile as File), optionalImageDataFile),
        verifiedImageDescriptors.byPath,
        customerIds,
        vehicleIndex.customerByVehicleId)
    : []
  const vehicleIds = vehicleIndex.ids
  for (const [index, row] of customerRows.entries()) {
    const expected = parseNonNegativeInteger(row[9])
    const actual = vehicleIndex.counts.get(row[0]) ?? 0
    if (expected === null || expected !== actual) throw validationError(`customers.csvの${index + 2}行目とvehicles.csvの車両台数が一致しません。`)
  }
  const csvDocuments = validateCsvDocuments(salesRows, maintenanceRows, customerIds, vehicleIds)
  validateDocumentLinks(documentLinks, csvDocuments, customerIds, vehicleIndex.customerByVehicleId)
  validateManifestGroups(manifest.groups, customerRows, customerIds)
  validateManifestDocuments(manifest.documents, documentLinks)
  if (!sameStringSet(parsedLinks.excludedDocumentKeys, manifest.excludedDocumentKeys)) throw validationError('manifest.jsonとdocument-links.jsonの除外書類一覧が一致しません。')
  validateSummary(manifest.summary, customerRows, vehicleRows, salesRows, maintenanceRows, documentLinks, manifest.excludedDocumentKeys)
  if (manifest.summary.imageCount !== undefined && manifest.summary.imageCount !== imageAttachments.length) throw validationError(`マニフェストの画像件数が一致しません: ${manifest.summary.imageCount} / ${imageAttachments.length}`)

  const excludedDocumentKeys = validateExcludedDocumentKeys(manifest.excludedDocumentKeys, documentLinks)
  return {
    format: 'graph-final',
    manifest,
    files,
    customerRows,
    vehicleRows,
    salesRows,
    maintenanceRows,
    documents: documentLinks,
    imageAttachments,
    excludedDocumentKeys,
    checkedFileCount: descriptors.length + verifiedImageDescriptors.descriptors.length + 1,
    checkedImageCount: verifiedImageDescriptors.descriptors.length,
    manifestSha256: await sha256(manifestFile),
  }
}

/**
 * Gate 17 の `ABACUS-Import-*` ルートを、既存のGraph Final登録形式へ
 * 正規化します。`work/`は読み取るだけで検証対象へ入れず、readyの
 * マニフェストに列挙されたファイルだけを登録前パッケージとして扱います。
 */
export async function validateAbacusImportPackage(selectedFiles: File[]): Promise<GraphFinalPackageValidation> {
  const rootFiles = collectFiles(selectedFiles)
  const rootManifestFile = rootFiles.get('abacus-import.json')
  if (!rootManifestFile) throw validationError('abacus-import.jsonがありません。ABACUS-Import-*ルートを選択してください。')
  if (rootManifestFile.size <= 0 || rootManifestFile.size > maximumManifestBytes) throw validationError('abacus-import.jsonのサイズが不正です。')
  const rootManifest = parseJson<AbacusImportRootManifest>(await readUtf8(rootManifestFile), 'abacus-import.json')
  if (rootManifest.version !== 1 || rootManifest.kind !== 'abacus-import' || rootManifest.status !== 'ready' || !rootManifest.packageId || rootManifest.readyPath !== 'ready' || rootManifest.readyManifest !== 'ready/manifest.json' || rootManifest.imageAcquisitionMethod !== 'fp5-vehicle-record') {
    throw validationError('完成状態のABACUS-Importパッケージではありません。')
  }

  const readyManifestPath = `${rootManifest.readyPath}/manifest.json`
  const readyManifestFile = rootFiles.get(readyManifestPath)
  if (!readyManifestFile) throw validationError('ready/manifest.jsonがありません。完成品が未作成の可能性があります。')
  if (readyManifestFile.size <= 0 || readyManifestFile.size > maximumManifestBytes) throw validationError('ready/manifest.jsonのサイズが不正です。')
  const readyManifest = parseJson<AbacusImportReadyManifest>(await readUtf8(readyManifestFile), 'ready/manifest.json')
  validateAbacusImportReadyManifest(readyManifest, rootManifest)

  const descriptors = readyManifest.files.map((descriptor) => ({ ...descriptor, path: normalizePackagePath(descriptor.path) }))
  const descriptorPaths = new Set<string>()
  const readyFiles = new Map<string, File>()
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
  let totalBytes = rootManifestFile.size + readyManifestFile.size
  for (const descriptor of descriptors) {
    if (!descriptor.path || (!descriptor.path.startsWith('images/') && !allowedReadyPaths.has(descriptor.path))) throw validationError(`readyマニフェストのファイルパスが不正です: ${descriptor.path || '(空欄)'}`)
    if (descriptor.path.startsWith('images/') && (descriptor.path.split('/').length !== 2 || !/\.(?:png|jpe?g)$/iu.test(descriptor.path))) throw validationError(`readyマニフェストの画像パスが不正です: ${descriptor.path}`)
    if (descriptorPaths.has(descriptor.path)) throw validationError(`readyマニフェストに同じファイルが重複しています: ${descriptor.path}`)
    descriptorPaths.add(descriptor.path)
    const file = rootFiles.get(`${rootManifest.readyPath}/${descriptor.path}`)
    if (!file) throw validationError(`readyに記載されたファイルがありません: ${descriptor.path}`)
    const maximumBytes = descriptor.path.startsWith('images/') ? maximumImageBytes : maximumDataFileBytes
    if (!Number.isSafeInteger(descriptor.sizeBytes) || descriptor.sizeBytes <= 0 || !/^[0-9a-f]{64}$/i.test(descriptor.sha256) || file.size !== descriptor.sizeBytes || file.size > maximumBytes) {
      throw validationError(`readyファイルのサイズまたは記述が一致しません: ${descriptor.path}`)
    }
    await verifySha256(file, descriptor.sha256, descriptor.path)
    readyFiles.set(descriptor.path, file)
    totalBytes += file.size
    if (totalBytes > maximumPackageBytes) throw validationError('ABACUS-Importパッケージの合計サイズが上限を超えています。')
  }
  const readyPrefix = `${rootManifest.readyPath}/`
  for (const path of rootFiles.keys()) {
    if (!path.startsWith(readyPrefix)) continue
    const relative = path.slice(readyPrefix.length)
    if (relative !== 'manifest.json' && !descriptorPaths.has(relative)) throw validationError(`readyマニフェストに記載されていないファイルがあります: ${relative}`)
  }

  const data = new Map<string, File>()
  const dataPaths = {
    'customers.csv': 'data/customers.csv',
    'vehicles.csv': 'data/vehicles.csv',
    'sales.csv': 'data/sales-documents.csv',
    'maintenance.csv': 'data/maintenance-documents.csv',
    'document-links.json': 'mappings/document-links.json',
  } as const
  for (const [target, source] of Object.entries(dataPaths) as Array<[keyof typeof dataPaths, string]>) {
    const file = readyFiles.get(source)
    if (!file) throw validationError(`readyに必要なファイルがありません: ${source}`)
    data.set(target, file)
  }
  const imageAttachmentsFile = readyFiles.get('mappings/image-attachments.json')
  if (imageAttachmentsFile) data.set('image-attachments.json', imageAttachmentsFile)
  const mergeFile = readyFiles.get('mappings/customer-merges.json')
  const excludedFile = readyFiles.get('reports/excluded-documents.json')
  if (!mergeFile || !excludedFile) throw validationError('readyに顧客統合または除外書類の証跡がありません。')
  const groups = parseJson<GraphFinalGroup[]>(await readUtf8(mergeFile), 'ready/mappings/customer-merges.json')
  const excludedDocumentKeys = parseJson<string[]>(await readUtf8(excludedFile), 'ready/reports/excluded-documents.json')
  if (!Array.isArray(groups) || !Array.isArray(excludedDocumentKeys)) throw validationError('readyの顧客統合または除外書類の形式が不正です。')
  const links = parseDocumentLinks(parseJson<unknown>(await readUtf8(data.get('document-links.json') as File), 'ready/mappings/document-links.json')).documents

  const graphManifest: GraphFinalManifest = {
    version: 1,
    kind: 'abacus-export-import-final-package',
    status: 'registration-preview',
    source: { candidatePackagePath: `ABACUS-Import/${rootManifest.packageId}`, candidateManifestSha256: await sha256(rootManifestFile) },
    summary: {
      customerRowCount: readyManifest.summary.customerCount,
      vehicleRowCount: readyManifest.summary.vehicleCount,
      salesRowCount: readyManifest.summary.salesDocumentCount,
      maintenanceRowCount: readyManifest.summary.maintenanceDocumentCount,
      vehiclelessDocumentCount: readyManifest.summary.vehiclelessDocumentCount,
      excludedDocumentCount: readyManifest.summary.excludedDocumentCount,
      imageCount: readyManifest.summary.imageCount,
    },
    dataFiles: [...data.entries()].map(([fileName, file]) => ({ fileName, sizeBytes: file.size, sha256: '' })),
    imageFiles: descriptors.filter((descriptor) => descriptor.path.startsWith('images/')).map((descriptor) => ({ fileName: descriptor.path, sizeBytes: descriptor.sizeBytes, sha256: descriptor.sha256 })),
    warnings: [],
    groups,
    documents: links.map((link) => ({ documentKey: link.documentKey, documentId: link.documentId, kind: link.documentKind, customerId: link.customerId, vehicleId: link.vehicleId, sourceLocation: link.sourceLocation, vehicleless: link.vehicleless })),
    excludedDocumentKeys,
  }
  // dataFilesのSHAは実ファイルから作り、合成manifest自体をAPI再検証可能にします。
  const graphFiles: PackageFileMap = new Map(data)
  for (const descriptor of graphManifest.dataFiles) {
    const file = graphFiles.get(descriptor.fileName)
    if (file) descriptor.sha256 = await sha256(file)
  }
  for (const descriptor of graphManifest.imageFiles ?? []) graphFiles.set(descriptor.fileName, readyFiles.get(descriptor.fileName) as File)
  const syntheticManifestFile = new File([JSON.stringify(graphManifest)], 'manifest.json', { type: 'application/json' })
  graphFiles.set('manifest.json', syntheticManifestFile)
  const validation = await validateGraphFinalFiles(graphFiles)
  validation.abacusImport = {
    rootManifestFile,
    readyManifestFile,
    rootManifestSha256: await sha256(rootManifestFile),
    readyManifestSha256: await sha256(readyManifestFile),
    readyFiles,
    readyDescriptors: descriptors,
  }
  return validation
}

function validateAbacusImportReadyManifest(manifest: AbacusImportReadyManifest, root: AbacusImportRootManifest) {
  if (manifest.version !== 1 || manifest.kind !== 'abacus-import-ready' || manifest.status !== 'ready' || manifest.packageId !== root.packageId || manifest.imageAcquisitionMethod !== 'fp5-vehicle-record' || !Array.isArray(manifest.files) || !manifest.summary) throw validationError('ready/manifest.jsonの形式または状態が不正です。')
  const summaryValues = Object.values(manifest.summary)
  if (summaryValues.some((value) => !Number.isSafeInteger(value) || value < 0)) throw validationError('ready/manifest.jsonの集計値が不正です。')
}

function validateManifestShape(manifest: GraphFinalManifest) {
  if (manifest.version !== 1 || manifest.kind !== 'abacus-export-import-final-package' || manifest.status !== 'registration-preview') throw validationError('Gate 8Aのregistration-previewパッケージではありません。')
  if (!manifest.summary || !Number.isSafeInteger(manifest.summary.customerRowCount) || !Number.isSafeInteger(manifest.summary.vehicleRowCount) || !Number.isSafeInteger(manifest.summary.salesRowCount) || !Number.isSafeInteger(manifest.summary.maintenanceRowCount) || !Number.isSafeInteger(manifest.summary.vehiclelessDocumentCount) || !Number.isSafeInteger(manifest.summary.excludedDocumentCount)) throw validationError('マニフェストの集計情報が不正です。')
  if (manifest.summary.imageCount !== undefined && (!Number.isSafeInteger(manifest.summary.imageCount) || manifest.summary.imageCount < 0)) throw validationError('マニフェストの画像件数が不正です。')
  if (!Array.isArray(manifest.dataFiles) || (manifest.imageFiles !== undefined && !Array.isArray(manifest.imageFiles)) || !Array.isArray(manifest.groups) || !Array.isArray(manifest.documents) || !Array.isArray(manifest.excludedDocumentKeys) || !Array.isArray(manifest.warnings)) throw validationError('マニフェストの配列項目が不正です。')
  if (manifest.groups.length === 0 || manifest.groups.length > maximumCustomers || manifest.documents.length > maximumDocuments || manifest.excludedDocumentKeys.length > maximumDocuments) throw validationError('マニフェストの件数が上限を超えています。')
  if ((manifest.imageFiles?.length ?? 0) > maximumDocuments) throw validationError('マニフェストの画像件数が上限を超えています。')
}

async function verifyDescriptor(descriptor: GraphFinalFileDescriptor, files: PackageFileMap, checkedPaths: Set<string>) {
  if (!descriptor || typeof descriptor !== 'object' || typeof descriptor.fileName !== 'string' || !Number.isSafeInteger(descriptor.sizeBytes) || descriptor.sizeBytes <= 0 || typeof descriptor.sha256 !== 'string' || !/^[0-9a-f]{64}$/i.test(descriptor.sha256)) throw validationError('マニフェストのファイル記述が不正です。')
  const path = normalizePackagePath(descriptor.fileName)
  if (!path || path.startsWith('images/') || checkedPaths.has(path)) throw validationError(`マニフェストのファイル記述が不正です: ${path || '(空欄)'}`)
  checkedPaths.add(path)
  const file = files.get(path)
  if (!file) throw validationError(`パッケージ内に記載ファイルがありません: ${path}`)
  if (file.size !== descriptor.sizeBytes || file.size > maximumDataFileBytes) throw validationError(`ファイルサイズが一致しません: ${path}`)
  return verifySha256(file, descriptor.sha256, path)
}

async function verifyImageDescriptors(descriptors: GraphFinalFileDescriptor[], files: PackageFileMap, checkedPaths: Set<string>, initialBytes: number) {
  let totalBytes = 0
  const byPath = new Map<string, GraphFinalFileDescriptor>()
  for (const descriptor of descriptors) {
    if (!descriptor || typeof descriptor !== 'object' || typeof descriptor.fileName !== 'string' || !Number.isSafeInteger(descriptor.sizeBytes) || descriptor.sizeBytes <= 0 || descriptor.sizeBytes > maximumImageBytes || typeof descriptor.sha256 !== 'string' || !/^[0-9a-f]{64}$/i.test(descriptor.sha256)) throw validationError('マニフェストの画像記述が不正です。')
    const path = normalizePackagePath(descriptor.fileName)
    if (!path.startsWith('images/') || path.split('/').length !== 2 || checkedPaths.has(path)) throw validationError(`マニフェストの画像記述が不正です: ${path || '(空欄)'}`)
    checkedPaths.add(path)
    const file = files.get(path)
    if (!file) throw validationError(`パッケージ内に記載画像がありません: ${path}`)
    if (file.size !== descriptor.sizeBytes || file.size > maximumImageBytes) throw validationError(`画像サイズが一致しません: ${path}`)
    await verifySha256(file, descriptor.sha256, path)
    totalBytes += file.size
    if (initialBytes + totalBytes > maximumPackageBytes) throw validationError('パッケージの合計サイズが上限を超えています。')
    byPath.set(path, { ...descriptor, fileName: path })
  }
  return { descriptors, byPath, totalBytes }
}

function parseGraphFinalImageAttachments(value: unknown, imageFiles: Map<string, GraphFinalFileDescriptor>, customerIds: Set<string>, customerByVehicleId: Map<string, string>) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw validationError('image-attachments.jsonの形式が不正です。')
  const document = value as Record<string, unknown>
  if (document.version !== 1 || document.kind !== 'abacus-web-import-image-attachments' || document.status !== 'manual-upload-required' || !Array.isArray(document.attachments)) throw validationError('image-attachments.jsonの種別または添付一覧が不正です。')
  if (document.attachments.length !== imageFiles.size || document.attachments.length > maximumDocuments) throw validationError('画像対応表とマニフェストの画像件数が一致しません。')
  const paths = new Set<string>()
  return document.attachments.map((item, index) => {
    if (!item || typeof item !== 'object' || Array.isArray(item)) throw validationError(`画像対応表の${index + 1}件目が不正です。`)
    const row = item as Record<string, unknown>
    const customerId = textValue(row.customerId)
    const vehicleId = textValue(row.vehicleId)
    const imagePath = normalizePackagePath(row.imagePath)
    const imageSha256 = textValue(row.imageSha256).toUpperCase()
    const contentType = textValue(row.contentType)
    if (!customerIds.has(customerId) || !customerByVehicleId.has(vehicleId) || customerByVehicleId.get(vehicleId) !== customerId || !imageFiles.has(imagePath) || imageFiles.get(imagePath)?.sha256.toUpperCase() !== imageSha256 || (contentType !== 'image/png' && contentType !== 'image/jpeg') || paths.has(imagePath)) throw validationError(`画像対応表の参照先が不正です: ${imagePath || '(空欄)'}`)
    paths.add(imagePath)
    return { customerId, vehicleId, imagePath, imageSha256, contentType } satisfies GraphFinalImageAttachment
  })
}

async function verifySha256(file: File, expected: string, path: string) {
  const actual = await sha256(file)
  if (actual !== expected.toUpperCase()) throw validationError(`SHA-256が一致しません: ${path}`)
  return path
}

function validateCustomers(rows: string[][]) {
  const ids = new Set<string>()
  for (const [index, row] of rows.entries()) {
    const id = textValue(row[0])
    const name = textValue(row[2])
    const vehicleCount = parseNonNegativeInteger(row[9])
    if (!id || !name || vehicleCount === null || ids.has(id)) throw validationError(`customers.csvの${index + 2}行目が不正です。`)
    ids.add(id)
  }
  return ids
}

function validateVehicles(rows: string[][], customerIds: Set<string>) {
  const ids = new Set<string>()
  const counts = new Map<string, number>()
  const customerByVehicleId = new Map<string, string>()
  for (const [index, row] of rows.entries()) {
    const id = textValue(row[0])
    const customerId = textValue(row[1])
    if (!id || !customerId || !customerIds.has(customerId) || ids.has(id)) throw validationError(`vehicles.csvの${index + 2}行目が不正です。`)
    ids.add(id)
    counts.set(customerId, (counts.get(customerId) ?? 0) + 1)
    customerByVehicleId.set(id, customerId)
  }
  return { ids, counts, customerByVehicleId }
}

function validateCsvDocuments(salesRows: string[][], maintenanceRows: string[][], customerIds: Set<string>, vehicleIds: Set<string>) {
  const documents = new Map<string, { kind: '販売書類' | '整備書類'; documentNumber: string; customerName: string; vehicleName: string; registrationNumber: string }>()
  for (const [index, row] of salesRows.entries()) {
    validateDocumentRow(row, index + 2, 'sales.csv', customerIds, documents, '販売書類', 4, 5, 6)
  }
  for (const [index, row] of maintenanceRows.entries()) {
    validateDocumentRow(row, index + 2, 'maintenance.csv', customerIds, documents, '整備書類', 5, 6, 7)
  }
  for (const item of documents.values()) {
    if (item.vehicleName || item.registrationNumber) {
      // 車両IDはCSVの備考に記録されるため、ここでは空欄の特例を妨げない。
      continue
    }
  }
  void vehicleIds
  return documents
}

function validateDocumentRow(row: string[], rowNumber: number, label: string, customerIds: Set<string>, documents: Map<string, { kind: '販売書類' | '整備書類'; documentNumber: string; customerName: string; vehicleName: string; registrationNumber: string }>, kind: '販売書類' | '整備書類', customerNameIndex: number, vehicleNameIndex: number, registrationIndex: number) {
  const id = textValue(row[0])
  const documentNumber = textValue(row[1])
  const customerName = textValue(row[customerNameIndex])
  const vehicleName = textValue(row[vehicleNameIndex])
  const registrationNumber = textValue(row[registrationIndex])
  if (!id || !documentNumber || !customerName || documents.has(id)) throw validationError(`${label}の${rowNumber}行目が不正です。`)
  // 顧客IDは書類CSVに出さない互換形式のため、顧客名の空欄だけを拒否する。
  if (!customerIds.size) throw validationError(`${label}を紐付ける顧客がありません。`)
  documents.set(id, { kind, documentNumber, customerName, vehicleName, registrationNumber })
}

function validateDocumentLinks(links: GraphFinalDocumentLink[], csvDocuments: Map<string, { kind: '販売書類' | '整備書類'; documentNumber: string; customerName: string; vehicleName: string; registrationNumber: string }>, customerIds: Set<string>, customerByVehicleId: Map<string, string>) {
  const keys = new Set<string>()
  const ids = new Set<string>()
  for (const [index, link] of links.entries()) {
    if (!link.documentKey || !link.documentId || keys.has(link.documentKey) || ids.has(link.documentId) || !customerIds.has(link.customerId) || !link.customerName || !link.sourceLocation || typeof link.vehicleless !== 'boolean') throw validationError(`document-links.jsonの${index + 1}件目が不正です。`)
    const csv = csvDocuments.get(link.documentId)
    if (!csv || csv.kind !== link.documentKind || csv.documentNumber !== link.documentNumber || csv.customerName !== link.customerName || csv.vehicleName !== textValue(link.vehicleName)) throw validationError(`document-links.jsonの${index + 1}件目がCSVと一致しません。`)
    const vehicleId = textValue(link.vehicleId)
    const vehicleless = link.vehicleless
    if (vehicleless && vehicleId) throw validationError(`車両なし書類にvehicleIdがあります: ${link.documentId}`)
    if (!vehicleless && !vehicleId) throw validationError(`車両あり書類にvehicleIdがありません: ${link.documentId}`)
    if (vehicleId && !customerByVehicleId.has(vehicleId)) throw validationError(`存在しない車両IDが参照されています: ${vehicleId}`)
    if (vehicleId && customerByVehicleId.get(vehicleId) !== link.customerId) throw validationError(`書類と車両の顧客IDが一致しません: ${link.documentId}`)
    if (vehicleless !== (!csv.vehicleName && !csv.registrationNumber)) throw validationError(`車両なし判定とCSVの車両欄が一致しません: ${link.documentId}`)
    keys.add(link.documentKey)
    ids.add(link.documentId)
  }
  if (ids.size !== csvDocuments.size) throw validationError(`書類対応表とCSVの件数が一致しません: 対応表${ids.size}件 / CSV${csvDocuments.size}件`)
}

function validateManifestGroups(groups: GraphFinalGroup[], customerRows: string[][], customerIds: Set<string>) {
  if (groups.length !== customerRows.length) throw validationError(`顧客グループとcustomers.csvの件数が一致しません: グループ${groups.length}件 / CSV${customerRows.length}行`)
  const groupIds = new Set<string>()
  const groupKeys = new Set<string>()
  for (const [index, group] of groups.entries()) {
    if (!group || typeof group !== 'object' || typeof group.groupKey !== 'string' || typeof group.origin !== 'string' || group.approved !== true || !Array.isArray(group.sourceCustomerIds) || group.sourceCustomerIds.length === 0 || group.sourceCustomerIds.some((id) => !textValue(id)) || !customerIds.has(group.customerId) || !group.customerName || groupIds.has(group.customerId) || groupKeys.has(group.groupKey)) throw validationError(`マニフェストの顧客グループ${index + 1}件目が不正です。`)
    groupIds.add(group.customerId)
    groupKeys.add(group.groupKey)
  }
  if (groupIds.size !== customerIds.size) throw validationError('マニフェストの顧客グループがcustomers.csvを網羅していません。')
}

function validateManifestDocuments(manifestDocuments: GraphFinalManifestDocument[], links: GraphFinalDocumentLink[]) {
  if (manifestDocuments.length !== links.length) throw validationError(`マニフェストとdocument-links.jsonの件数が一致しません: マニフェスト${manifestDocuments.length}件 / 対応表${links.length}件`)
  const linksById = new Map(links.map((link) => [link.documentId, link]))
  const ids = new Set<string>()
  for (const [index, item] of manifestDocuments.entries()) {
    if (!item || typeof item.documentId !== 'string' || typeof item.documentKey !== 'string' || typeof item.kind !== 'string' || typeof item.customerId !== 'string' || typeof item.sourceLocation !== 'string' || typeof item.vehicleless !== 'boolean' || ids.has(item.documentId)) throw validationError(`マニフェストの書類${index + 1}件目が不正です。`)
    const link = linksById.get(item.documentId)
    if (!link || link.documentKey !== item.documentKey || link.documentKind !== item.kind || link.customerId !== item.customerId || link.sourceLocation !== item.sourceLocation || link.vehicleless !== item.vehicleless || textValue(item.vehicleId) !== textValue(link.vehicleId)) throw validationError(`マニフェストの書類${index + 1}件目が対応表と一致しません。`)
    ids.add(item.documentId)
  }
}

function validateSummary(summary: GraphFinalManifest['summary'], customers: string[][], vehicles: string[][], sales: string[][], maintenance: string[][], links: GraphFinalDocumentLink[], excluded: string[]) {
  const expected = {
    customerRowCount: customers.length,
    vehicleRowCount: vehicles.length,
    salesRowCount: sales.length,
    maintenanceRowCount: maintenance.length,
    vehiclelessDocumentCount: links.filter((link) => link.vehicleless).length,
    excludedDocumentCount: excluded.length,
  }
  for (const [key, value] of Object.entries(expected) as Array<[keyof typeof expected, number]>) {
    if (summary[key] !== value) throw validationError(`マニフェストの集計が一致しません: ${key}=${summary[key]} / 実データ=${value}`)
  }
}

function validateExcludedDocumentKeys(keys: string[], links: GraphFinalDocumentLink[]) {
  const included = new Set(links.map((link) => link.documentKey))
  const seen = new Set<string>()
  for (const key of keys) {
    if (!textValue(key) || seen.has(key) || included.has(key)) throw validationError(`除外書類キーが不正です: ${key || '(空欄)'}`)
    seen.add(key)
  }
  return [...seen]
}

function parseDocumentLinks(value: unknown) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw validationError('document-links.jsonの形式が不正です。')
  const documentLinks = value as { version?: unknown; kind?: unknown; status?: unknown; documents?: unknown; excludedDocumentKeys?: unknown }
  if (documentLinks.version !== 1 || documentLinks.kind !== 'abacus-export-import-document-links' || documentLinks.status !== 'finalization-preview' || !Array.isArray(documentLinks.documents) || !Array.isArray(documentLinks.excludedDocumentKeys) || documentLinks.documents.length > maximumDocuments) throw validationError('document-links.jsonの形式または件数が不正です。')
  const excludedDocumentKeys = documentLinks.excludedDocumentKeys.map((item) => textValue(item))
  if (excludedDocumentKeys.some((key) => !key)) throw validationError('document-links.jsonの除外書類キーが不正です。')
  const documents = documentLinks.documents.map((item, index) => {
    if (!item || typeof item !== 'object' || Array.isArray(item)) throw validationError(`document-links.jsonの${index + 1}件目が不正です。`)
    const document = item as Record<string, unknown>
    const documentKind = textValue(document.documentKind)
    if (documentKind !== '販売書類' && documentKind !== '整備書類') throw validationError(`document-links.jsonの書類種別が不正です: ${index + 1}件目`)
    return {
      documentKey: textValue(document.documentKey),
      documentId: textValue(document.documentId),
      documentKind,
      documentNumber: textValue(document.documentNumber),
      customerId: textValue(document.customerId),
      customerName: textValue(document.customerName),
      vehicleId: textValue(document.vehicleId) || null,
      vehicleName: textValue(document.vehicleName) || null,
      vehicleless: document.vehicleless === true,
      sourceLocation: textValue(document.sourceLocation),
      warning: textValue(document.warning),
    } satisfies GraphFinalDocumentLink
  })
  return { documents, excludedDocumentKeys }
}

function parseRows(text: string, expectedHeaders: string[], label: string, maximumRows: number) {
  const rows = parseCsv(text, label)
  if (rows.length === 0 || rows[0].length !== expectedHeaders.length || rows[0].some((value, index) => value !== expectedHeaders[index])) throw validationError(`${label}の見出し行がGate 8A形式と一致しません。`)
  const dataRows = rows.slice(1).filter((row) => row.some((value) => value.trim()))
  if (dataRows.length > maximumRows) throw validationError(`${label}のデータ行数が上限を超えています。`)
  const invalidRow = dataRows.findIndex((row) => row.length !== expectedHeaders.length)
  if (invalidRow >= 0) throw validationError(`${label}の${invalidRow + 2}行目の列数が見出しと一致しません。`)
  return dataRows
}

function collectFiles(selectedFiles: File[]) {
  const files: PackageFileMap = new Map()
  for (const file of selectedFiles) {
    const path = normalizeSelectedPath(file)
    if (!path || files.has(path)) throw validationError(`同じ相対パスのファイルが複数あります: ${path}`)
    files.set(path, file)
  }
  return files
}

function normalizeSelectedPath(file: File) {
  const raw = ((file as File & { webkitRelativePath?: string }).webkitRelativePath || file.name).replaceAll('\\', '/').replace(/^\/+/, '')
  const parts = raw.split('/').filter(Boolean)
  return parts.length > 1 ? parts.slice(1).join('/') : raw
}

function normalizePackagePath(value: unknown) {
  if (typeof value !== 'string') return ''
  const path = value.trim().replaceAll('\\', '/').replace(/^\/+/, '')
  if (!path || path.includes('..') || path.includes(':') || path.split('/').some((part) => !part)) return ''
  return path
}

function requireFile(files: PackageFileMap, path: string) {
  const file = files.get(path)
  if (!file) throw validationError(`${path}がありません。`)
  return file
}

async function readUtf8(file: File) {
  try {
    return new TextDecoder('utf-8', { fatal: true }).decode(await file.arrayBuffer()).replace(/^\uFEFF/, '')
  } catch {
    throw validationError(`${file.name}をUTF-8として読み取れません。`)
  }
}

async function sha256(file: File) {
  const digest = await crypto.subtle.digest('SHA-256', await file.arrayBuffer())
  return Array.from(new Uint8Array(digest), (value) => value.toString(16).padStart(2, '0')).join('').toUpperCase()
}

function parseJson<T>(text: string, label: string) {
  try {
    return JSON.parse(text) as T
  } catch {
    throw validationError(`${label}のJSONが不正です。`)
  }
}

function parseCsv(text: string, label: string) {
  const rows: string[][] = []
  let row: string[] = []
  let field = ''
  let quoted = false
  for (let index = 0; index < text.length; index += 1) {
    const character = text[index]
    if (character === '"') {
      if (quoted && text[index + 1] === '"') {
        field += '"'
        index += 1
      } else {
        quoted = !quoted
      }
    } else if (character === ',' && !quoted) {
      row.push(field)
      field = ''
    } else if ((character === '\n' || character === '\r') && !quoted) {
      if (character === '\r' && text[index + 1] === '\n') index += 1
      row.push(field)
      if (row.some((value) => value.trim())) rows.push(row)
      row = []
      field = ''
    } else {
      field += character
    }
  }
  if (quoted) throw validationError(`${label}の引用符が閉じられていません。`)
  if (field || row.length) {
    row.push(field)
    if (row.some((value) => value.trim())) rows.push(row)
  }
  return rows
}

function parseNonNegativeInteger(value: string) {
  const normalized = textValue(value)
  if (!/^\d+$/.test(normalized)) return null
  const result = Number(normalized)
  return Number.isSafeInteger(result) ? result : null
}

function textValue(value: unknown) {
  return typeof value === 'string' ? value.trim() : ''
}

function sameStringSet(left: string[], right: string[]) {
  if (left.length !== right.length) return false
  const leftSet = new Set(left)
  const rightSet = new Set(right)
  return leftSet.size === rightSet.size && [...leftSet].every((value) => rightSet.has(value))
}

function validationError(message: string, details?: string[]): ValidationFailure {
  const error = new Error(message) as ValidationFailure
  error.details = details
  return error
}
