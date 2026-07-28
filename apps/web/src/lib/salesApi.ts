import { apiFetch } from './api'

export type SalesDocumentType = '見積書' | '請求書'
export type SalesStatus = '下書き' | '発行済み' | '入金待ち' | 'アーカイブ済み'
export type SalesTaxCategory = '課税' | '非課税' | '対象外'

export type SalesCustomerDetails = {
  name: string
  kana: string
  phone: string
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
  customerOverride: Pick<SalesCustomerDetails, 'name' | 'kana' | 'phone' | 'postalCode' | 'address'> | null
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
  customerDetails: SalesCustomerDetails
  vehicleDetails: SalesVehicleDetails | null
  details: SalesDocumentDetails
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
  details?: SalesDocumentDetails
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
      details: input.details,
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
      details: document.details,
      items: document.items.map((item) => ({
        itemType: item.itemType,
        description: item.description,
        quantity: item.quantity,
        unit: item.unit,
        unitPrice: item.unitPrice,
        taxCategory: item.taxCategory,
        otherAmount: item.otherAmount,
        summary: item.summary,
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
    customerDetails: document.customerDetails ?? { name: document.customerName, kana: '', phone: document.phone, postalCode: '', address: '', birthDate: '', employer: '', contactPhone: '' },
    vehicleDetails: document.vehicleDetails ?? null,
    details: normalizeDetails(document.details),
    issuedAt: formatDate(document.issuedAt),
    dueDate: formatDate(document.dueDate),
    taxRate: document.taxRate / 100,
    note: document.note ?? '',
    items: document.items.map(({ id, itemType, description, quantity, unit, unitPrice, taxCategory, otherAmount, summary }) => ({ id, itemType: itemType || 'その他', description, quantity, unit, unitPrice, taxCategory: taxCategory || '課税', otherAmount: otherAmount ?? 0, summary: summary ?? '' })),
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
