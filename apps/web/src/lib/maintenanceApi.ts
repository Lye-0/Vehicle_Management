import { apiFetch } from './api'

export type MaintenanceDocumentType = '整備見積書' | '整備請求書'
export type MaintenanceStatus = '下書き' | '入金待ち' | '完了' | 'アーカイブ済み'
export type IntakeCategory = '車検' | '板金' | '一般整備'
export type MaintenanceItemKind = '作業' | '部品'
export type MandatoryFees = { 自賠責: number; 重量税: number; 印紙代: number; リサイクル料金: number }
export type MaintenanceFeeKey = keyof MandatoryFees | '調整額'
export type MaintenanceCustomerDetails = { name: string; kana: string; phone: string; postalCode: string; address: string }
export type MaintenanceVehicleDetails = { maker: string; name: string; modelType: string; plate: string; vin: string; year: string; inspectionDate: string; mileage: string; color: string; displacement: string; transmission: string; inspectionRecordAvailable: boolean }
export type MaintenanceDocumentDetails = {
  staffName: string
  customerHonorific: string
  customerBirthDate: string
  customerEmployer: string
  customerContactPhone: string
  bankName: string
  bankAccount: string
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
export type MaintenanceLineItem = { id: string; kind: MaintenanceItemKind; description: string; quantity: number; unit: string; unitPrice: number; technicalFee: number; summary: string }

export const defaultMaintenanceDocumentDetails: MaintenanceDocumentDetails = {
  staffName: '',
  customerHonorific: '様',
  customerBirthDate: '',
  customerEmployer: '',
  customerContactPhone: '',
  bankName: '',
  bankAccount: '',
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
  number: string
  type: MaintenanceDocumentType
  status: MaintenanceStatus
  category: IntakeCategory
  customerId: string
  customerName: string
  phone: string
  customerDetails: MaintenanceCustomerDetails
  vehicleId: string
  vehicle: string
  plate: string
  mileage: string
  vehicleDetails: MaintenanceVehicleDetails | null
  details: MaintenanceDocumentDetails
  intakeDate: string
  plannedReleaseDate: string
  completionDate: string
  issuedAt: string
  dueDate: string
  taxRate: number
  fees: MandatoryFees
  adjustment: number
  note: string
  items: MaintenanceLineItem[]
}

export type MaintenanceDocumentInput = {
  number?: string
  type: MaintenanceDocumentType
  status: MaintenanceStatus
  category: IntakeCategory
  customerId: string
  vehicleId: string
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
}

type ApiMaintenanceDocument = Omit<MaintenanceDocument, 'type' | 'status' | 'category' | 'taxRate' | 'intakeDate' | 'plannedReleaseDate' | 'completionDate' | 'issuedAt' | 'dueDate'> & { type: MaintenanceDocumentType | '納品書'; status: MaintenanceStatus | '受付中' | '作業中'; category: IntakeCategory | '法定点検'; taxRate: number; intakeDate: string | null; plannedReleaseDate: string | null; completionDate: string | null; issuedAt: string; dueDate: string | null }

export async function fetchMaintenanceDocuments() {
  const response = await apiFetch<{ documents: ApiMaintenanceDocument[] }>('/api/maintenance-documents')
  return response.documents.map(mapMaintenanceDocument)
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
    type: document.type === '納品書' ? '整備請求書' : document.type,
    status: normalizeMaintenanceStatus(document.status),
    category: document.category === '法定点検' ? '板金' : document.category,
    customerDetails: document.customerDetails ?? { name: document.customerName, kana: '', phone: document.phone, postalCode: '', address: '' },
    vehicleDetails: document.vehicleDetails ?? null,
    details: normalizeMaintenanceDetails(document.details),
    intakeDate: formatDate(document.intakeDate),
    plannedReleaseDate: formatDate(document.plannedReleaseDate),
    completionDate: formatDate(document.completionDate),
    issuedAt: formatDate(document.issuedAt),
    dueDate: formatDate(document.dueDate),
    taxRate: document.taxRate / 100,
    note: document.note ?? '',
    items: document.items.map((item) => ({ ...item, quantity: Number(item.quantity), unitPrice: Number(item.unitPrice), technicalFee: Number(item.technicalFee ?? 0), summary: item.summary ?? '' })),
  }
}

function normalizeMaintenanceStatus(status: ApiMaintenanceDocument['status']): MaintenanceStatus {
  return status === '受付中' || status === '作業中' ? '下書き' : status
}

function toPayload(input: MaintenanceDocumentInput) {
  return { ...input, number: input.number || undefined, issuedAt: input.issuedAt ? toApiDate(input.issuedAt) : undefined, intakeDate: toApiDate(input.intakeDate), plannedReleaseDate: toApiDate(input.plannedReleaseDate), completionDate: toApiDate(input.completionDate), taxRate: Math.round(input.taxRate * 100), rounding: input.taxRounding, items: input.items.map(({ description, kind, quantity, unit, unitPrice, technicalFee, summary }) => ({ description, kind, quantity, unit, unitPrice, technicalFee, summary })) }
}

function normalizeMaintenanceDetails(value: MaintenanceDocumentDetails | null | undefined): MaintenanceDocumentDetails {
  const details = value ?? defaultMaintenanceDocumentDetails
  return {
    ...defaultMaintenanceDocumentDetails,
    ...details,
    bankName: typeof details.bankName === 'string' ? details.bankName : '',
    bankAccount: typeof details.bankAccount === 'string' ? details.bankAccount : '',
    customerOverride: details.customerOverride && hasMaintenanceOverrideValue(details.customerOverride) ? {
      name: details.customerOverride.name,
      kana: details.customerOverride.kana,
      phone: details.customerOverride.phone,
      postalCode: details.customerOverride.postalCode,
      address: details.customerOverride.address,
    } : null,
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
