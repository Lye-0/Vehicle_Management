import { apiFetch } from './api'
import type { AbacusDocumentImportMetadata } from './abacusDocumentMetadata'
import type { AbacusDetailLine, AbacusDetailReport, AbacusDocumentAmounts } from './abacusDetail'

export type SalesDocumentType = '見積書' | '請求書'
export type SalesStatus = '下書き' | '入金待ち' | '完了' | 'アーカイブ済み'
export type SalesTaxCategory = '課税' | '非課税' | '対象外'

export type SalesCustomerDetails = {
  name: string
  kana: string
  phone: string
  email?: string
  postalCode: string
  address: string
  birthDate: string
  employer: string
  contactPhone: string
}

export type SalesVehicleDetails = {
  maker: string
  name: string
  modelType: string
  plate: string
  vin: string
  year: string
  inspectionDate: string
  mileage: string
  color: string
  displacement: string
  transmission: string
  inspectionRecordAvailable: boolean
}

export type SalesDocumentDetails = {
  salesCategory: string
  staffName: string
  customerHonorific: string
  customerBirthDate: string
  customerEmployer: string
  customerContactPhone: string
  selectedImageAttachmentId: string
  customerOverride: Pick<SalesCustomerDetails, 'name' | 'kana' | 'phone' | 'email' | 'postalCode' | 'address' | 'birthDate' | 'employer'> | null
  vehicleOverride: SalesVehicleDetails | null
  tradeIn: {
    name: string
    modelYear: string
    inspectionDate: string
    mileage: string
    color: string
  }
  recycleFee: number
  downPayment: number
  remainingPayment: number
  credit: {
    enabled: boolean
    paymentCount: string
    fee: number
    monthlyPayment: number
    initialPayment: number
    bonusMonths: string
    bonusPayment: number
  }
  requiredDocuments: {
    sealCertificate: boolean
    selfDeclaration: boolean
    residentCard: boolean
    powerOfAttorney: boolean
    lightVehicleCertificate: boolean
    transferCertificate: boolean
    taxPaymentCertificate: boolean
    guarantorSealCertificate: boolean
    warrantyCertificate: boolean
    other: string
  }
}

export const defaultSalesDocumentDetails: SalesDocumentDetails = {
  salesCategory: '中古車',
  staffName: '',
  customerHonorific: '様',
  customerBirthDate: '',
  customerEmployer: '',
  customerContactPhone: '',
  selectedImageAttachmentId: '',
  customerOverride: null,
  vehicleOverride: null,
  tradeIn: { name: '', modelYear: '', inspectionDate: '', mileage: '', color: '' },
  recycleFee: 0,
  downPayment: 0,
  remainingPayment: 0,
  credit: { enabled: false, paymentCount: '', fee: 0, monthlyPayment: 0, initialPayment: 0, bonusMonths: '', bonusPayment: 0 },
  requiredDocuments: { sealCertificate: false, selfDeclaration: false, residentCard: false, powerOfAttorney: false, lightVehicleCertificate: false, transferCertificate: false, taxPaymentCertificate: false, guarantorSealCertificate: false, warrantyCertificate: false, other: '' },
}

export type SalesLineItem = {
  id: string
  itemType: string
  description: string
  quantity: number
  unit: string
  unitPrice: number
  taxCategory: SalesTaxCategory
  otherAmount: number
  summary: string
  sourceRowIndex?: number
  abacusDetail?: AbacusDetailLine | null
  isAbacusMigration?: boolean
}

export type SalesDocument = {
  id: string
  number: string
  type: SalesDocumentType
  status: SalesStatus
  customerId: string
  customerName: string
  phone: string
  vehicleId: string | null
  vehicle: string
  plate: string
  /** ABACUSグラフ登録が付与する互換表示用メタデータ。通常書類では未設定です。 */
  abacusImport?: AbacusDocumentImportMetadata | null
  isAbacusMigration?: boolean
  abacusDetailReport?: AbacusDetailReport | null
  abacusAmounts?: AbacusDocumentAmounts | null
  customerDetails: SalesCustomerDetails
  vehicleDetails: SalesVehicleDetails | null
  details: SalesDocumentDetails
  issuedAt: string
  dueDate: string
  taxRate: number
  taxRounding: '切り捨て' | '四捨五入'
  note: string
  archivedAt: string | null
  archivedPreviousStatus: SalesStatus | null
  archivedBy: string | null
  purgeAt: string | null
  keepForever: boolean
  items: SalesLineItem[]
  isSummary?: boolean
}

export type SalesDocumentSummary = Pick<SalesDocument, 'id' | 'number' | 'type' | 'status' | 'customerId' | 'customerName' | 'phone' | 'vehicleId' | 'vehicle' | 'plate' | 'issuedAt' | 'dueDate' | 'archivedAt' | 'archivedPreviousStatus' | 'archivedBy' | 'purgeAt' | 'keepForever'> & {
  total: number
  abacusImport?: AbacusDocumentImportMetadata | null
  isAbacusMigration?: boolean
}

/** Persisted fields plus the optional identifiers used by an unsaved document draft. */
export type SalesDocumentLike = Omit<SalesDocument, 'id' | 'customerId' | 'vehicleId'> & {
  id?: string
  customerId: string | null
  vehicleId: string | null
}

export type SalesCreateInput = {
  type: SalesDocumentType
  status?: SalesStatus
  customerId?: string
  vehicleId?: string | null
  issuedAt?: string
  dueDate: string
  note: string
  taxRate: number
  taxRounding: '切り捨て' | '四捨五入'
  initialItemDescription?: string
  details?: SalesDocumentDetails
  items?: Array<Omit<SalesLineItem, 'id'>>
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
    registrationNumberConfirmed: true
    confirmedVehicleId: string
  }
  masterSync?: {
    confirmed: true
    customerFields: string[]
    vehicleFields: string[]
    expectedCustomerUpdatedAt?: string
    expectedVehicleUpdatedAt?: string
  }
}

type ApiSalesDocument = Omit<SalesDocument, 'taxRate' | 'issuedAt' | 'dueDate' | 'items' | 'details'> & {
  taxRate: number
  issuedAt: string
  dueDate: string | null
  details?: SalesDocumentDetails | null
  items?: Array<SalesLineItem & { amount: number }>
  summary?: boolean
}

export async function fetchSalesDocuments() {
  const response = await apiFetch<{ documents: ApiSalesDocument[] }>('/api/sales-documents')
  return response.documents.map(mapSalesDocument)
}

export async function fetchSalesDocumentSummaries(options: { q?: string; type?: string; status?: string; cursor?: string | null; limit?: number; includeArchived?: boolean; sortKey?: string; sortDirection?: string } = {}) {
  const params = new URLSearchParams({ view: 'summary', limit: String(options.limit ?? 50) })
  if (options.q?.trim()) params.set('q', options.q.trim())
  if (options.type && options.type !== 'すべて') params.set('type', options.type)
  if (options.status && options.status !== 'すべて') params.set('status', options.status)
  if (options.cursor) params.set('cursor', options.cursor)
  if (options.includeArchived) params.set('includeArchived', 'true')
  if (options.sortKey) params.set('sortKey', options.sortKey)
  if (options.sortDirection) params.set('sortDirection', options.sortDirection)
  const response = await apiFetch<{ documents: ApiSalesDocument[]; nextCursor: string | null; hasMore: boolean }>(`/api/sales-documents?${params.toString()}`)
  return { documents: response.documents.map(mapSalesDocument), nextCursor: response.nextCursor, hasMore: response.hasMore }
}

export async function fetchSalesDocument(id: string) {
  const response = await apiFetch<{ document: ApiSalesDocument }>(`/api/sales-documents/${encodeURIComponent(id)}`)
  return mapSalesDocument(response.document)
}

export async function createSalesDocument(input: SalesCreateInput) {
  const payload: Record<string, unknown> = {
    type: input.type,
    status: input.status ?? '下書き',
    issuedAt: input.issuedAt ? toApiDate(input.issuedAt) : today(),
    dueDate: toApiDate(input.dueDate),
    taxRate: input.taxRate,
    rounding: input.taxRounding,
    note: input.note,
    details: input.details,
    items: input.items?.map((item) => ({
      itemType: item.itemType,
      description: item.description,
      quantity: item.quantity,
      unit: item.unit,
      unitPrice: item.unitPrice,
      taxCategory: item.taxCategory,
      otherAmount: item.otherAmount,
      summary: item.summary,
    })) ?? [{ description: input.initialItemDescription ?? '', quantity: 1, unit: '式', unitPrice: 0 }],
  }
  if (input.newCustomer) {
    payload.newCustomer = input.newCustomer
  } else if (input.customerId) {
    payload.customerId = input.customerId
  }
  if (input.newVehicle) {
    payload.newVehicle = input.newVehicle
  } else if (input.vehicleId) {
    payload.vehicleId = input.vehicleId
  }
  if (input.duplicateConfirmation) {
    payload.duplicateConfirmation = input.duplicateConfirmation
  }
  if (input.masterSync) {
    payload.masterSync = input.masterSync
  }
  const response = await apiFetch<{ document: ApiSalesDocument }>('/api/sales-documents', {
    method: 'POST',
    body: JSON.stringify(payload),
  })
  return mapSalesDocument(response.document)
}

export type SalesDocumentInput = {
  number?: string
  type: SalesDocumentType
  status: SalesStatus
  customerId: string
  vehicleId: string | null
  issuedAt?: string
  dueDate: string
  taxRate: number
  taxRounding: '切り捨て' | '四捨五入'
  note: string
  details: SalesDocumentDetails
  items: Array<Omit<SalesLineItem, 'id'>>
  masterSync?: {
    confirmed: true
    customerFields: string[]
    vehicleFields: string[]
    expectedCustomerUpdatedAt?: string
    expectedVehicleUpdatedAt?: string
  }
}

export async function updateSalesDocument(id: string, input: SalesDocumentInput) {
  const response = await apiFetch<{ document: ApiSalesDocument }>(`/api/sales-documents/${id}`, {
    method: 'PATCH',
    body: JSON.stringify(toPayload(input)),
  })
  return mapSalesDocument(response.document)
}

function toPayload(input: SalesDocumentInput) {
  const payload: Record<string, unknown> = {
    type: input.type,
    number: input.number || undefined,
    status: input.status,
    customerId: input.customerId,
    vehicleId: input.vehicleId,
    issuedAt: input.issuedAt ? toApiDate(input.issuedAt) : undefined,
    dueDate: toApiDate(input.dueDate),
    taxRate: Math.round(input.taxRate * 100),
    rounding: input.taxRounding,
    note: input.note,
    details: input.details,
    items: input.items.map((item) => ({
      itemType: item.itemType,
      description: item.description,
      quantity: item.quantity,
      unit: item.unit,
      unitPrice: item.unitPrice,
      taxCategory: item.taxCategory,
      otherAmount: item.otherAmount,
      summary: item.summary,
    })),
  }
  if (input.masterSync) {
    payload.masterSync = input.masterSync
  }
  return payload
}

export async function archiveSalesDocument(id: string) {
  await apiFetch(`/api/sales-documents/${id}`, { method: 'DELETE' })
}

export async function restoreSalesDocument(id: string) {
  await apiFetch(`/api/sales-documents/${id}/restore`, { method: 'POST' })
}

function mapSalesDocument(document: ApiSalesDocument): SalesDocument {
  return {
    ...document,
    isSummary: document.summary === true,
    customerDetails: document.customerDetails ?? { name: document.customerName, kana: '', phone: document.phone, email: '', postalCode: '', address: '', birthDate: '', employer: '', contactPhone: '' },
    vehicleDetails: document.vehicleDetails ?? null,
    details: normalizeDetails(document.details),
    issuedAt: formatDate(document.issuedAt),
    dueDate: formatDate(document.dueDate),
    taxRate: document.taxRate / 100,
    taxRounding: document.taxRounding === '四捨五入' ? '四捨五入' : '切り捨て',
    note: document.note ?? '',
    items: (document.items ?? []).map(({ id, itemType, description, quantity, unit, unitPrice, taxCategory, otherAmount, summary, sourceRowIndex, abacusDetail, isAbacusMigration }) => ({ id, itemType: itemType || 'その他', description, quantity, unit, unitPrice, taxCategory: taxCategory || '課税', otherAmount: otherAmount ?? 0, summary: summary ?? '', sourceRowIndex, abacusDetail: abacusDetail ?? null, isAbacusMigration })),
  }
}

function normalizeDetails(value: SalesDocumentDetails | null | undefined): SalesDocumentDetails {
  const details = value ?? defaultSalesDocumentDetails
  return {
    ...defaultSalesDocumentDetails,
    ...details,
    tradeIn: { ...defaultSalesDocumentDetails.tradeIn, ...details.tradeIn },
    customerOverride: details.customerOverride ? { ...details.customerOverride } : null,
    vehicleOverride: details.vehicleOverride ? { ...details.vehicleOverride } : null,
    credit: { ...defaultSalesDocumentDetails.credit, ...details.credit },
    requiredDocuments: { ...defaultSalesDocumentDetails.requiredDocuments, ...details.requiredDocuments },
  }
}

function formatDate(value: string | null) {
  return value ? value.slice(0, 10).replaceAll('-', '/') : ''
}

function toApiDate(value: string) {
  return value ? value.replaceAll('/', '-') : null
}

function today() {
  return new Date().toISOString().slice(0, 10)
}
