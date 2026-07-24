import { useMemo, useState } from 'react'
import {
  Banknote,
  CalendarDays,
  CarFront,
  CheckCircle2,
  ChevronRight,
  CircleDollarSign,
  FileDown,
  FileText,
  Pencil,
  Plus,
  Search,
  UserRound,
  WalletCards,
} from 'lucide-react'

type PaymentMethod = '現金' | '銀行振込' | 'クレジットカード' | 'その他' | ''
type PaymentStatus = '未入金' | '一部入金' | '入金済み'
type PaymentFilter = 'すべて' | PaymentStatus

type PaymentRecord = {
  id: string
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

const initialPaymentRecords: PaymentRecord[] = [
  {
    id: 'payment-sales-041', number: 'S-2026-041', sourceType: '販売請求書', customerName: '佐藤 太郎', phone: '090-1234-5678', vehicle: 'トヨタ プリウス', plate: '品川 500 あ 1234', issuedAt: '2026/07/25', dueDate: '2026/08/08', invoiceAmount: 1280000, paidAmount: 0, paymentDate: '', method: '', note: '',
  },
  {
    id: 'payment-maintenance-118', number: 'M-2026-118', sourceType: '整備請求書', customerName: '伊藤 雄介', phone: '080-5678-9012', vehicle: 'マツダ CX-5', plate: '川崎 501 お 7890', issuedAt: '2026/07/08', dueDate: '2026/07/18', invoiceAmount: 86420, paidAmount: 0, paymentDate: '', method: '', note: '車検整備一式。請求書を郵送済み。',
  },
  {
    id: 'payment-sales-039', number: 'S-2026-039', sourceType: '販売請求書', customerName: '山田 恵子', phone: '090-4567-8901', vehicle: 'スバル インプレッサ', plate: '多摩 500 え 3456', issuedAt: '2026/07/10', dueDate: '2026/08/09', invoiceAmount: 420000, paidAmount: 120000, paymentDate: '2026/07/20', method: '銀行振込', note: '残金は納車日に支払い予定。',
  },
  {
    id: 'payment-maintenance-108', number: 'M-2026-108', sourceType: '整備請求書', customerName: '鈴木 一郎', phone: '070-3456-7890', vehicle: 'ニッサン ノート', plate: '大宮 400 う 9012', issuedAt: '2026/07/17', dueDate: '2026/07/24', invoiceAmount: 62000, paidAmount: 62000, paymentDate: '2026/07/22', method: '現金', note: '店頭で受領。',
  },
]

export function PaymentsPage() {
  const [records, setRecords] = useState<PaymentRecord[]>(initialPaymentRecords)
  const [query, setQuery] = useState('')
  const [statusFilter, setStatusFilter] = useState<PaymentFilter>('すべて')
  const [selectedRecordId, setSelectedRecordId] = useState(initialPaymentRecords[0].id)
  const [saved, setSaved] = useState(false)

  const filteredRecords = useMemo(() => {
    const normalizedQuery = query.trim().toLocaleLowerCase()
    return records.filter((record) => {
      const matchesStatus = statusFilter === 'すべて' || getPaymentStatus(record) === statusFilter
      const searchableText = `${record.number} ${record.customerName} ${record.vehicle} ${record.plate}`.toLocaleLowerCase()
      return matchesStatus && (!normalizedQuery || searchableText.includes(normalizedQuery))
    })
  }, [query, records, statusFilter])

  const selectedRecord = filteredRecords.find((record) => record.id === selectedRecordId) ?? filteredRecords[0] ?? null
  const totalOutstanding = filteredRecords.reduce((sum, record) => sum + getOutstandingAmount(record), 0)

  function updateSelected(patch: Partial<PaymentRecord>) {
    if (!selectedRecord) return
    setRecords((current) => current.map((record) => record.id === selectedRecord.id ? { ...record, ...patch } : record))
    setSaved(false)
  }

  function focusUnpaidRecord() {
    const firstUnpaid = records.find((record) => getPaymentStatus(record) === '未入金')
    setStatusFilter('未入金')
    if (firstUnpaid) setSelectedRecordId(firstUnpaid.id)
  }

  return (
    <>
      <div className="page-header payment-page-header">
        <div><span className="page-eyebrow">請求・入金</span><h1>入金管理</h1><p>請求ごとの現在の入金情報を登録し、未入金額を確認します。</p></div>
        <button className="button button-primary" type="button" onClick={focusUnpaidRecord}><Plus size={18} />入金を登録</button>
      </div>
      <div className="payment-toolbar">
        <label className="payment-search"><Search size={18} /><span className="sr-only">入金情報を検索</span><input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="請求書番号、顧客名、車名で検索" /></label>
        <div className="payment-filter-tabs" aria-label="入金状態">
          {(['すべて', '未入金', '一部入金', '入金済み'] as PaymentFilter[]).map((filter) => <button className={statusFilter === filter ? 'is-active' : ''} key={filter} type="button" aria-pressed={statusFilter === filter} onClick={() => setStatusFilter(filter)}>{filter}</button>)}
        </div>
        <span className="payment-result-summary"><strong>{filteredRecords.length}件</strong><span>請求</span><em>未入金 {formatYen(totalOutstanding)}</em></span>
      </div>
      <div className="payment-workspace">
        <PaymentRecordList records={filteredRecords} selectedRecordId={selectedRecord?.id ?? ''} onSelect={(id) => { setSelectedRecordId(id); setSaved(false) }} />
        {selectedRecord ? <PaymentRecordDetail record={selectedRecord} saved={saved} onUpdate={updateSelected} onSave={() => setSaved(true)} /> : <div className="panel payment-empty"><WalletCards size={30} /><strong>請求が見つかりません</strong><span>検索条件または入金状態を変更してください。</span></div>}
      </div>
    </>
  )
}

function PaymentRecordList({ records, selectedRecordId, onSelect }: { records: PaymentRecord[]; selectedRecordId: string; onSelect: (id: string) => void }) {
  return <section className="panel payment-list-panel"><div className="payment-list-header"><div><h2>請求一覧</h2><span>請求を選択すると現在の入金情報を表示します</span></div><span className="results-count">{records.length}件</span></div><div className="payment-record-list">{records.map((record) => { const status = getPaymentStatus(record); return <button className={`payment-record-card${record.id === selectedRecordId ? ' is-selected' : ''}`} key={record.id} type="button" onClick={() => onSelect(record.id)}><div className="payment-card-top"><span className={`payment-type-badge payment-type-${record.sourceType === '販売請求書' ? 'sales' : 'maintenance'}`}>{record.sourceType}</span><PaymentStatusTag status={status} /><ChevronRight size={16} /></div><strong className="payment-card-number">{record.number}</strong><span className="payment-card-customer"><UserRound size={14} />{record.customerName}</span><span className="payment-card-vehicle"><CarFront size={14} />{record.vehicle} ・ {record.plate}</span><div className="payment-card-bottom"><span>未入金 {formatYen(getOutstandingAmount(record))}</span><strong className={isOverdue(record) && status !== '入金済み' ? 'is-overdue' : ''}>{status === '入金済み' ? '入金済み' : `期限 ${record.dueDate}`}</strong></div></button> })}</div></section>
}

function PaymentRecordDetail({ record, saved, onUpdate, onSave }: { record: PaymentRecord; saved: boolean; onUpdate: (patch: Partial<PaymentRecord>) => void; onSave: () => void }) {
  const status = getPaymentStatus(record)
  const outstanding = getOutstandingAmount(record)

  return <section className="panel payment-detail-panel"><div className="payment-detail-header"><div className="payment-detail-title"><div><span className={`payment-type-badge payment-type-${record.sourceType === '販売請求書' ? 'sales' : 'maintenance'}`}>{record.sourceType}</span><h2>{record.number}</h2><small>{record.issuedAt} 作成</small></div><PaymentStatusTag status={status} /></div><div className="payment-detail-actions"><button className="button button-secondary" type="button"><Pencil size={16} />編集</button><button className="button button-secondary" type="button"><FileDown size={16} />PDF出力</button></div></div><div className="payment-detail-content"><div className="payment-context-grid"><div className="payment-context-card"><span className="payment-context-label"><UserRound size={15} />顧客</span><strong>{record.customerName}</strong><small>{record.phone}</small></div><div className="payment-context-card"><span className="payment-context-label"><CarFront size={15} />対象車両</span><strong>{record.vehicle}</strong><small>{record.plate}</small></div></div><section className="payment-amount-panel"><div className="payment-amount-header"><div><h3>請求金額と入金状況</h3><span>請求に対する現在の入金額を確認します</span></div><span className="payment-amount-icon"><CircleDollarSign size={20} /></span></div><div className="payment-amount-grid"><PaymentAmount label="請求金額" value={record.invoiceAmount} /><PaymentAmount label="入金済み" value={record.paidAmount} tone="paid" /><PaymentAmount label="未入金額" value={outstanding} tone={outstanding > 0 ? 'outstanding' : 'paid'} emphasized /></div></section><section className="payment-form-panel"><div className="payment-form-header"><div><h3>現在の入金情報</h3><span>この請求に対する最新の入金内容を登録します</span></div><Banknote size={21} /></div><div className="payment-form-grid"><label className="form-field"><span>今回までの入金額<em>必須</em></span><div className="payment-input-with-prefix"><span>¥</span><input type="number" min="0" max={record.invoiceAmount} value={record.paidAmount} onChange={(event) => { const amount = Math.min(record.invoiceAmount, Math.max(0, Number(event.target.value) || 0)); onUpdate({ paidAmount: amount }) }} /></div></label><label className="form-field"><span>入金日</span><input type="date" value={toDateInputValue(record.paymentDate)} onChange={(event) => onUpdate({ paymentDate: fromDateInputValue(event.target.value) })} /></label><label className="form-field"><span>入金方法</span><select value={record.method} onChange={(event) => onUpdate({ method: event.target.value as PaymentMethod })}><option value="">選択してください</option><option>現金</option><option>銀行振込</option><option>クレジットカード</option><option>その他</option></select></label></div><label className="form-field payment-note-field"><span>メモ</span><textarea value={record.note} onChange={(event) => onUpdate({ note: event.target.value })} placeholder="入金に関するメモ" /></label><div className="payment-save-row"><span className="payment-current-note"><CalendarDays size={15} />入金履歴は作成せず、請求ごとの現在情報を管理します</span><div>{saved && <span className="payment-saved"><CheckCircle2 size={15} />保存しました</span>}<button className="button button-primary" type="button" onClick={onSave}><WalletCards size={16} />入金情報を保存</button></div></div></section><div className="payment-detail-footer"><span><FileText size={15} />請求書発行日：{record.issuedAt}</span><span><CalendarDays size={15} />支払期限：{record.dueDate}{isOverdue(record) && status !== '入金済み' ? '（期限超過）' : ''}</span></div></div></section>
}

function PaymentAmount({ label, value, tone = 'normal', emphasized = false }: { label: string; value: number; tone?: 'normal' | 'paid' | 'outstanding'; emphasized?: boolean }) {
  return <div className={`payment-amount-item payment-amount-${tone}${emphasized ? ' is-emphasized' : ''}`}><span>{label}</span><strong>{formatYen(value)}</strong></div>
}

function PaymentStatusTag({ status }: { status: PaymentStatus }) {
  const tone = status === '入金済み' ? 'paid' : status === '一部入金' ? 'partial' : 'unpaid'
  return <span className={`payment-status-tag payment-status-${tone}`}><span className="status-dot" />{status}</span>
}

function getPaymentStatus(record: PaymentRecord): PaymentStatus {
  if (record.paidAmount >= record.invoiceAmount && record.invoiceAmount > 0) return '入金済み'
  if (record.paidAmount > 0) return '一部入金'
  return '未入金'
}

function getOutstandingAmount(record: PaymentRecord) {
  return Math.max(record.invoiceAmount - record.paidAmount, 0)
}

function isOverdue(record: PaymentRecord) {
  return new Date(record.dueDate.replace(/\//g, '-')).getTime() < Date.now()
}

function toDateInputValue(date: string) {
  return date.replace(/\//g, '-')
}

function fromDateInputValue(date: string) {
  return date.replace(/-/g, '/')
}

function formatYen(amount: number) {
  return `¥${new Intl.NumberFormat('ja-JP').format(Math.round(amount))}`
}
