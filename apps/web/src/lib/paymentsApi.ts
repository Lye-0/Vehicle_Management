import { apiFetch } from './api'

export type PaymentMethod = '現金' | '銀行振込' | 'クレジットカード' | 'その他' | ''
export type PaymentRecord = {
  id: string
  documentType: '販売請求書' | '整備請求書'
  documentId: string
  number: string
  sourceType: '販売請求書' | '整備請求書'
  customerName: string
  phone: string
  vehicle: string
  plate: string
  issuedAt: string
  dueDate: string
  invoiceAmount: number
  paidAmount: number
  paymentDate: string
  method: PaymentMethod
  note: string
}

type ApiPaymentRecord = Omit<PaymentRecord, 'issuedAt' | 'dueDate' | 'paymentDate' | 'method'> & { issuedAt: string; dueDate: string | null; paymentDate: string | null; method: string | null }

export async function fetchPayments() {
  const response = await apiFetch<{ records: ApiPaymentRecord[] }>('/api/payments')
  return response.records.map(mapPaymentRecord)
}

export async function updatePayment(record: PaymentRecord) {
  const response = await apiFetch<{ record: ApiPaymentRecord }>(`/api/payments/${encodeURIComponent(record.documentType)}/${encodeURIComponent(record.documentId)}`, { method: 'PATCH', body: JSON.stringify({ paidAmount: record.paidAmount, paymentDate: record.paymentDate ? record.paymentDate.replaceAll('/', '-') : null, method: record.method, note: record.note }) })
  return mapPaymentRecord(response.record)
}

function mapPaymentRecord(record: ApiPaymentRecord): PaymentRecord {
  return { ...record, issuedAt: formatDate(record.issuedAt), dueDate: formatDate(record.dueDate), paymentDate: formatDate(record.paymentDate), method: isPaymentMethod(record.method) ? record.method : '', note: record.note ?? '' }
}

function formatDate(value: string | null) { return value ? value.slice(0, 10).replaceAll('-', '/') : '' }
function isPaymentMethod(value: string | null): value is Exclude<PaymentMethod, ''> { return value === '現金' || value === '銀行振込' || value === 'クレジットカード' || value === 'その他' }
