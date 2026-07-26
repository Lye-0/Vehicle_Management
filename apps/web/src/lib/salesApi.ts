import { apiFetch } from './api'

export type SalesDocumentType = '見積書' | '注文書' | '請求書'
export type SalesStatus = '下書き' | '発行済み' | '入金待ち' | 'アーカイブ済み'

export type SalesLineItem = {
  id: string
  itemType: string
  description: string
  quantity: number
  unit: string
  unitPrice: number
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
  issuedAt: string
  dueDate: string
  taxRate: number
  note: string
  archivedAt: string | null
  items: SalesLineItem[]
}

export type SalesCreateInput = {
  type: SalesDocumentType
  customerId: string
  vehicleId: string | null
  dueDate: string
  note: string
  taxRate: number
  taxRounding: '切り捨て' | '四捨五入'
  initialItemDescription: string
}

type ApiSalesDocument = Omit<SalesDocument, 'taxRate' | 'issuedAt' | 'dueDate' | 'items'> & {
  taxRate: number
  issuedAt: string
  dueDate: string | null
  items: Array<SalesLineItem & { amount: number }>
}

export async function fetchSalesDocuments() {
  const response = await apiFetch<{ documents: ApiSalesDocument[] }>('/api/sales-documents')
  return response.documents.map(mapSalesDocument)
}

export async function createSalesDocument(input: SalesCreateInput) {
  const response = await apiFetch<{ document: ApiSalesDocument }>('/api/sales-documents', {
    method: 'POST',
    body: JSON.stringify({
      type: input.type,
      customerId: input.customerId,
      vehicleId: input.vehicleId,
      issuedAt: today(),
      dueDate: toApiDate(input.dueDate),
      taxRate: input.taxRate,
      rounding: input.taxRounding,
      note: input.note,
      items: [{ description: input.initialItemDescription, quantity: 1, unit: '式', unitPrice: 0 }],
    }),
  })
  return mapSalesDocument(response.document)
}

export async function updateSalesDocument(document: SalesDocument, taxRounding: '切り捨て' | '四捨五入') {
  const response = await apiFetch<{ document: ApiSalesDocument }>(`/api/sales-documents/${document.id}`, {
    method: 'PATCH',
    body: JSON.stringify({
      type: document.type,
      number: document.number,
      status: document.status,
      customerId: document.customerId,
      vehicleId: document.vehicleId,
      issuedAt: toApiDate(document.issuedAt),
      dueDate: toApiDate(document.dueDate),
      taxRate: Math.round(document.taxRate * 100),
      rounding: taxRounding,
      note: document.note,
      items: document.items.map((item) => ({
        itemType: item.itemType,
        description: item.description,
        quantity: item.quantity,
        unit: item.unit,
        unitPrice: item.unitPrice,
      })),
    }),
  })
  return mapSalesDocument(response.document)
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
    issuedAt: formatDate(document.issuedAt),
    dueDate: formatDate(document.dueDate),
    taxRate: document.taxRate / 100,
    note: document.note ?? '',
    items: document.items.map(({ id, itemType, description, quantity, unit, unitPrice }) => ({ id, itemType: itemType || 'その他', description, quantity, unit, unitPrice })),
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
