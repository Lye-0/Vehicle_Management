import { useEffect, useMemo, useState } from 'react'
import {
  Banknote,
  CalendarDays,
  CarFront,
  CheckCircle2,
  ChevronRight,
  CircleDollarSign,
  FileDown,
  FileText,
  Info,
  Plus,
  Search,
  UserRound,
  WalletCards,
} from 'lucide-react'
import { fetchPayments, updatePayment, type PaymentDocumentStatus, type PaymentMethod, type PaymentRecord } from '../lib/paymentsApi'
import { printDocument } from '../lib/print'
import type { VehicleHistoryNavigation } from './CustomerVehiclePage'

type PaymentStatus = '未入金' | '一部入金' | '入金済み'
type PaymentFilter = 'すべて' | PaymentStatus
type InvoiceType = PaymentRecord['sourceType']
type InvoiceTypeFilter = 'すべて' | InvoiceType
type DocumentStatusFilter = 'すべて' | PaymentDocumentStatus

export function PaymentsPage({ initialRecordId, onNavigate }: { initialRecordId?: string; onNavigate?: (target: VehicleHistoryNavigation) => void } = {}) {
  const [records, setRecords] = useState<PaymentRecord[]>([])
  const [query, setQuery] = useState('')
  const [statusFilter, setStatusFilter] = useState<PaymentFilter>('すべて')
  const [invoiceTypeFilter, setInvoiceTypeFilter] = useState<InvoiceTypeFilter>('すべて')
  const [documentStatusFilter, setDocumentStatusFilter] = useState<DocumentStatusFilter>('すべて')
  const [selectedRecordId, setSelectedRecordId] = useState(initialRecordId ?? '')
  const [saved, setSaved] = useState(false)
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState('')

  useEffect(() => {
    let cancelled = false
    fetchPayments()
      .then((nextRecords) => { if (!cancelled) { setRecords(nextRecords); setSelectedRecordId((current) => nextRecords.some((record) => record.id === current) ? current : nextRecords[0]?.id ?? ''); setError('') } })
      .catch((reason: unknown) => { if (!cancelled) setError(reason instanceof Error ? reason.message : '入金データを読み込めませんでした。') })
      .finally(() => { if (!cancelled) setLoading(false) })
    return () => { cancelled = true }
  }, [])

  const filteredRecords = useMemo(() => {
    const normalizedQuery = query.trim().toLocaleLowerCase()
    return records.filter((record) => {
      const matchesStatus = statusFilter === 'すべて' || getPaymentStatus(record) === statusFilter
      const matchesInvoiceType = invoiceTypeFilter === 'すべて' || record.sourceType === invoiceTypeFilter
      const matchesDocumentStatus = documentStatusFilter === 'すべて' || record.documentStatus === documentStatusFilter
      const searchableText = `${record.number} ${record.customerName} ${record.vehicle} ${record.plate} ${record.sourceType} ${record.documentStatus}`.toLocaleLowerCase()
      return matchesStatus && matchesInvoiceType && matchesDocumentStatus && (!normalizedQuery || searchableText.includes(normalizedQuery))
    })
  }, [documentStatusFilter, invoiceTypeFilter, query, records, statusFilter])

  const selectedRecord = filteredRecords.find((record) => record.id === selectedRecordId) ?? filteredRecords[0] ?? null

  function updateSelected(patch: Partial<PaymentRecord>) {
    if (!selectedRecord) return
    setRecords((current) => current.map((record) => record.id === selectedRecord.id ? { ...record, ...patch } : record))
    setSaved(false)
  }

  async function saveSelected() {
    if (!selectedRecord) return
    setSaving(true)
    setError('')
    try {
      const savedRecord = await updatePayment(selectedRecord)
      setRecords((current) => current.map((record) => record.id === savedRecord.id ? savedRecord : record))
      setSelectedRecordId(savedRecord.id)
      setSaved(true)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : '入金情報を保存できませんでした。')
      setSaved(false)
    } finally {
      setSaving(false)
    }
  }

  function focusUnpaidRecord() {
    const firstUnpaid = records.find((record) => getPaymentStatus(record) === '未入金')
    setStatusFilter('未入金')
    setInvoiceTypeFilter('すべて')
    setDocumentStatusFilter('すべて')
    if (firstUnpaid) setSelectedRecordId(firstUnpaid.id)
  }

  function resetFilters() {
    setStatusFilter('すべて')
    setInvoiceTypeFilter('すべて')
    setDocumentStatusFilter('すべて')
  }

  return <>
    <div className="page-header payment-page-header"><div><span className="page-eyebrow">請求・入金</span><h1>入金管理</h1><p>請求ごとの現在の入金情報を登録し、未入金額を確認します。</p></div><button className="button button-primary" type="button" disabled={!records.length} onClick={focusUnpaidRecord}><Plus size={18} />入金を登録</button></div>
    {error && <div className="customer-sync-status is-error"><span>{error}</span><button className="text-button" type="button" onClick={() => window.location.reload()}>再読み込み</button></div>}
    {loading && <div className="customer-sync-status"><span>請求・入金データを読み込んでいます。</span></div>}
    <div className="payment-scope-note"><Info size={17} /><div><strong>表示対象</strong><span>販売タブの請求書と、車検・点検・一般タブの整備請求書のうち、書類状態が「入金待ち」または「完了」のものを表示しています。</span></div></div>
    <div className="payment-toolbar"><label className="payment-search"><Search size={18} /><span className="sr-only">入金情報を検索</span><input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="請求書番号、顧客名、車名で検索" /></label></div>
    <div className="payment-filter-panel"><PaymentFilterGroup label="入金状況" value={statusFilter} options={['すべて', '未入金', '一部入金', '入金済み'] as PaymentFilter[]} onChange={setStatusFilter} /><PaymentFilterGroup label="請求書の種類" value={invoiceTypeFilter} options={['すべて', '販売請求書', '整備請求書'] as InvoiceTypeFilter[]} onChange={setInvoiceTypeFilter} /><PaymentFilterGroup label="書類状態（元タブ）" value={documentStatusFilter} options={['すべて', '入金待ち', '完了'] as DocumentStatusFilter[]} onChange={setDocumentStatusFilter} /><button className="text-button payment-filter-reset" type="button" onClick={resetFilters} disabled={statusFilter === 'すべて' && invoiceTypeFilter === 'すべて' && documentStatusFilter === 'すべて'}>条件をリセット</button></div>
    <div className="payment-workspace"><PaymentRecordList records={filteredRecords} selectedRecordId={selectedRecord?.id ?? ''} onSelect={(id) => { setSelectedRecordId(id); setSaved(false) }} />{selectedRecord ? <PaymentRecordDetail record={selectedRecord} saved={saved} saving={saving} onUpdate={updateSelected} onSave={() => void saveSelected()} onOpenDocument={onNavigate ? () => onNavigate({ section: selectedRecord.sourceType === '販売請求書' ? 'sales' : 'maintenance', recordId: selectedRecord.documentId }) : undefined} onPdf={() => printDocument(`${selectedRecord.number}-${selectedRecord.sourceType}`)} /> : <div className="panel payment-empty"><WalletCards size={30} /><strong>請求が見つかりません</strong><span>{loading ? '読み込み中です。' : '検索条件または入金状態を変更してください。'}</span></div>}</div>
  </>
}

function PaymentRecordList({ records, selectedRecordId, onSelect }: { records: PaymentRecord[]; selectedRecordId: string; onSelect: (id: string) => void }) {
  return <section className="panel payment-list-panel"><div className="payment-list-header"><div><h2>請求一覧</h2><span>請求を選択すると現在の入金情報を表示します</span></div><span className="results-count">{records.length}件</span></div><div className="payment-record-list">{records.map((record) => { const status = getPaymentStatus(record); return <button className={`payment-record-card${record.id === selectedRecordId ? ' is-selected' : ''}`} key={record.id} type="button" onClick={() => onSelect(record.id)}><div className="payment-card-top"><span className={`payment-type-badge payment-type-${record.sourceType === '販売請求書' ? 'sales' : 'maintenance'}`}>{record.sourceType}</span><DocumentStatusTag status={record.documentStatus} /><PaymentStatusTag status={status} /><ChevronRight size={16} /></div><strong className="payment-card-number">{record.number}</strong><span className="payment-card-customer"><UserRound size={14} />{record.customerName}</span><span className="payment-card-vehicle"><CarFront size={14} />{record.vehicle} ・ {record.plate}</span><div className="payment-card-bottom"><span>未入金 {formatYen(getOutstandingAmount(record))}</span><strong className={isOverdue(record) && status !== '入金済み' ? 'is-overdue' : ''}>{status === '入金済み' ? '入金済み' : `期限 ${record.dueDate || '未設定'}`}</strong></div></button> })}</div></section>
}

function PaymentRecordDetail({ record, saved, saving, onUpdate, onSave, onOpenDocument, onPdf }: { record: PaymentRecord; saved: boolean; saving: boolean; onUpdate: (patch: Partial<PaymentRecord>) => void; onSave: () => void; onOpenDocument?: () => void; onPdf: () => void }) {
  const status = getPaymentStatus(record)
  const outstanding = getOutstandingAmount(record)
  return <section className="panel payment-detail-panel"><div className="payment-detail-header"><div className="payment-detail-title"><div><div className="payment-detail-badges"><span className={`payment-type-badge payment-type-${record.sourceType === '販売請求書' ? 'sales' : 'maintenance'}`}>{record.sourceType}</span><DocumentStatusTag status={record.documentStatus} /></div><h2>{record.number}</h2><small>{record.issuedAt} 作成</small></div><PaymentStatusTag status={status} /></div><div className="payment-detail-actions"><button className="button button-primary" type="button" disabled={saving} onClick={onSave}><WalletCards size={16} />{saving ? '保存中…' : saved ? '保存済み' : '保存'}</button>{onOpenDocument && <button className="button button-secondary" type="button" onClick={onOpenDocument}><FileText size={16} />書類を開く</button>}<button className="button button-secondary" type="button" onClick={onPdf}><FileDown size={16} />PDF出力</button></div></div><div className="payment-detail-content"><div className="payment-context-grid"><div className="payment-context-card"><span className="payment-context-label"><UserRound size={15} />顧客</span><strong>{record.customerName}</strong><small>{record.phone}</small></div><div className="payment-context-card"><span className="payment-context-label"><CarFront size={15} />対象車両</span><strong>{record.vehicle || '車両未指定'}</strong><small>{record.plate}</small></div></div><section className="payment-amount-panel"><div className="payment-amount-header"><div><h3>請求金額と入金状況</h3><span>請求に対する現在の入金額を確認します</span></div><span className="payment-amount-icon"><CircleDollarSign size={20} /></span></div><div className="payment-amount-grid"><PaymentAmount label="請求金額" value={record.invoiceAmount} /><PaymentAmount label="入金済み" value={record.paidAmount} tone="paid" /><PaymentAmount label="未入金額" value={outstanding} tone={outstanding > 0 ? 'outstanding' : 'paid'} emphasized /></div></section><section className="payment-form-panel"><div className="payment-form-header"><div><h3>現在の入金情報</h3><span>この請求に対する最新の入金内容を登録します</span></div><Banknote size={21} /></div><div className="payment-form-grid"><label className="form-field"><span>今回までの入金額<em>必須</em></span><div className="payment-input-with-prefix"><span>¥</span><input type="number" min="0" max={record.invoiceAmount} value={record.paidAmount} onChange={(event) => { const amount = Math.min(record.invoiceAmount, Math.max(0, Number(event.target.value) || 0)); onUpdate({ paidAmount: amount }) }} /></div></label><label className="form-field"><span>入金日</span><input type="date" value={toDateInputValue(record.paymentDate)} onChange={(event) => onUpdate({ paymentDate: fromDateInputValue(event.target.value) })} /></label><label className="form-field"><span>入金方法</span><select value={record.method} onChange={(event) => onUpdate({ method: event.target.value as PaymentMethod })}><option value="">選択してください</option><option>現金</option><option>銀行振込</option><option>クレジットカード</option><option>その他</option></select></label></div><label className="form-field payment-note-field"><span>メモ</span><textarea value={record.note} onChange={(event) => onUpdate({ note: event.target.value })} placeholder="入金に関するメモ" /></label><div className="payment-save-row"><span className="payment-current-note"><CalendarDays size={15} />入金履歴は作成せず、請求ごとの現在情報を管理します</span>{saved && <span className="payment-saved"><CheckCircle2 size={15} />保存しました</span>}</div></section><div className="payment-detail-footer"><span><FileText size={15} />請求書発行日：{record.issuedAt}</span><span><CalendarDays size={15} />支払期限：{record.dueDate || '未設定'}{isOverdue(record) && status !== '入金済み' ? '（期限超過）' : ''}</span></div></div></section>
}

function PaymentAmount({ label, value, tone = 'normal', emphasized = false }: { label: string; value: number; tone?: 'normal' | 'paid' | 'outstanding'; emphasized?: boolean }) { return <div className={`payment-amount-item payment-amount-${tone}${emphasized ? ' is-emphasized' : ''}`}><span>{label}</span><strong>{formatYen(value)}</strong></div> }
function PaymentFilterGroup<T extends string>({ label, value, options, onChange }: { label: string; value: T; options: T[]; onChange: (value: T) => void }) { return <div className="payment-filter-group"><span className="payment-filter-label">{label}</span><div className="payment-filter-options" role="group" aria-label={label}>{options.map((option) => <button className={value === option ? 'is-active' : ''} key={option} type="button" aria-pressed={value === option} onClick={() => onChange(option)}>{option}</button>)}</div></div> }
function DocumentStatusTag({ status }: { status: PaymentDocumentStatus }) { const tone = status === '完了' ? 'completed' : 'pending'; return <span className={`payment-document-status payment-document-status-${tone}`}><span className="status-dot" />{status}</span> }
function PaymentStatusTag({ status }: { status: PaymentStatus }) { const tone = status === '入金済み' ? 'paid' : status === '一部入金' ? 'partial' : 'unpaid'; return <span className={`payment-status-tag payment-status-${tone}`}><span className="status-dot" />{status}</span> }
function getPaymentStatus(record: PaymentRecord): PaymentStatus { if (record.paidAmount >= record.invoiceAmount && record.invoiceAmount > 0) return '入金済み'; if (record.paidAmount > 0) return '一部入金'; return '未入金' }
function getOutstandingAmount(record: PaymentRecord) { return Math.max(record.invoiceAmount - record.paidAmount, 0) }
function isOverdue(record: PaymentRecord) { return Boolean(record.dueDate) && new Date(record.dueDate.replace(/\//g, '-')).getTime() < Date.now() }
function toDateInputValue(date: string) { return date.replace(/\//g, '-') }
function fromDateInputValue(date: string) { return date.replace(/-/g, '/') }
function formatYen(amount: number) { return `¥${new Intl.NumberFormat('ja-JP').format(Math.round(amount))}` }
