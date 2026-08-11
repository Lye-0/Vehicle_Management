import { useEffect, useRef, useState } from 'react'
import { AlertTriangle, CheckCircle2, FileUp, FolderOpen, ShieldCheck } from 'lucide-react'
import { commitAbacusRegistration, previewCsvImport, uploadAbacusRegistrationImage, type AbacusRegistrationCommitResult, type CsvImportPreview } from '../lib/importApi'

type PackageFileDescriptor = {
  relativePath: string
  sizeBytes: number
  sha256: string
}

type RegistrationManifest = {
  version: number
  kind: string
  status: string
  source?: {
    mappingPackagePath?: string
    mappingManifestSha256?: string
    sourcePackagePath?: string
    sourceManifestSha256?: string
  }
  summary?: {
    candidateCount?: number
    customerRowCount?: number
    vehicleRowCount?: number
    imageCount?: number
    mergedVehicleCount?: number
    note?: string
  }
  dataFiles?: PackageFileDescriptor[]
  imageFiles?: PackageFileDescriptor[]
  warnings?: string[]
  groups?: Array<{
    customerId: string
    customerNumber: string
    customerName: string
    customerGroupKey: string
    vehicleCount: number
  }>
}

type PackageFiles = Map<string, File>

type PackageValidation = {
  manifest: RegistrationManifest
  files: PackageFiles
  customerRows: string[][]
  vehicleRows: string[][]
  attachments: Array<Record<string, unknown>>
  checkedFileCount: number
  checkedImageCount: number
  manifestSha256: string
}

type ValidationFailure = Error & { details?: string[] }

const customerHeaders = ['顧客ID', '顧客番号', '顧客名', 'ふりがな', '電話番号', 'メールアドレス', '郵便番号', '住所', 'メモ', '車両台数']
const vehicleHeaders = ['車両ID', '顧客ID', '顧客名', 'メーカー', '車名', '型式', '登録番号', '車台番号', '年式', '車検満了日', '走行距離', '車体色', '排気量', 'ミッション', '記録簿', '備考']
const maximumManifestBytes = 1 * 1024 * 1024
const maximumCsvBytes = 5 * 1024 * 1024
const maximumImageBytes = 20 * 1024 * 1024
const maximumPackageBytes = 1024 * 1024 * 1024
const registrationConfirmationText = 'ABACUS登録を実行'

type RegistrationState = {
  commit: AbacusRegistrationCommitResult
  uploadedImageCount: number
  alreadyUploadedImageCount: number
  failedImages: string[]
}

export function AbacusRegistrationPackagePanel() {
  const inputRef = useRef<HTMLInputElement>(null)
  const [loading, setLoading] = useState(false)
  const [validation, setValidation] = useState<PackageValidation | null>(null)
  const [apiPreviews, setApiPreviews] = useState<{ customers: CsvImportPreview; vehicles: CsvImportPreview } | null>(null)
  const [errors, setErrors] = useState<string[]>([])
  const [message, setMessage] = useState('')
  const [confirmation, setConfirmation] = useState('')
  const [registering, setRegistering] = useState(false)
  const [registration, setRegistration] = useState<RegistrationState | null>(null)
  const [registrationError, setRegistrationError] = useState('')
  const [imageProgress, setImageProgress] = useState({ completed: 0, total: 0 })

  useEffect(() => {
    const input = inputRef.current as (HTMLInputElement & { webkitdirectory?: boolean }) | null
    if (input) input.webkitdirectory = true
  }, [])

  async function handleFiles(selectedFiles: FileList | null) {
    if (!selectedFiles || selectedFiles.length === 0) return
    setLoading(true)
    setValidation(null)
    setApiPreviews(null)
    setErrors([])
    setConfirmation('')
    setRegistration(null)
    setRegistrationError('')
    setImageProgress({ completed: 0, total: 0 })
    setMessage('フォルダー内のマニフェスト・CSV・画像を検証しています…')
    try {
      const nextValidation = await validatePackage(Array.from(selectedFiles))
      setValidation(nextValidation)
      setMessage('ローカル検証に合格しました。Web APIのプレビューを確認しています…')
      const [customers, vehicles] = await Promise.all([
        previewCsvImport('customers', nextValidation.files.get('customers.csv') as File),
        previewCsvImport('vehicles', nextValidation.files.get('vehicles.csv') as File),
      ])
      setApiPreviews({ customers, vehicles })
      setMessage('ローカル検証とWeb APIプレビューが完了しました。登録はまだ行っていません。')
    } catch (reason: unknown) {
      const failure = reason as ValidationFailure
      setErrors([failure.message || '登録前パッケージを確認できませんでした。', ...(failure.details ?? [])])
      setMessage('登録前パッケージを確認できませんでした。')
    } finally {
      setLoading(false)
    }
  }

  async function handleRegistration() {
    if (!validation || !apiPreviews || confirmation !== registrationConfirmationText || registering) return
    if (apiPreviews.customers.errors.length > 0 || apiPreviews.vehicles.errors.length > 0) {
      setRegistrationError('Web APIプレビューに入力エラーがあるため、登録できません。')
      return
    }
    setRegistering(true)
    setRegistrationError('')
    setRegistration(null)
    const customerFile = validation.files.get('customers.csv')
    const vehicleFile = validation.files.get('vehicles.csv')
    const manifestFile = validation.files.get('manifest.json')
    const attachmentsFile = validation.files.get('image-attachments.json')
    if (!customerFile || !vehicleFile || !manifestFile || !attachmentsFile) {
      setRegistrationError('登録に必要なパッケージファイルが見つかりません。')
      setRegistering(false)
      return
    }
    try {
      const formData = new FormData()
      formData.append('manifest', manifestFile, 'manifest.json')
      formData.append('customers', customerFile, 'customers.csv')
      formData.append('vehicles', vehicleFile, 'vehicles.csv')
      formData.append('attachments', attachmentsFile, 'image-attachments.json')
      formData.append('manifestSha256', validation.manifestSha256)
      formData.append('confirmation', confirmation)
      const commit = await commitAbacusRegistration(formData)
      const imageAttachments = validation.attachments
      setImageProgress({ completed: 0, total: imageAttachments.length })
      let uploadedImageCount = 0
      let alreadyUploadedImageCount = 0
      const failedImages: string[] = []
      for (const attachment of imageAttachments) {
        const imagePath = textValue(attachment.imagePath)
        const file = validation.files.get(imagePath)
        if (!file) {
          failedImages.push(imagePath || '(画像パス不明)')
          setImageProgress((current) => ({ ...current, completed: current.completed + 1 }))
          continue
        }
        try {
          const result = await uploadAbacusRegistrationImage({
            vehicleId: textValue(attachment.vehicleId),
            customerId: textValue(attachment.customerId),
            imagePath,
            imageSha256: textValue(attachment.imageSha256),
            manifestSha256: validation.manifestSha256,
            file,
          })
          if (result.status === 'already-uploaded') alreadyUploadedImageCount += 1
          else uploadedImageCount += 1
        } catch {
          failedImages.push(imagePath || '(画像パス不明)')
        } finally {
          setImageProgress((current) => ({ ...current, completed: current.completed + 1 }))
        }
      }
      setRegistration({ commit, uploadedImageCount, alreadyUploadedImageCount, failedImages })
      setMessage(failedImages.length > 0
        ? `顧客・車両は登録済みです。画像は${imageAttachments.length - failedImages.length}/${imageAttachments.length}件まで処理しました。失敗分は同じ確認操作で再試行できます。`
        : '顧客・車両の登録と画像のアップロードが完了しました。')
    } catch (reason: unknown) {
      setRegistrationError(reason instanceof Error ? reason.message : 'ABACUSデータを登録できませんでした。')
      setMessage('ABACUSデータを登録できませんでした。')
    } finally {
      setRegistering(false)
    }
  }

  const manifest = validation?.manifest
  const groups = manifest?.groups ?? []
  const hasErrors = errors.length > 0 || Boolean(registrationError) || Boolean(registration?.failedImages.length)
  return <section className="panel settings-panel abacus-package-panel">
    <div className="settings-section-heading"><FileUp size={18} /><div><h2>ABACUS登録前パッケージをプレビュー</h2><p>ローカル補助ソフトが作成したパッケージを選択し、ファイル・紐付け・Web APIのプレビューを確認します。登録は最終確認後にだけ実行できます。</p></div></div>
    <div className="abacus-package-controls">
      <button className="button button-secondary" type="button" disabled={loading || registering} onClick={() => inputRef.current?.click()}><FolderOpen size={16} />{loading ? '検証中…' : registering ? '登録中…' : '登録前パッケージのフォルダーを選択'}</button>
      <input ref={inputRef} className="abacus-package-hidden-input" type="file" multiple onChange={(event) => { void handleFiles(event.target.files); event.currentTarget.value = '' }} />
      <span className="abacus-package-hint">フォルダー選択後、manifest.json・CSV・画像をまとめて読み取ります。</span>
    </div>
    {message && <div className={`abacus-package-message${hasErrors ? ' is-error' : validation ? ' is-success' : ''}`} role="status">{hasErrors ? <AlertTriangle size={16} /> : validation && !loading ? <CheckCircle2 size={16} /> : <ShieldCheck size={16} />}<span>{message}</span></div>}
    {errors.length > 0 && <ul className="abacus-package-errors" role="alert">{errors.map((error, index) => <li key={`${index}-${error}`}>{error}</li>)}</ul>}
    {registrationError && <ul className="abacus-package-errors" role="alert"><li>{registrationError}</li></ul>}
    {manifest && validation && <>
      <div className="abacus-package-summary">
        <span>候補 {formatCount(manifest.summary?.candidateCount ?? validation.vehicleRows.length)}件</span>
        <span>顧客 {formatCount(manifest.summary?.customerRowCount ?? validation.customerRows.length)}行</span>
        <span>車両 {formatCount(manifest.summary?.vehicleRowCount ?? validation.vehicleRows.length)}行</span>
        <span>画像 {formatCount(manifest.summary?.imageCount ?? validation.checkedImageCount)}件</span>
        <span>検証ファイル {formatCount(validation.checkedFileCount)}件</span>
      </div>
      <div className="abacus-package-meta">
        <span><strong>形式</strong> {manifest.kind}</span>
        <span><strong>状態</strong> {manifest.status}</span>
        <span><strong>複数車両として統合</strong> {formatCount(manifest.summary?.mergedVehicleCount ?? 0)}台</span>
      </div>
      {groups.length > 0 && <div className="abacus-package-groups"><h3>顧客グループ（先頭20件）</h3><div className="abacus-package-table"><div className="abacus-package-row is-head"><span>顧客名</span><span>顧客ID</span><span>車両台数</span></div>{groups.slice(0, 20).map((group) => <div className="abacus-package-row" key={group.customerId}><span>{group.customerName || '未設定'}</span><span title={group.customerId}>{group.customerId}</span><span>{group.vehicleCount}</span></div>)}</div></div>}
      {apiPreviews && <div className="abacus-package-api-preview"><h3>Web APIプレビュー（登録前）</h3><div className="abacus-package-api-grid"><ApiPreviewSummary label="顧客CSV" preview={apiPreviews.customers} /><ApiPreviewSummary label="車両CSV" preview={apiPreviews.vehicles} /></div></div>}
      {manifest.warnings && manifest.warnings.length > 0 && <ul className="abacus-package-warnings">{manifest.warnings.map((warning, index) => <li key={`${index}-${warning}`}>{warning}</li>)}</ul>}
      {apiPreviews && <div className="abacus-registration-action"><h3>最終確認と登録</h3><p>顧客・車両をD1へ登録し、対応表の画像を対象車両へアップロードします。既存IDの内容が変わっている場合は安全のため登録を中止します。</p><label><span>確認文字列</span><input type="text" value={confirmation} onChange={(event) => setConfirmation(event.target.value)} placeholder={registrationConfirmationText} disabled={registering} /></label><button className="button button-primary" type="button" disabled={registering || apiPreviews.customers.errors.length > 0 || apiPreviews.vehicles.errors.length > 0 || confirmation !== registrationConfirmationText} onClick={() => void handleRegistration()}>{registering ? imageProgress.total > 0 ? `登録中…画像 ${imageProgress.completed}/${imageProgress.total}` : '登録中…' : '最終確認して登録を実行'}</button><small>実行には確認文字列「{registrationConfirmationText}」の入力が必要です。登録後も同じパッケージで画像の失敗分を再試行できます。</small></div>}
      {registration && <div className={`abacus-registration-result${registration.failedImages.length > 0 ? ' is-warning' : ''}`}><strong>{registration.failedImages.length > 0 ? '顧客・車両登録済み（一部画像要再試行）' : 'ABACUS登録が完了しました'}</strong><span>顧客: 新規{registration.commit.customers.imported}件 / 更新{registration.commit.customers.updated}件</span><span>車両: 新規{registration.commit.vehicles.imported}件 / 更新{registration.commit.vehicles.updated}件</span><span>画像: 新規{registration.uploadedImageCount}件 / 既存{registration.alreadyUploadedImageCount}件 / 失敗{registration.failedImages.length}件</span>{registration.failedImages.length > 0 && <small>{registration.failedImages.slice(0, 5).join('、')}</small>}</div>}
      <div className="abacus-package-no-commit"><ShieldCheck size={16} /><span>{registration ? 'このパッケージの顧客・車両登録と画像処理の結果を表示しています。' : '登録ボタンを押すまでは顧客・車両の登録、画像アップロード、Web APIのcommitは行いません。'}</span></div>
    </>}
  </section>
}

function ApiPreviewSummary({ label, preview }: { label: string; preview: CsvImportPreview }) {
  return <div className="abacus-package-api-card"><strong>{label}</strong><span>全 {preview.totalRows}行</span><span className={preview.errors.length ? 'is-warning' : 'is-success'}>{preview.errors.length ? `要確認 ${preview.errors.length}件` : '入力エラーなし'}</span>{preview.errors.slice(0, 3).map((error) => <small key={`${error.row}-${error.message}`}>{error.row}行目: {error.message}</small>)}</div>
}

async function validatePackage(selectedFiles: File[]): Promise<PackageValidation> {
  const files = collectFiles(selectedFiles)
  const manifestFile = files.get('manifest.json')
  if (!manifestFile) throw validationError('manifest.jsonがありません。登録前パッケージのフォルダーを選択してください。')
  if (manifestFile.size <= 0 || manifestFile.size > maximumManifestBytes) throw validationError('manifest.jsonのサイズが不正です。')
  const manifest = parseJson<RegistrationManifest>(await readUtf8(manifestFile), 'manifest.json')
  if (manifest.version !== 1 || manifest.kind !== 'abacus-web-import-registration-package' || manifest.status !== 'registration-preview') {
    throw validationError('Gate5Mのregistration-previewパッケージではありません。')
  }
  if (!Array.isArray(manifest.dataFiles) || !Array.isArray(manifest.imageFiles) || !Array.isArray(manifest.groups)) {
    throw validationError('マニフェストのファイル一覧または顧客グループがありません。')
  }
  const descriptors = [...manifest.dataFiles, ...manifest.imageFiles]
  if (descriptors.length === 0 || descriptors.length > 5_000) throw validationError('マニフェストのファイル件数が不正です。')
  const checkedPaths = new Set<string>()
  const imageDescriptorPaths = new Set<string>()
  let totalBytes = 0
  for (const descriptor of manifest.dataFiles) {
    await verifyDescriptor(descriptor, files, checkedPaths, totalBytes, false)
    totalBytes += descriptor.sizeBytes
  }
  for (const descriptor of manifest.imageFiles) {
    const path = await verifyDescriptor(descriptor, files, checkedPaths, totalBytes, true)
    imageDescriptorPaths.add(path)
    totalBytes += descriptor.sizeBytes
  }
  if (totalBytes > maximumPackageBytes) throw validationError('パッケージの合計サイズが上限を超えています。')
  const customersFile = requireFile(files, 'customers.csv')
  const vehiclesFile = requireFile(files, 'vehicles.csv')
  const attachmentsFile = requireFile(files, 'image-attachments.json')
  const customerRows = parseDataRows(await readUtf8(customersFile), customerHeaders, 'customers.csv')
  const vehicleRows = parseDataRows(await readUtf8(vehiclesFile), vehicleHeaders, 'vehicles.csv')
  const attachmentsJson = parseJson<unknown>(await readUtf8(attachmentsFile), 'image-attachments.json')
  const attachments = readAttachments(attachmentsJson)
  if (attachments.length !== manifest.imageFiles.length) {
    throw validationError(`画像対応表の件数が一致しません: マニフェスト${manifest.imageFiles.length}件 / 対応表${attachments.length}件`)
  }
  const customerIds = new Set(customerRows.map((row) => row[0]))
  const vehicleIds = new Set<string>()
  const groupIds = new Set<string>()
  const groupKeys = new Set<string>()
  let groupVehicleCount = 0
  for (const group of manifest.groups) {
    const customerId = textValue(group.customerId)
    const customerGroupKey = textValue(group.customerGroupKey)
    const customerName = textValue(group.customerName)
    if (!customerId || !customerGroupKey || !customerName || !Number.isSafeInteger(group.vehicleCount) || group.vehicleCount <= 0 || !customerIds.has(customerId) || groupIds.has(customerId) || groupKeys.has(customerGroupKey)) {
      throw validationError(`顧客グループの記述が不正です: ${customerId || '(空欄)'}`)
    }
    groupIds.add(customerId)
    groupKeys.add(customerGroupKey)
    groupVehicleCount += group.vehicleCount
  }
  if (groupIds.size !== customerRows.length || groupVehicleCount !== vehicleRows.length) {
    throw validationError(`顧客グループとCSV行数が一致しません: グループ${groupIds.size}件・${groupVehicleCount}台 / CSV${customerRows.length}行・${vehicleRows.length}台`)
  }
  for (const row of vehicleRows) {
    if (vehicleIds.has(row[0]) || !customerIds.has(row[1]) || !groupIds.has(row[1])) {
      throw validationError(`車両CSVの顧客IDまたは車両IDが不正です: ${row[0] || '(空欄)'}`)
    }
    vehicleIds.add(row[0])
  }
  const attachedImagePaths = new Set<string>()
  for (const attachment of attachments) {
    const customerId = textValue(attachment.customerId)
    const vehicleId = textValue(attachment.vehicleId)
    const imagePath = normalizePackagePath(attachment.imagePath)
    if (!customerIds.has(customerId) || !vehicleIds.has(vehicleId) || !imagePath.startsWith('images/') || !imageDescriptorPaths.has(imagePath) || attachedImagePaths.has(imagePath)) {
      throw validationError(`画像対応表の参照先が不正です: ${imagePath || '(空欄)'}`)
    }
    attachedImagePaths.add(imagePath)
  }
  if (attachedImagePaths.size !== imageDescriptorPaths.size) {
    throw validationError(`画像対応表で参照されていない画像があります: マニフェスト${imageDescriptorPaths.size}件 / 対応表${attachedImagePaths.size}件`)
  }
  const expected = manifest.summary
  const details: string[] = []
  if (expected?.candidateCount !== undefined && expected.candidateCount !== vehicleRows.length) details.push(`候補数: マニフェスト${expected.candidateCount} / 実車両行${vehicleRows.length}`)
  if (expected?.customerRowCount !== undefined && expected.customerRowCount !== customerRows.length) details.push(`顧客行数: マニフェスト${expected.customerRowCount} / 実ファイル${customerRows.length}`)
  if (expected?.vehicleRowCount !== undefined && expected.vehicleRowCount !== vehicleRows.length) details.push(`車両行数: マニフェスト${expected.vehicleRowCount} / 実ファイル${vehicleRows.length}`)
  if (expected?.imageCount !== undefined && expected.imageCount !== manifest.imageFiles.length) details.push(`画像数: マニフェスト${expected.imageCount} / 実ファイル${manifest.imageFiles.length}`)
  if (details.length > 0) throw validationError('マニフェストと実ファイルの集計が一致しません。', details)
  return { manifest, files, customerRows, vehicleRows, attachments, checkedFileCount: descriptors.length, checkedImageCount: manifest.imageFiles.length, manifestSha256: await sha256(manifestFile) }
}

async function verifyDescriptor(descriptor: PackageFileDescriptor, files: PackageFiles, checkedPaths: Set<string>, totalBytes: number, isImage: boolean) {
  if (!descriptor || typeof descriptor !== 'object' || typeof descriptor.relativePath !== 'string' || !Number.isSafeInteger(descriptor.sizeBytes) || descriptor.sizeBytes <= 0 || typeof descriptor.sha256 !== 'string' || !/^[0-9a-f]{64}$/i.test(descriptor.sha256)) {
    throw validationError('マニフェストのファイル記述が不正です。')
  }
  const path = normalizePackagePath(descriptor.relativePath)
  if (!path || (isImage ? !path.startsWith('images/') : path.startsWith('images/')) || checkedPaths.has(path)) {
    throw validationError(`マニフェストのファイル記述が不正です: ${path || '(空欄)'}`)
  }
  checkedPaths.add(path)
  const file = files.get(path)
  if (!file) throw validationError(`パッケージ内に記載ファイルがありません: ${path}`)
  const maximumBytes = isImage ? maximumImageBytes : maximumCsvBytes
  if (file.size !== descriptor.sizeBytes || file.size > maximumBytes) throw validationError(`ファイルサイズが一致しません: ${path}`)
  const actualSha256 = await sha256(file)
  if (actualSha256 !== descriptor.sha256.toUpperCase()) throw validationError(`SHA-256が一致しません: ${path}`)
  if (totalBytes + file.size > maximumPackageBytes) throw validationError('パッケージの合計サイズが上限を超えています。')
  return path
}

function collectFiles(selectedFiles: File[]) {
  const files: PackageFiles = new Map()
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

function requireFile(files: PackageFiles, path: string) {
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

function parseDataRows(text: string, expectedHeaders: string[], label: string) {
  const rows = parseCsv(text, label)
  if (rows.length < 2 || rows[0].length !== expectedHeaders.length || rows[0].some((value, index) => value !== expectedHeaders[index])) {
    throw validationError(`${label}の見出し行が既存Webインポート形式と一致しません。`)
  }
  const dataRows = rows.slice(1).filter((row) => row.some((value) => value.trim()))
  if (dataRows.length === 0 || dataRows.length > 5_000) throw validationError(`${label}のデータ行数が不正です。`)
  const invalidRow = dataRows.findIndex((row) => row.length !== expectedHeaders.length)
  if (invalidRow >= 0) throw validationError(`${label}の${invalidRow + 2}行目の列数が見出しと一致しません。`)
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

function readAttachments(value: unknown) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw validationError('image-attachments.jsonの形式が不正です。')
  const attachments = (value as { attachments?: unknown }).attachments
  if (!Array.isArray(attachments) || attachments.length === 0 || attachments.length > 5_000 || attachments.some((item) => !item || typeof item !== 'object' || Array.isArray(item))) {
    throw validationError('image-attachments.jsonの添付一覧が不正です。')
  }
  return attachments as Array<Record<string, unknown>>
}

function textValue(value: unknown) {
  return typeof value === 'string' ? value.trim() : ''
}

function validationError(message: string, details?: string[]): ValidationFailure {
  const error = new Error(message) as ValidationFailure
  error.details = details
  return error
}

function formatCount(value: number) {
  return new Intl.NumberFormat('ja-JP').format(value)
}
