import { apiFetch } from './api'
import type { AbacusDocumentImportMetadata } from './abacusDocumentMetadata'
import type { AbacusDetailLine, AbacusDetailReport, AbacusDocumentAmounts } from './abacusDetail'
import { hasOwnField } from './documentCustomerField'

export type MaintenanceDocumentType = '整備見積書' | '整備請求書'
export type MaintenanceStatus = '下書き' | '入金待ち' | '完了' | 'アーカイブ済み'
export type IntakeCategory = '車検' | '板金' | '一般整備'
export type MaintenanceItemKind = '作業' | '部品'
export type MandatoryFees = { 自賠責: number; 重量税: number; 印紙代: number; リサイクル料金: number }
export type MaintenanceFeeKey = keyof MandatoryFees | '調整額'
export type MaintenanceCustomerDetails = { name: string; kana: string; phone: string; email?: string; postalCode: string; address: string; birthDate: string; employer: string }
export type MaintenanceVehicleDetails = { maker: string; name: string; modelType: string; plate: string; vin: string; year: string; inspectionDate: string; mileage: string; color: string; displacement: string; transmission: string; inspectionRecordAvailable: boolean }
export type MaintenanceDocumentDetails = {
  staffName: string
  customerHonorific: string
  customerBirthDate: string
  customerEmployer: string
  customerContactPhone: string
  bankName: string
  bankAccount: string
  bankAccountHolder: string
  customerOverride: MaintenanceCustomerDetails | null
  vehicleOverride: MaintenanceVehicleDetails | null
  labels: {
    documentTitle: string
    amountTitle: string
    workSectionTitle: string
    bankTitle: string
    otherFee: string
  }
}
export type MaintenanceLineItem = { id: string; kind: MaintenanceItemKind; description: string; quantity: number | null; unit: string; unitPrice: number | null; technicalFee: number | null; summary: string; sourceRowIndex?: number; abacusDetail?: AbacusDetailLine | null; isAbacusMigration?: boolean }

export const defaultMaintenanceDocumentDetails: MaintenanceDocumentDetails = {
  staffName: '',
  customerHonorific: '様',
  customerBirthDate: '',
  customerEmployer: '',
  customerContactPhone: '',
  bankName: '',
  bankAccount: '',
  bankAccountHolder: '',
  customerOverride: null,
  vehicleOverride: null,
  labels: {
    documentTitle: '',
    amountTitle: 'お見積金額（税込）',
    workSectionTitle: '作業内容／部品名等',
    bankTitle: 'お振込先',
    otherFee: 'その他',
  },
}

export type MaintenanceDocument = {
  id: string
  updatedAt: string
  number: string
  type: MaintenanceDocumentType
  status: MaintenanceStatus
  category: IntakeCategory
  customerId: string
  customerName: string
  phone: string
  customerDetails: MaintenanceCustomerDetails
  vehicleId: string | null
  vehicle: string
  plate: string
  /** ABACUSグラフ登録が付与する互換表示用メタデータ。通常書類では未設定です。 */
  abacusImport?: AbacusDocumentImportMetadata | null
  isAbacusMigration?: boolean
  abacusDetailReport?: AbacusDetailReport | null
  abacusAmounts?: AbacusDocumentAmounts | null
  mileage: string
  vehicleDetails: MaintenanceVehicleDetails | null
  details: MaintenanceDocumentDetails
  intakeDate: string
  plannedReleaseDate: string
  completionDate: string
  issuedAt: string
  dueDate: string
  taxRate: number
  taxRounding: '切り捨て' | '四捨五入'
  fees: MandatoryFees
  adjustment: number
  note: string
  archivedAt: string | null
  archivedPreviousStatus: MaintenanceStatus | null
  archivedBy: string | null
  purgeAt: string | null
  keepForever: boolean
  /** 一覧の要約レスポンスに含まれる保存済み合計金額。詳細書類では計算値を優先します。 */
  total?: number
  items: MaintenanceLineItem[]
  isSummary?: boolean
}

export type MaintenanceDocumentSummary = Pick<MaintenanceDocument, 'id' | 'updatedAt' | 'number' | 'type' | 'status' | 'category' | 'customerId' | 'customerName' | 'phone' | 'vehicleId' | 'vehicle' | 'plate' | 'intakeDate' | 'issuedAt' | 'archivedAt' | 'archivedPreviousStatus' | 'archivedBy' | 'purgeAt' | 'keepForever'> & {
  total: number
  abacusImport?: AbacusDocumentImportMetadata | null
  isAbacusMigration?: boolean
}

/** Persisted fields plus the optional identifiers used by an unsaved document draft. */
export type MaintenanceDocumentLike = Omit<MaintenanceDocument, 'id' | 'customerId' | 'vehicleId'> & {
  id?: string
  customerId: string | null
  vehicleId: string | null
}

export type MaintenanceDocumentInput = {
  number?: string
  type: MaintenanceDocumentType
  status: MaintenanceStatus
  category: IntakeCategory
  customerId?: string
  vehicleId?: string
  issuedAt?: string
  intakeDate: string
  plannedReleaseDate: string
  completionDate: string
  dueDate: string
  taxRate: number
  taxRounding: '切り捨て' | '四捨五入'
  fees: MandatoryFees
  adjustment: number
  note: string
  details: MaintenanceDocumentDetails
  items: Array<Omit<MaintenanceLineItem, 'id'>>
  expectedUpdatedAt?: string
  mileageSync?: {
    confirmed: true
    openedMileage: number | null
    inputMileage: number
  }
  masterSync?: {
    confirmed: true
    customerFields: string[]
    vehicleFields: string[]
    expectedCustomerUpdatedAt?: string
    expectedVehicleUpdatedAt?: string
  }
  newCustomer?: {
    name: string
    nameKana?: string
    phone?: string
    email?: string
    postalCode?: string
    address?: string
    birthDate?: string
    employer?: string
  }
  newVehicle?: {
    maker: string
    name: string
    model?: string
    registrationNumber?: string
    chassisNumber?: string
    modelYear?: number
    inspectionDate?: string
    mileage?: number
    bodyColor?: string
    displacement?: number
    transmission?: string
  }
  duplicateConfirmation?: {
    registrationNumberConfirmed?: boolean
    confirmedVehicleId?: string
  }
}

type ApiMaintenanceDocument = Omit<MaintenanceDocument, 'type' | 'status' | 'category' | 'taxRate' | 'intakeDate' | 'plannedReleaseDate' | 'completionDate' | 'issuedAt' | 'dueDate' | 'details' | 'items'> & { type: MaintenanceDocumentType | '納品書'; status: MaintenanceStatus | '受付中' | '作業中'; category: IntakeCategory | '法定点検'; taxRate: number; intakeDate: string | null; plannedReleaseDate: string | null; completionDate: string | null; issuedAt: string; dueDate: string | null; details?: MaintenanceDocumentDetails | null; items?: MaintenanceLineItem[]; summary?: boolean }

export async function fetchMaintenanceDocuments() {
  const response = await apiFetch<{ documents: ApiMaintenanceDocument[] }>('/api/maintenance-documents')
  return response.documents.map(mapMaintenanceDocument)
}

export async function fetchMaintenanceDocumentSummaries(options: { q?: string; type?: string; category?: string; status?: string; cursor?: string | null; limit?: number; includeArchived?: boolean; sortKey?: string; sortDirection?: string } = {}) {
  const params = new URLSearchParams({ view: 'summary', limit: String(options.limit ?? 50) })
  if (options.q?.trim()) params.set('q', options.q.trim())
  if (options.type && options.type !== 'すべて') params.set('type', options.type)
  if (options.category && options.category !== 'すべて') params.set('category', options.category)
  if (options.status && options.status !== 'すべて') params.set('status', options.status)
  if (options.cursor) params.set('cursor', options.cursor)
  if (options.includeArchived) params.set('includeArchived', 'true')
  if (options.sortKey) params.set('sortKey', options.sortKey)
  if (options.sortDirection) params.set('sortDirection', options.sortDirection)
  const response = await apiFetch<{ documents: ApiMaintenanceDocument[]; nextCursor: string | null; hasMore: boolean }>(`/api/maintenance-documents?${params.toString()}`)
  return { documents: response.documents.map(mapMaintenanceDocument), nextCursor: response.nextCursor, hasMore: response.hasMore }
}

export async function fetchMaintenanceDocument(id: string) {
  const response = await apiFetch<{ document: ApiMaintenanceDocument }>(`/api/maintenance-documents/${encodeURIComponent(id)}`)
  return mapMaintenanceDocument(response.document)
}

export async function createMaintenanceDocument(input: MaintenanceDocumentInput) {
  const response = await apiFetch<{ document: ApiMaintenanceDocument }>('/api/maintenance-documents', { method: 'POST', body: JSON.stringify(toPayload(input)) })
  return mapMaintenanceDocument(response.document)
}

export async function updateMaintenanceDocument(id: string, input: MaintenanceDocumentInput) {
  const response = await apiFetch<{ document: ApiMaintenanceDocument }>(`/api/maintenance-documents/${id}`, { method: 'PATCH', body: JSON.stringify(toPayload(input)) })
  return mapMaintenanceDocument(response.document)
}

export async function archiveMaintenanceDocument(id: string) {
  await apiFetch(`/api/maintenance-documents/${id}`, { method: 'DELETE' })
}

export async function restoreMaintenanceDocument(id: string) {
  await apiFetch(`/api/maintenance-documents/${id}/restore`, { method: 'POST' })
}

function mapMaintenanceDocument(document: ApiMaintenanceDocument): MaintenanceDocument {
  return {
    ...document,
    isSummary: document.summary === true,
    type: document.type === '納品書' ? '整備請求書' : document.type,
    status: normalizeMaintenanceStatus(document.status),
    category: document.category === '法定点検' ? '板金' : document.category,
    customerDetails: document.customerDetails ?? { name: document.customerName, kana: '', phone: document.phone, email: '', postalCode: '', address: '', birthDate: '', employer: '' },
    vehicleDetails: document.vehicleDetails ?? null,
    details: normalizeMaintenanceDetails(document.details),
    intakeDate: formatDate(document.intakeDate),
    plannedReleaseDate: formatDate(document.plannedReleaseDate),
    completionDate: formatDate(document.completionDate),
    issuedAt: formatDate(document.issuedAt),
    dueDate: formatDate(document.dueDate),
    taxRate: document.taxRate / 100,
    taxRounding: document.taxRounding === '四捨五入' ? '四捨五入' : '切り捨て',
    note: document.note ?? '',
    items: (document.items ?? []).map(normalizeMaintenanceLineItem),
  }
}

function normalizeMaintenanceLineItem(item: MaintenanceLineItem): MaintenanceLineItem {
  const quantity = Number(item.quantity)
  const unitPrice = Number(item.unitPrice)
  const technicalFee = Number(item.technicalFee ?? 0)
  const unit = item.unit ?? ''
  const hasBlankUnit = unit === ''
  return {
    ...item,
    quantity: hasBlankUnit && quantity === 0 ? null : quantity,
    unit,
    unitPrice: hasBlankUnit && unitPrice === 0 ? null : unitPrice,
    technicalFee: hasBlankUnit && technicalFee === 0 ? null : technicalFee,
    summary: item.summary ?? '',
  }
}

function normalizeMaintenanceStatus(status: ApiMaintenanceDocument['status']): MaintenanceStatus {
  return status === '受付中' || status === '作業中' ? '下書き' : status
}

function toPayload(input: MaintenanceDocumentInput) {
  const payload: Record<string, unknown> = { ...input, number: input.number || undefined, issuedAt: input.issuedAt ? toApiDate(input.issuedAt) : undefined, intakeDate: toApiDate(input.intakeDate), plannedReleaseDate: toApiDate(input.plannedReleaseDate), completionDate: input.completionDate ? toApiDate(input.completionDate) : undefined, taxRate: Math.round(input.taxRate * 100), rounding: input.taxRounding, items: input.items.map(({ description, kind, quantity, unit, unitPrice, technicalFee, summary }) => ({ description, kind, quantity: quantity ?? 0, unit: unit ?? '', unitPrice: unitPrice ?? 0, technicalFee: technicalFee ?? 0, summary })) }
  if (input.masterSync) {
    payload.masterSync = input.masterSync
  }
  if (input.newCustomer) {
    payload.newCustomer = input.newCustomer
  }
  if (input.newVehicle) {
    payload.newVehicle = input.newVehicle
  }
  if (input.duplicateConfirmation) {
    payload.duplicateConfirmation = input.duplicateConfirmation
  }
  return payload
}

function normalizeMaintenanceDetails(value: MaintenanceDocumentDetails | null | undefined): MaintenanceDocumentDetails {
  const details = value ?? defaultMaintenanceDocumentDetails
  const sourceCustomerOverride = details.customerOverride
  const hasCustomerOverride = sourceCustomerOverride && (
    hasMaintenanceOverrideValue(sourceCustomerOverride)
    || hasOwnField(sourceCustomerOverride, 'birthDate')
    || hasOwnField(sourceCustomerOverride, 'employer')
  )
  const customerOverride = hasCustomerOverride ? ({
    name: sourceCustomerOverride.name,
    kana: sourceCustomerOverride.kana,
    phone: sourceCustomerOverride.phone,
    email: sourceCustomerOverride.email ?? '',
    postalCode: sourceCustomerOverride.postalCode,
    address: sourceCustomerOverride.address,
    ...(hasOwnField(sourceCustomerOverride, 'birthDate') ? { birthDate: sourceCustomerOverride.birthDate ?? '' } : {}),
    ...(hasOwnField(sourceCustomerOverride, 'employer') ? { employer: sourceCustomerOverride.employer ?? '' } : {}),
  } as MaintenanceDocumentDetails['customerOverride']) : null
  return {
    ...defaultMaintenanceDocumentDetails,
    ...details,
    bankName: typeof details.bankName === 'string' ? details.bankName : '',
    bankAccount: typeof details.bankAccount === 'string' ? details.bankAccount : '',
    bankAccountHolder: typeof details.bankAccountHolder === 'string' ? details.bankAccountHolder : '',
    customerOverride,
    vehicleOverride: details.vehicleOverride && hasMaintenanceOverrideValue(details.vehicleOverride) ? {
      maker: details.vehicleOverride.maker,
      name: details.vehicleOverride.name,
      modelType: details.vehicleOverride.modelType,
      plate: details.vehicleOverride.plate,
      vin: details.vehicleOverride.vin,
      year: details.vehicleOverride.year,
      inspectionDate: details.vehicleOverride.inspectionDate,
      mileage: details.vehicleOverride.mileage,
      color: details.vehicleOverride.color,
      displacement: details.vehicleOverride.displacement,
      transmission: details.vehicleOverride.transmission,
      inspectionRecordAvailable: details.vehicleOverride.inspectionRecordAvailable,
    } : null,
    labels: {
      ...defaultMaintenanceDocumentDetails.labels,
      ...details.labels,
      documentTitle: '',
      amountTitle: 'お見積金額（税込）',
      workSectionTitle: '作業内容／部品名等',
    },
  }
}

function hasMaintenanceOverrideValue(value: MaintenanceCustomerDetails | MaintenanceVehicleDetails | null) {
  return Boolean(value && Object.values(value).some((field) => typeof field === 'string' ? field.trim().length > 0 : field))
}

function formatDate(value: string | null) { return value ? value.slice(0, 10).replaceAll('-', '/') : '' }
function toApiDate(value: string) { return value ? value.replaceAll('/', '-') : null }
