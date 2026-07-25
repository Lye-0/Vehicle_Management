import { apiFetch } from './api'

export type MaintenanceDocumentType = '整備見積書' | '納品書' | '整備請求書'
export type MaintenanceStatus = '受付中' | '作業中' | '完了' | '入金待ち'
export type IntakeCategory = '車検' | '法定点検' | '一般整備'
export type MaintenanceItemKind = '作業' | '部品'
export type MandatoryFees = { 自賠責: number; 重量税: number; 印紙代: number; リサイクル料金: number }
export type MaintenanceLineItem = { id: string; kind: MaintenanceItemKind; description: string; quantity: number; unit: string; unitPrice: number }
export type MaintenanceDocument = {
  id: string
  number: string
  type: MaintenanceDocumentType
  status: MaintenanceStatus
  category: IntakeCategory
  customerId: string
  customerName: string
  phone: string
  vehicleId: string
  vehicle: string
  plate: string
  mileage: string
  intakeDate: string
  plannedReleaseDate: string
  issuedAt: string
  dueDate: string
  taxRate: number
  fees: MandatoryFees
  adjustment: number
  note: string
  items: MaintenanceLineItem[]
}

export type MaintenanceDocumentInput = {
  type: MaintenanceDocumentType
  status: MaintenanceStatus
  category: IntakeCategory
  customerId: string
  vehicleId: string
  intakeDate: string
  plannedReleaseDate: string
  taxRate: number
  fees: MandatoryFees
  adjustment: number
  note: string
  items: Array<Omit<MaintenanceLineItem, 'id'>>
}

type ApiMaintenanceDocument = Omit<MaintenanceDocument, 'taxRate' | 'intakeDate' | 'plannedReleaseDate' | 'issuedAt' | 'dueDate'> & { taxRate: number; intakeDate: string | null; plannedReleaseDate: string | null; issuedAt: string; dueDate: string | null }

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

function mapMaintenanceDocument(document: ApiMaintenanceDocument): MaintenanceDocument {
  return { ...document, intakeDate: formatDate(document.intakeDate), plannedReleaseDate: formatDate(document.plannedReleaseDate), issuedAt: formatDate(document.issuedAt), dueDate: formatDate(document.dueDate), taxRate: document.taxRate / 100, note: document.note ?? '', items: document.items.map((item) => ({ ...item, quantity: Number(item.quantity), unitPrice: Number(item.unitPrice) })) }
}

function toPayload(input: MaintenanceDocumentInput) {
  return { ...input, intakeDate: toApiDate(input.intakeDate), plannedReleaseDate: toApiDate(input.plannedReleaseDate), taxRate: Math.round(input.taxRate * 100), items: input.items.map(({ description, kind, quantity, unit, unitPrice }) => ({ description, kind, quantity, unit, unitPrice })) }
}

function formatDate(value: string | null) { return value ? value.slice(0, 10).replaceAll('-', '/') : '' }
function toApiDate(value: string) { return value ? value.replaceAll('/', '-') : null }
