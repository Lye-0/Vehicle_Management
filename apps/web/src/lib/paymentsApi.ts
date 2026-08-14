import { apiFetch } from './api'

export type PaymentMethod = '現金' | '銀行振込' | 'クレジットカード' | 'その他' | ''
export type PaymentDocumentStatus = '入金待ち' | '完了'
export type PaymentEntry = {
  id: string
  amount: number
  paymentDate: string
  method: Exclude<PaymentMethod, ''> | ''
  note: string
  createdAt: string
  updatedAt: string
}
export type PaymentEntryInput = {
  amount: number
  paymentDate: string
  method: Exclude<PaymentMethod, ''>
  note: string
}
export type PaymentRecord = {
  id: string
  documentType: '販売請求書' | '整備請求書'
  documentId: string
  number: string
  sourceType: '販売請求書' | '整備請求書'
  documentStatus: PaymentDocumentStatus
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
  paymentHistory: PaymentEntry[]
  isSummary?: boolean
}

type ApiPaymentEntry = Omit<PaymentEntry, 'paymentDate' | 'method'> & { paymentDate: string | null; method: string | null }
type ApiPaymentRecord = Omit<PaymentRecord, 'issuedAt' | 'dueDate' | 'paymentDate' | 'method' | 'paymentHistory'> & { issuedAt: string; dueDate: string | null; paymentDate: string | null; method: string | null; paymentHistory: ApiPaymentEntry[] }

export async function fetchPayments() {
  const response = await apiFetch<{ records: ApiPaymentRecord[] }>('/api/payments')
  return response.records.map(mapPaymentRecord)
}

export async function fetchPaymentSummaries(options: { q?: string; type?: string; cursor?: string | null; limit?: number; sortKey?: string; sortDirection?: string } = {}) {
  const params = new URLSearchParams({ view: 'summary', limit: String(options.limit ?? 50) })
  if (options.q?.trim()) params.set('q', options.q.trim())
  if (options.type && options.type !== 'すべて') params.set('type', options.type)
  if (options.cursor) params.set('cursor', options.cursor)
  if (options.sortKey) params.set('sortKey', options.sortKey)
  if (options.sortDirection) params.set('sortDirection', options.sortDirection)
  const response = await apiFetch<{ records: ApiPaymentRecord[]; nextCursor: string | null; hasMore: boolean }>(`/api/payments?${params.toString()}`)
  return { records: response.records.map(mapPaymentRecord), nextCursor: response.nextCursor, hasMore: response.hasMore }
}

export async function fetchPaymentRecord(documentType: PaymentRecord['documentType'], documentId: string) {
  const response = await apiFetch<{ record: ApiPaymentRecord }>(`/api/payments/${encodeURIComponent(documentType)}/${encodeURIComponent(documentId)}`)
  return mapPaymentRecord(response.record)
}

export async function updatePayment(record: PaymentRecord) {
  const response = await apiFetch<{ record: ApiPaymentRecord }>(`/api/payments/${encodeURIComponent(record.documentType)}/${encodeURIComponent(record.documentId)}`, { method: 'PATCH', body: JSON.stringify({ paidAmount: record.paidAmount, paymentDate: record.paymentDate ? record.paymentDate.replaceAll('/', '-') : null, method: record.method, note: record.note }) })
  return mapPaymentRecord(response.record)
}

export async function addPaymentEntry(record: PaymentRecord, input: PaymentEntryInput) {
  const response = await apiFetch<{ record: ApiPaymentRecord }>(paymentEntriesUrl(record), { method: 'POST', body: JSON.stringify(input) })
  return mapPaymentRecord(response.record)
}

export async function updatePaymentEntry(record: PaymentRecord, entryId: string, input: PaymentEntryInput) {
  const response = await apiFetch<{ record: ApiPaymentRecord }>(`${paymentEntriesUrl(record)}/${encodeURIComponent(entryId)}`, { method: 'PATCH', body: JSON.stringify(input) })
  return mapPaymentRecord(response.record)
}

export async function deletePaymentEntry(record: PaymentRecord, entryId: string) {
  const response = await apiFetch<{ record: ApiPaymentRecord }>(`${paymentEntriesUrl(record)}/${encodeURIComponent(entryId)}`, { method: 'DELETE' })
  return mapPaymentRecord(response.record)
}

function mapPaymentRecord(record: ApiPaymentRecord): PaymentRecord {
  return { ...record, isSummary: (record as ApiPaymentRecord & { summary?: boolean }).summary === true, issuedAt: formatDate(record.issuedAt), dueDate: formatDate(record.dueDate), paymentDate: formatDate(record.paymentDate), method: isPaymentMethod(record.method) ? record.method : '', note: record.note ?? '', paymentHistory: (record.paymentHistory ?? []).map((entry) => ({ ...entry, paymentDate: formatDate(entry.paymentDate), method: isPaymentMethod(entry.method) ? entry.method : '', note: entry.note ?? '' })) }
}

function paymentEntriesUrl(record: PaymentRecord) { return `/api/payments/${encodeURIComponent(record.documentType)}/${encodeURIComponent(record.documentId)}/entries` }
function formatDate(value: string | null) { return value ? value.slice(0, 10).replaceAll('-', '/') : '' }
function isPaymentMethod(value: string | null): value is Exclude<PaymentMethod, ''> { return value === '現金' || value === '銀行振込' || value === 'クレジットカード' || value === 'その他' }
