import { useEffect, useMemo, useState } from 'react'
import {
  AlertTriangle,
  Banknote,
  CalendarDays,
  CarFront,
  CheckCircle2,
  ChevronRight,
  CircleDollarSign,
  FileDown,
  FileText,
  Info,
  Pencil,
  Plus,
  Search,
  Trash2,
  UserRound,
  WalletCards,
  X,
} from 'lucide-react'
import { addPaymentEntry, deletePaymentEntry, fetchPayments, updatePaymentEntry, type PaymentDocumentStatus, type PaymentEntry, type PaymentEntryInput, type PaymentMethod, type PaymentRecord } from '../lib/paymentsApi'
import { printDocument } from '../lib/print'
import type { VehicleHistoryNavigation } from './CustomerVehiclePage'
import { compareSortableDocuments, type DocumentSortDirection, type DocumentSortKey } from './DocumentSort'
import { DocumentSortControls } from './DocumentSortControls'

type PaymentStatus = '未入金' | '一部入金' | '入金済み'
type PaymentFilter = 'すべて' | PaymentStatus
type InvoiceType = PaymentRecord['sourceType']
type InvoiceTypeFilter = 'すべて' | InvoiceType
type PaymentInputMethod = Exclude<PaymentMethod, ''>

export function PaymentsPage({ initialRecordId, onNavigate }: { initialRecordId?: string; onNavigate?: (target: VehicleHistoryNavigation) => void } = {}) {
  const [records, setRecords] = useState<PaymentRecord[]>([])
  const [query, setQuery] = useState('')
  const [sortKey, setSortKey] = useState<DocumentSortKey>('dueDate')
  const [sortDirection, setSortDirection] = useState<DocumentSortDirection>('asc')
  const [statusFilter, setStatusFilter] = useState<PaymentFilter>('すべて')
  const [invoiceTypeFilter, setInvoiceTypeFilter] = useState<InvoiceTypeFilter>('すべて')
  const [selectedRecordId, setSelectedRecordId] = useState(initialRecordId ?? '')
  const [saved, setSaved] = useState(false)
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState('')

  useEffect(() => {
    let cancelled = false
    fetchPayments()
      .then((nextRecords) => { if (!cancelled) { const pendingRecords = nextRecords.filter((record) => record.documentStatus === '入金待ち'); setRecords(pendingRecords); setSelectedRecordId(initialRecordId && pendingRecords.some((record) => record.id === initialRecordId) ? initialRecordId : ''); setError('') } })
      .catch((reason: unknown) => { if (!cancelled) setError(reason instanceof Error ? reason.message : '入金データを読み込めませんでした。') })
      .finally(() => { if (!cancelled) setLoading(false) })
    return () => { cancelled = true }
  }, [initialRecordId])

  const filteredRecords = useMemo(() => {
    const normalizedQuery = query.trim().toLocaleLowerCase()
    return records.filter((record) => {
      const matchesStatus = statusFilter === 'すべて' || getPaymentStatus(record) === statusFilter
      const matchesInvoiceType = invoiceTypeFilter === 'すべて' || record.sourceType === invoiceTypeFilter
      const searchableText = `${record.number} ${record.customerName} ${record.vehicle} ${record.plate} ${record.sourceType} ${record.documentStatus}`.toLocaleLowerCase()
      return matchesStatus && matchesInvoiceType && (!normalizedQuery || searchableText.includes(normalizedQuery))
    }).sort((left, right) => compareSortableDocuments(left, right, sortKey, sortDirection))
  }, [invoiceTypeFilter, query, records, sortDirection, sortKey, statusFilter])

  const selectedRecord = filteredRecords.find((record) => record.id === selectedRecordId) ?? filteredRecords[0] ?? null

  async function saveEntry(entryId: string | undefined, input: PaymentEntryInput) {
    if (!selectedRecord) return false
    setSaving(true)
    setError('')
    try {
      const savedRecord = entryId ? await updatePaymentEntry(selectedRecord, entryId, input) : await addPaymentEntry(selectedRecord, input)
      setRecords((current) => current.map((record) => record.id === savedRecord.id ? savedRecord : record).filter((record) => record.documentStatus === '入金待ち'))
      setSelectedRecordId(savedRecord.id)
      setSaved(true)
      return true
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : '入金履歴を保存できませんでした。')
      setSaved(false)
      return false
    } finally {
      setSaving(false)
    }
  }

  async function removeEntry(entryId: string) {
    if (!selectedRecord) return false
    setSaving(true)
    setError('')
    try {
      const savedRecord = await deletePaymentEntry(selectedRecord, entryId)
      setRecords((current) => current.map((record) => record.id === savedRecord.id ? savedRecord : record).filter((record) => record.documentStatus === '入金待ち'))
      setSaved(false)
      return true
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : '入金履歴を削除できませんでした。')
      return false
    } finally {
      setSaving(false)
    }
  }

  function focusUnpaidRecord() {
    const firstUnpaid = records.find((record) => record.documentStatus === '入金待ち' && getPaymentStatus(record) === '未入金')
    setStatusFilter('未入金')
    setInvoiceTypeFilter('すべて')
    if (firstUnpaid) setSelectedRecordId(firstUnpaid.id)
  }

  function resetFilters() {
    setStatusFilter('すべて')
    setInvoiceTypeFilter('すべて')
  }

  return <>
    <div className="page-header payment-page-header"><div><span className="page-eyebrow">請求・入金</span><h1>入金管理</h1><p>請求ごとの入金履歴を登録し、未入金額を確認します。</p></div><button className="button button-primary" type="button" disabled={!records.length} onClick={focusUnpaidRecord}><Plus size={18} />入金を登録</button></div>
    {error && <div className="customer-sync-status is-error"><span>{error}</span><button className="text-button" type="button" onClick={() => window.location.reload()}>再読み込み</button></div>}
    {loading && <div className="customer-sync-status"><span>請求・入金データを読み込んでいます。</span></div>}
    <div className="payment-scope-note"><Info size={17} /><div><strong>表示対象</strong><span>販売タブの請求書と、車検・点検・一般タブの整備請求書のうち、書類状態が「入金待ち」のものだけを表示しています。</span></div></div>
    <div className="payment-toolbar"><label className="payment-search"><Search size={18} /><span className="sr-only">入金情報を検索</span><input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="請求書番号、顧客名、車名で検索" /></label><DocumentSortControls sortKey={sortKey} sortDirection={sortDirection} onSortKeyChange={setSortKey} onSortDirectionChange={setSortDirection} /></div>
    <div className="payment-filter-panel"><PaymentFilterGroup label="請求書の種類" kind="invoice-type" value={invoiceTypeFilter} options={['すべて', '販売請求書', '整備請求書'] as InvoiceTypeFilter[]} onChange={setInvoiceTypeFilter} /><PaymentFilterGroup label="入金状況" kind="payment-status" value={statusFilter} options={['すべて', '未入金', '一部入金', '入金済み'] as PaymentFilter[]} onChange={setStatusFilter} /><button className="text-button payment-filter-reset" type="button" onClick={resetFilters} disabled={statusFilter === 'すべて' && invoiceTypeFilter === 'すべて'}>条件をリセット</button></div>
    <div className="payment-workspace"><PaymentRecordList records={filteredRecords} selectedRecordId={selectedRecord?.id ?? ''} onSelect={(id) => { setSelectedRecordId(id); setSaved(false) }} />{selectedRecord ? <PaymentRecordDetail key={selectedRecord.id} record={selectedRecord} saved={saved} saving={saving} onSaveEntry={saveEntry} onDeleteEntry={removeEntry} onOpenDocument={onNavigate ? () => onNavigate({ section: selectedRecord.sourceType === '販売請求書' ? 'sales' : 'maintenance', recordId: selectedRecord.documentId }) : undefined} onPdf={() => printDocument(`${selectedRecord.number}-${selectedRecord.sourceType}`)} /> : <div className="panel payment-empty"><WalletCards size={30} /><strong>請求が見つかりません</strong><span>{loading ? '読み込み中です。' : '検索条件または入金状況を変更してください。'}</span></div>}</div>
  </>
}

function PaymentRecordList({ records, selectedRecordId, onSelect }: { records: PaymentRecord[]; selectedRecordId: string; onSelect: (id: string) => void }) {
  return <section className="panel payment-list-panel"><div className="payment-list-header"><div><h2>請求一覧</h2><span>請求を選択すると入金履歴と現在の残額を表示します</span></div><span className="results-count">{records.length}件</span></div><div className="payment-record-list">{records.map((record) => { const status = getPaymentStatus(record); return <button className={`payment-record-card${record.id === selectedRecordId ? ' is-selected' : ''}`} key={record.id} type="button" onClick={() => onSelect(record.id)}><div className="payment-card-top"><span className={`payment-type-badge payment-type-${record.sourceType === '販売請求書' ? 'sales' : 'maintenance'}`}>{record.sourceType}</span><DocumentStatusTag status={record.documentStatus} /><PaymentStatusTag status={status} /><ChevronRight size={16} /></div><strong className="payment-card-number">{record.number}</strong><span className="payment-card-customer"><UserRound size={14} />{record.customerName}</span><span className="payment-card-vehicle"><CarFront size={14} />{record.vehicle} ・ {record.plate}</span><div className="payment-card-bottom"><span>未入金 {formatYen(getOutstandingAmount(record))}</span><strong className={isOverdue(record) && status !== '入金済み' ? 'is-overdue' : ''}>{status === '入金済み' ? '入金済み' : `期限 ${record.dueDate || '未設定'}`}</strong></div></button> })}</div></section>
}

function PaymentRecordDetail({ record, saved, saving, onSaveEntry, onDeleteEntry, onOpenDocument, onPdf }: { record: PaymentRecord; saved: boolean; saving: boolean; onSaveEntry: (entryId: string | undefined, input: PaymentEntryInput) => Promise<boolean>; onDeleteEntry: (entryId: string) => Promise<boolean>; onOpenDocument?: () => void; onPdf: () => void }) {
  const status = getPaymentStatus(record)
  const outstanding = getOutstandingAmount(record)
  const [amount, setAmount] = useState('')
  const [paymentDate, setPaymentDate] = useState(todayDate())
  const [method, setMethod] = useState<PaymentMethod>('')
  const [note, setNote] = useState('')
  const editingEntryId = ''
  const maxAmount = outstanding

  function resetForm() {
    setAmount('')
    setPaymentDate(todayDate())
    setMethod('')
    setNote('')
  }

  function startEditing(entry: PaymentEntry) {
    return (input: PaymentEntryInput) => onSaveEntry(entry.id, input)
  }

  async function submitEntry(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!method) return
    const parsedAmount = Math.round(Number(amount))
    if (!Number.isFinite(parsedAmount) || parsedAmount <= 0 || parsedAmount > maxAmount) return
    const succeeded = await onSaveEntry(undefined, { amount: parsedAmount, paymentDate, method: method as PaymentInputMethod, note })
    if (succeeded) resetForm()
  }

  async function removeEntry(entry: PaymentEntry) {
    await onDeleteEntry(entry.id)
  }

  return <section className="panel payment-detail-panel"><div className="payment-detail-header"><div className="payment-detail-title"><div><div className="payment-detail-badges"><span className={`payment-type-badge payment-type-${record.sourceType === '販売請求書' ? 'sales' : 'maintenance'}`}>{record.sourceType}</span><DocumentStatusTag status={record.documentStatus} /></div><h2>{record.number}</h2><small>{record.issuedAt} 作成</small></div><PaymentStatusTag status={status} /></div><div className="payment-detail-actions">{onOpenDocument && <button className="button button-secondary" type="button" onClick={onOpenDocument}><FileText size={16} />書類を開く</button>}<button className="button button-secondary" type="button" onClick={onPdf}><FileDown size={16} />PDF出力</button></div></div><div className="payment-detail-content"><div className="payment-context-grid"><div className="payment-context-card"><span className="payment-context-label"><UserRound size={15} />顧客</span><strong>{record.customerName}</strong><small>{record.phone}</small></div><div className="payment-context-card"><span className="payment-context-label"><CarFront size={15} />対象車両</span><strong>{record.vehicle || '車両未指定'}</strong><small>{record.plate}</small></div></div><section className="payment-amount-panel"><div className="payment-amount-header"><div><h3>請求金額と入金状況</h3><span>入金履歴の合計から現在の残額を計算します</span></div><span className="payment-amount-icon"><CircleDollarSign size={20} /></span></div><div className="payment-amount-grid"><PaymentAmount label="請求金額" value={record.invoiceAmount} /><PaymentAmount label="入金済み" value={record.paidAmount} tone="paid" /><PaymentAmount label="未入金額" value={outstanding} tone={outstanding > 0 ? 'outstanding' : 'paid'} emphasized /></div></section><section className="payment-form-panel payment-entry-form-panel"><div className="payment-form-header"><div><h3>{editingEntryId ? '入金履歴を編集' : '今回の入金を登録'}</h3><span>{editingEntryId ? '入金履歴の内容を修正します' : '入金のたびに1件ずつ登録します'}</span></div><Banknote size={21} /></div><form onSubmit={(event) => void submitEntry(event)}><div className="payment-form-grid"><label className="form-field"><span>入金額<em>必須</em></span><PaymentAmountInput value={amount} onChange={setAmount} required placeholder="0" /></label><label className="form-field"><span>入金日<em>必須</em></span><input type="date" required value={toDateInputValue(paymentDate)} onChange={(event) => setPaymentDate(fromDateInputValue(event.target.value))} /></label><label className="form-field"><span>入金方法<em>必須</em></span><select required value={method} onChange={(event) => setMethod(event.target.value as PaymentMethod)}><option value="">選択してください</option><option>現金</option><option>銀行振込</option><option>クレジットカード</option><option>その他</option></select></label></div><label className="form-field payment-note-field"><span>メモ</span><textarea value={note} onChange={(event) => setNote(event.target.value)} placeholder="入金に関するメモ" /></label><div className="payment-entry-submit-row"><span className="payment-current-note"><CalendarDays size={15} />{outstanding > 0 ? `残り ${formatYen(maxAmount)}` : '請求額の入金が完了しています'}</span><div><button className="button button-secondary" type="button" onClick={resetForm} disabled={!editingEntryId && !amount && !method && !note}>{editingEntryId ? <><X size={15} />キャンセル</> : '入力をクリア'}</button><button className="button button-primary" type="submit" disabled={saving || (!editingEntryId && outstanding <= 0)}>{saving ? '保存中…' : editingEntryId ? '履歴を更新' : '入金を追加'}</button></div></div>{saved && <span className="payment-saved"><CheckCircle2 size={15} />保存しました</span>}</form></section><PaymentHistory entries={record.paymentHistory} onEdit={startEditing} onDelete={(entry) => void removeEntry(entry)} /><div className="payment-detail-footer"><span><FileText size={15} />請求書発行日：{record.issuedAt}</span><span><CalendarDays size={15} />支払期限：{record.dueDate || '未設定'}{isOverdue(record) && status !== '入金済み' ? '（期限超過）' : ''}</span></div></div></section>
}

function PaymentHistory({ entries, onEdit, onDelete }: { entries: PaymentEntry[]; onEdit: (entry: PaymentEntry) => (input: PaymentEntryInput) => Promise<boolean>; onDelete: (entry: PaymentEntry) => Promise<void> | void }) {
  const [editingEntry, setEditingEntry] = useState<PaymentEntry | null>(null)
  const [deleteEntry, setDeleteEntry] = useState<PaymentEntry | null>(null)
  const [saveEditingEntry, setSaveEditingEntry] = useState<((input: PaymentEntryInput) => Promise<boolean>) | null>(null)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState('')

  function openEdit(entry: PaymentEntry) {
    setEditingEntry(entry)
    setSaveEditingEntry(() => onEdit(entry))
    setError('')
  }

  function closeEdit() {
    if (saving) return
    setEditingEntry(null)
    setSaveEditingEntry(null)
    setError('')
  }

  async function saveEdit(input: PaymentEntryInput) {
    if (!saveEditingEntry) return false
    setSaving(true)
    setError('')
    try {
      const succeeded = await saveEditingEntry(input)
      if (succeeded) {
        setEditingEntry(null)
        setSaveEditingEntry(null)
      } else {
        setError('入金履歴を更新できませんでした。')
      }
      return succeeded
    } finally {
      setSaving(false)
    }
  }

  async function confirmDelete() {
    if (!deleteEntry) return
    setSaving(true)
    try {
      await onDelete(deleteEntry)
      setDeleteEntry(null)
    } finally {
      setSaving(false)
    }
  }

  return <>
    <section className="payment-history-panel"><div className="payment-history-header"><div><h3>入金履歴</h3><span>この請求に対する入金記録を新しい順に表示しています</span></div><span className="results-count">{entries.length}件</span></div>{entries.length ? <div className="payment-history-list">{entries.map((entry) => <article className="payment-history-entry" key={entry.id}><div className="payment-history-entry-main"><div><strong>{formatYen(entry.amount)}</strong><span>{entry.paymentDate || '入金日未登録'}</span></div><span className="payment-history-method">{entry.method || '入金方法未登録'}</span></div>{entry.note && <p>{entry.note}</p>}<div className="payment-history-actions"><button className="text-button" type="button" onClick={() => openEdit(entry)}><Pencil size={13} />編集</button><button className="text-button payment-history-delete" type="button" onClick={() => setDeleteEntry(entry)}><Trash2 size={13} />削除</button></div></article>)}</div> : <div className="payment-history-empty"><CalendarDays size={23} /><span>まだ入金履歴がありません。</span></div>}</section>
    {editingEntry && <PaymentEntryEditModal entry={editingEntry} maxAmount={editingEntry.amount} saving={saving} error={error} onClose={closeEdit} onSave={saveEdit} />}
    {deleteEntry && <PaymentEntryDeleteModal entry={deleteEntry} saving={saving} onClose={() => { if (!saving) setDeleteEntry(null) }} onConfirm={() => void confirmDelete()} />}
  </>
}

function PaymentEntryEditModal({ entry, maxAmount, saving, error, onClose, onSave }: { entry: PaymentEntry; maxAmount: number; saving: boolean; error: string; onClose: () => void; onSave: (input: PaymentEntryInput) => Promise<boolean> }) {
  const [amount, setAmount] = useState(String(entry.amount))
  const [paymentDate, setPaymentDate] = useState(entry.paymentDate ?? todayDate())
  const [method, setMethod] = useState<PaymentMethod>(entry.method)
  const [note, setNote] = useState(entry.note)

  async function submit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!method) return
    const parsedAmount = Math.round(Number(amount))
    if (!Number.isFinite(parsedAmount) || parsedAmount <= 0) return
    if (parsedAmount > maxAmount) return
    await onSave({ amount: parsedAmount, paymentDate, method: method as PaymentInputMethod, note })
  }

  return <div className="modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget && !saving) onClose() }}><section className="modal payment-entry-modal" role="dialog" aria-modal="true" aria-labelledby="payment-entry-edit-title"><div className="modal-header"><div><span className="payment-modal-eyebrow">入金履歴</span><h2 id="payment-entry-edit-title">入金履歴を編集</h2></div><button className="modal-close" type="button" aria-label="編集を閉じる" disabled={saving} onClick={onClose}><X size={19} /></button></div><form className="modal-form" onSubmit={(event) => void submit(event)}><p className="modal-description"><Pencil size={16} />入金額・入金日・入金方法・メモを編集できます。</p><div className="payment-form-grid"><label className="form-field"><span>入金額<em>必須</em></span><PaymentAmountInput autoFocus value={amount} onChange={setAmount} required /></label><label className="form-field"><span>入金日<em>必須</em></span><input type="date" required value={toDateInputValue(paymentDate)} onChange={(event) => setPaymentDate(fromDateInputValue(event.target.value))} /></label><label className="form-field"><span>入金方法<em>必須</em></span><select required value={method} onChange={(event) => setMethod(event.target.value as PaymentMethod)}><option value="">選択してください</option><option>現金</option><option>銀行振込</option><option>クレジットカード</option><option>その他</option></select></label></div><label className="form-field payment-note-field"><span>メモ</span><textarea value={note} onChange={(event) => setNote(event.target.value)} placeholder="入金に関するメモ" /></label>{error && <p className="payment-modal-error" role="alert">{error}</p>}<div className="modal-footer"><button className="button button-secondary" type="button" disabled={saving} onClick={onClose}>キャンセル</button><button className="button button-primary" type="submit" disabled={saving}>{saving ? '保存中…' : '変更を保存'}</button></div></form></section></div>
}

function PaymentEntryDeleteModal({ entry, saving, onClose, onConfirm }: { entry: PaymentEntry; saving: boolean; onClose: () => void; onConfirm: () => void }) {
  return <div className="modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget && !saving) onClose() }}><section className="modal payment-delete-modal" role="dialog" aria-modal="true" aria-labelledby="payment-delete-title"><div className="modal-header"><div><span className="payment-modal-eyebrow payment-modal-eyebrow-danger">入金履歴の削除</span><h2 id="payment-delete-title">この入金履歴を削除しますか？</h2></div><button className="modal-close" type="button" aria-label="削除確認を閉じる" disabled={saving} onClick={onClose}><X size={19} /></button></div><div className="payment-delete-body"><div className="payment-delete-summary"><span className="payment-delete-icon"><AlertTriangle size={20} /></span><div><strong>{formatYen(entry.amount)}</strong><span>{entry.paymentDate || '入金日未登録'} ・ {entry.method || '入金方法未登録'}</span></div></div><p className="payment-delete-message">この入金履歴を削除すると、請求書の入金済み金額と未入金額が再計算されます。</p><p className="payment-delete-warning">削除した履歴は元に戻せません。</p><div className="modal-footer"><button className="button button-secondary" type="button" disabled={saving} onClick={onClose}>キャンセル</button><button className="button button-danger" type="button" disabled={saving} onClick={onConfirm}><Trash2 size={15} />{saving ? '削除中…' : '削除する'}</button></div></div></section></div>
}

function PaymentAmount({ label, value, tone = 'normal', emphasized = false }: { label: string; value: number; tone?: 'normal' | 'paid' | 'outstanding'; emphasized?: boolean }) { return <div className={`payment-amount-item payment-amount-${tone}${emphasized ? ' is-emphasized' : ''}`}><span>{label}</span><strong>{formatYen(value)}</strong></div> }
function PaymentAmountInput({ value, onChange, autoFocus = false, required = false, placeholder }: { value: string; onChange: (value: string) => void; autoFocus?: boolean; required?: boolean; placeholder?: string }) {
  const [draft, setDraft] = useState(() => editablePaymentAmount(value))
  const [focused, setFocused] = useState(false)

  useEffect(() => {
    if (!focused) setDraft(editablePaymentAmount(value))
  }, [focused, value])

  function beginEdit() {
    setFocused(true)
    setDraft(editablePaymentAmount(value))
  }

  function handleChange(nextValue: string) {
    const nextDraft = editablePaymentAmount(nextValue)
    setDraft(nextDraft)
    onChange(nextDraft)
  }

  function finish() {
    const nextValue = editablePaymentAmount(draft)
    setFocused(false)
    setDraft(nextValue)
    if (nextValue !== value) onChange(nextValue)
  }

  return <input className="payment-amount-input" autoFocus={autoFocus} type="text" inputMode="numeric" required={required} value={focused ? draft : formatPaymentDraft(draft)} onFocus={beginEdit} onChange={(event) => handleChange(event.target.value)} onBlur={finish} placeholder={placeholder} />
}

function editablePaymentAmount(value: string) {
  return value.normalize('NFKC').replace(/[^\d]/g, '')
}

function formatPaymentDraft(value: string) {
  const editableValue = editablePaymentAmount(value)
  return editableValue ? formatYen(Number(editableValue)) : ''
}

type PaymentFilterGroupKind = 'invoice-type' | 'payment-status'

function PaymentFilterGroup<T extends string>({ label, kind, value, options, onChange }: { label: string; kind: PaymentFilterGroupKind; value: T; options: T[]; onChange: (value: T) => void }) { return <div className="payment-filter-group"><span className="payment-filter-label">{label}</span><div className={`payment-filter-options payment-filter-options-${kind}`} role="group" aria-label={label}>{options.map((option) => { const tone = getPaymentFilterTone(kind, option); return <button className={`payment-filter-option payment-filter-option-${tone}${value === option ? ' is-active' : ''}`} key={option} type="button" aria-pressed={value === option} onClick={() => onChange(option)}>{option}</button> })}</div></div> }

function getPaymentFilterTone(kind: PaymentFilterGroupKind, option: string) { if (option === 'すべて') return 'all'; if (kind === 'invoice-type') return option === '販売請求書' ? 'sales' : 'maintenance'; return option === '未入金' ? 'unpaid' : option === '一部入金' ? 'partial' : 'paid' }
function DocumentStatusTag({ status }: { status: PaymentDocumentStatus }) { const tone = status === '完了' ? 'completed' : 'pending'; return <span className={`payment-document-status payment-document-status-${tone}`}><span className="status-dot" />{status}</span> }
function PaymentStatusTag({ status }: { status: PaymentStatus }) { const tone = status === '入金済み' ? 'paid' : status === '一部入金' ? 'partial' : 'unpaid'; return <span className={`payment-status-tag payment-status-${tone}`}><span className="status-dot" />{status}</span> }
function getPaymentStatus(record: PaymentRecord): PaymentStatus { if (record.paidAmount >= record.invoiceAmount && record.invoiceAmount > 0) return '入金済み'; if (record.paidAmount > 0) return '一部入金'; return '未入金' }
function getOutstandingAmount(record: PaymentRecord) { return Math.max(record.invoiceAmount - record.paidAmount, 0) }
function isOverdue(record: PaymentRecord) { return Boolean(record.dueDate) && new Date(record.dueDate.replace(/\//g, '-')).getTime() < Date.now() }
function todayDate() { const date = new Date(); return `${date.getFullYear()}/${String(date.getMonth() + 1).padStart(2, '0')}/${String(date.getDate()).padStart(2, '0')}` }
function toDateInputValue(date: string) { return date.replace(/\//g, '-') }
function fromDateInputValue(date: string) { return date.replace(/-/g, '/') }
function formatYen(amount: number) { return `¥${new Intl.NumberFormat('ja-JP').format(Math.round(amount))}` }
