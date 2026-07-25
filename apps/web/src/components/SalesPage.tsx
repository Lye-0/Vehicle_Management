import { useEffect, useMemo, useState, type FormEvent } from 'react'
import {
  CarFront,
  ChevronRight,
  CircleDollarSign,
  FileDown,
  FileText,
  Plus,
  Save,
  Search,
  ShoppingCart,
  Trash2,
  UserRound,
  X,
} from 'lucide-react'
import { fetchCustomers, type Customer } from '../lib/customerApi'
import { printDocument } from '../lib/print'
import {
  createSalesDocument,
  fetchSalesDocuments,
  updateSalesDocument,
  type SalesDocument,
  type SalesDocumentType,
  type SalesLineItem,
} from '../lib/salesApi'
import { defaultSettings, fetchSettings, type AppSettings } from '../lib/settingsApi'

type DocumentFilter = 'すべて' | SalesDocumentType

type SalesCreateForm = {
  type: SalesDocumentType
  customerId: string
  vehicleId: string
  dueDate: string
  note: string
  taxRate: number
  taxRounding: '切り捨て' | '四捨五入'
  initialItemDescription: string
}

export function SalesPage() {
  const [documents, setDocuments] = useState<SalesDocument[]>([])
  const [customers, setCustomers] = useState<Customer[]>([])
  const [settings, setSettings] = useState<AppSettings>(defaultSettings)
  const [query, setQuery] = useState('')
  const [filterType, setFilterType] = useState<DocumentFilter>('すべて')
  const [selectedDocumentId, setSelectedDocumentId] = useState('')
  const [createDialogOpen, setCreateDialogOpen] = useState(false)
  const [createForm, setCreateForm] = useState<SalesCreateForm>(emptyCreateForm())
  const [loading, setLoading] = useState(true)
  const [syncError, setSyncError] = useState('')
  const [saving, setSaving] = useState(false)
  const [creating, setCreating] = useState(false)
  const [dirty, setDirty] = useState(false)
  const [saved, setSaved] = useState(false)

  useEffect(() => {
    let cancelled = false
    setLoading(true)
    Promise.all([fetchSalesDocuments(), fetchCustomers(), fetchSettings()])
      .then(([nextDocuments, nextCustomers, nextSettings]) => {
        if (cancelled) return
        setDocuments(nextDocuments)
        setCustomers(nextCustomers)
        setSettings(nextSettings)
        setSelectedDocumentId((current) => nextDocuments.some((document) => document.id === current) ? current : nextDocuments[0]?.id ?? '')
        setSyncError('')
      })
      .catch((error: unknown) => {
        if (!cancelled) setSyncError(error instanceof Error ? error.message : '販売書類を読み込めませんでした。')
      })
      .finally(() => {
        if (!cancelled) setLoading(false)
      })
    return () => { cancelled = true }
  }, [])

  const filteredDocuments = useMemo(() => {
    const normalizedQuery = query.trim().toLocaleLowerCase()
    return documents.filter((document) => {
      const matchesType = filterType === 'すべて' || document.type === filterType
      const searchableText = `${document.number} ${document.customerName} ${document.vehicle} ${document.plate}`.toLocaleLowerCase()
      return matchesType && (!normalizedQuery || searchableText.includes(normalizedQuery))
    })
  }, [documents, filterType, query])

  const selectedDocument = filteredDocuments.find((document) => document.id === selectedDocumentId) ?? filteredDocuments[0] ?? null
  const selectedTotals = selectedDocument ? calculateTotals(selectedDocument, settings.tax.rounding) : null

  function updateLineItem(itemId: string, field: 'description' | 'quantity' | 'unitPrice', value: string) {
    if (!selectedDocument) return
    const nextValue = field === 'description' ? value : Number(value)
    setDocuments((current) => current.map((document) => document.id !== selectedDocument.id ? document : { ...document, items: document.items.map((item) => item.id === itemId ? { ...item, [field]: nextValue } : item) }))
    setDirty(true)
    setSaved(false)
  }

  function addLineItem() {
    if (!selectedDocument) return
    const newItem: SalesLineItem = { id: `item-${Date.now()}`, description: '', quantity: 1, unit: '式', unitPrice: 0 }
    setDocuments((current) => current.map((document) => document.id === selectedDocument.id ? { ...document, items: [...document.items, newItem] } : document))
    setDirty(true)
    setSaved(false)
  }

  function removeLineItem(itemId: string) {
    if (!selectedDocument) return
    setDocuments((current) => current.map((document) => document.id === selectedDocument.id ? { ...document, items: document.items.filter((item) => item.id !== itemId) } : document))
    setDirty(true)
    setSaved(false)
  }

  async function saveSelectedDocument() {
    if (!selectedDocument || saving) return
    setSaving(true)
    setSaved(false)
    try {
      const nextDocument = await updateSalesDocument(selectedDocument, settings.tax.rounding)
      setDocuments((current) => current.map((document) => document.id === nextDocument.id ? nextDocument : document))
      setDirty(false)
      setSaved(true)
      setSyncError('')
    } catch (error: unknown) {
      setSyncError(error instanceof Error ? error.message : '販売書類を保存できませんでした。')
    } finally {
      setSaving(false)
    }
  }

  function openCreateDialog() {
    const customer = customers[0]
    setCreateForm({ type: '見積書', customerId: customer?.id ?? '', vehicleId: customer?.vehicles[0]?.id ?? '', dueDate: dateAfter(settings.document.defaultDueDays), note: '', taxRate: settings.tax.consumptionTaxRate, taxRounding: settings.tax.rounding, initialItemDescription: settings.salesItemPresets[0] ?? '車両本体価格' })
    setCreateDialogOpen(true)
  }

  async function createDocument(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (creating || !createForm.customerId) return
    setCreating(true)
    try {
      const newDocument = await createSalesDocument({ ...createForm, vehicleId: createForm.vehicleId || null })
      setDocuments((current) => [newDocument, ...current])
      setSelectedDocumentId(newDocument.id)
      setCreateDialogOpen(false)
      setDirty(false)
      setSaved(false)
      setSyncError('')
    } catch (error: unknown) {
      setSyncError(error instanceof Error ? error.message : '販売書類を作成できませんでした。')
    } finally {
      setCreating(false)
    }
  }

  return (
    <>
      <div className="page-header sales-page-header"><div><span className="page-eyebrow">販売書類</span><h1>販売</h1><p>見積書・注文書・請求書を車両情報と連動して管理します。</p></div><button className="button button-primary" type="button" onClick={openCreateDialog}><Plus size={18} />販売書類を作成</button></div>
      {syncError && <div className="customer-sync-status is-error"><span>{syncError}</span><button className="text-button" type="button" onClick={() => window.location.reload()}>再読み込み</button></div>}
      {loading && <div className="customer-sync-status"><span>販売書類を読み込んでいます。</span></div>}
      <div className="sales-toolbar"><label className="sales-search"><Search size={18} /><span className="sr-only">販売書類を検索</span><input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="書類番号、顧客名、車名で検索" /></label><div className="sales-filter-tabs" aria-label="書類種別"><button className={filterType === 'すべて' ? 'is-active' : ''} type="button" onClick={() => setFilterType('すべて')}>すべて</button>{(['見積書', '注文書', '請求書'] as SalesDocumentType[]).map((type) => <button className={filterType === type ? 'is-active' : ''} key={type} type="button" onClick={() => setFilterType(type)}>{type}</button>)}</div><span className="sales-result-summary"><strong>{filteredDocuments.length}件</strong><span>販売書類</span></span></div>
    <div className="sales-workspace"><SalesDocumentList documents={filteredDocuments} selectedDocumentId={selectedDocument?.id ?? ''} rounding={settings.tax.rounding} onSelect={setSelectedDocumentId} />{selectedDocument && selectedTotals ? <SalesDocumentDetail document={selectedDocument} totals={selectedTotals} shopName={settings.shop.name} itemPresets={settings.salesItemPresets} dirty={dirty} saving={saving} saved={saved} onUpdateItem={updateLineItem} onAddItem={addLineItem} onRemoveItem={removeLineItem} onSave={saveSelectedDocument} onPrint={() => printDocument(`${selectedDocument.number}-${selectedDocument.type}`)} /> : <div className="panel sales-empty"><FileText size={30} /><strong>{loading ? '販売書類を読み込んでいます' : '販売書類が見つかりません'}</strong><span>{loading ? 'しばらくお待ちください。' : '検索条件または書類種別を変更してください。'}</span></div>}</div>
      {createDialogOpen && <SalesDocumentDialog form={createForm} customers={customers} creating={creating} onChange={setCreateForm} onClose={() => setCreateDialogOpen(false)} onSubmit={createDocument} />}
    </>
  )
}

function SalesDocumentList({ documents, selectedDocumentId, rounding, onSelect }: { documents: SalesDocument[]; selectedDocumentId: string; rounding: AppSettings['tax']['rounding']; onSelect: (id: string) => void }) {
  return <section className="panel sales-list-panel"><div className="sales-list-header"><div><h2>販売書類</h2><span>書類を選択すると詳細を表示します</span></div><span className="results-count">{documents.length}件</span></div><div className="sales-document-list">{documents.map((document) => <button className={`sales-document-card${document.id === selectedDocumentId ? ' is-selected' : ''}`} key={document.id} type="button" onClick={() => onSelect(document.id)}><div className="sales-card-top"><span className={`sales-type-badge sales-type-${document.type}`}>{document.type}</span><StatusTag status={document.status} /><ChevronRight size={16} /></div><strong className="sales-card-number">{document.number}</strong><span className="sales-card-customer"><UserRound size={14} />{document.customerName}</span><span className="sales-card-vehicle"><CarFront size={14} />{document.vehicle || '車両未指定'}{document.plate ? ` ・ ${document.plate}` : ''}</span><div className="sales-card-bottom"><span>{document.issuedAt}</span><strong>{formatYen(calculateTotals(document, rounding).total)}</strong></div></button>)}</div></section>
}

function SalesDocumentDetail({ document, totals, shopName, itemPresets, dirty, saving, saved, onUpdateItem, onAddItem, onRemoveItem, onSave, onPrint }: { document: SalesDocument; totals: SalesTotals; shopName: string; itemPresets: string[]; dirty: boolean; saving: boolean; saved: boolean; onUpdateItem: (itemId: string, field: 'description' | 'quantity' | 'unitPrice', value: string) => void; onAddItem: () => void; onRemoveItem: (itemId: string) => void; onSave: () => void; onPrint: () => void }) {
  return <section className="panel sales-detail-panel"><div className="sales-detail-header"><div className="sales-detail-title"><div><span className={`sales-type-badge sales-type-${document.type}`}>{document.type}</span><h2>{document.number}</h2><small>{document.issuedAt} 作成 ・ 発行元 {shopName}</small></div><StatusTag status={document.status} /></div><div className="sales-detail-actions"><button className="button button-secondary" type="button" disabled={!dirty || saving} onClick={onSave}><Save size={16} />{saving ? '保存中…' : saved ? '保存済み' : '保存'}</button><button className="button button-secondary" type="button" onClick={onPrint}><FileDown size={16} />PDF出力</button></div></div><div className="sales-detail-content"><div className="sales-context-grid"><div className="sales-context-card"><span className="sales-context-label"><UserRound size={15} />顧客</span><strong>{document.customerName}</strong><small>{document.phone || '電話番号未登録'}</small></div><div className="sales-context-card"><span className="sales-context-label"><CarFront size={15} />対象車両</span><strong>{document.vehicle || '車両未指定'}</strong><small>{document.plate || '登録番号未登録'}</small></div></div><section className="sales-items-panel"><div className="sales-items-header"><div><h3>販売明細</h3><span>車両本体・付属品・諸費用・値引きを登録します</span></div><button className="text-button" type="button" onClick={onAddItem}><Plus size={15} />明細を追加</button></div><div className="sales-items-table"><div className="sales-items-head"><span>内容</span><span>数量</span><span>単位</span><span>単価</span><span>金額</span><span /></div>{document.items.map((item) => <div className="sales-item-row" key={item.id}><input list="sales-item-presets" aria-label="明細内容" value={item.description} onChange={(event) => onUpdateItem(item.id, 'description', event.target.value)} placeholder="明細内容" /><input className="sales-number-input" aria-label="数量" type="number" min="0" value={item.quantity} onChange={(event) => onUpdateItem(item.id, 'quantity', event.target.value)} /><span>{item.unit}</span><input className="sales-price-input" aria-label="単価" type="number" value={item.unitPrice} onChange={(event) => onUpdateItem(item.id, 'unitPrice', event.target.value)} /><strong>{formatYen(item.quantity * item.unitPrice)}</strong><button className="sales-item-remove" type="button" aria-label="明細を削除" onClick={() => onRemoveLineItemGuard(item.id, document.items.length, onRemoveItem)}><Trash2 size={15} /></button></div>)}</div><datalist id="sales-item-presets">{itemPresets.map((preset) => <option key={preset} value={preset} />)}</datalist></section><div className="sales-summary-grid"><div className="sales-note"><span>備考</span><p>{document.note || '備考はありません。'}</p></div><div className="sales-totals"><div><span>小計</span><strong>{formatYen(totals.subtotal)}</strong></div><div><span>消費税（{document.taxRate * 100}%）</span><strong>{formatYen(totals.tax)}</strong></div><div className="sales-total-row"><span>合計金額</span><strong>{formatYen(totals.total)}</strong></div></div></div><div className="sales-detail-footer"><span><ShoppingCart size={15} />支払期限：{document.dueDate || '未設定'}</span><span><CircleDollarSign size={15} />入金状況は入金管理で登録</span></div></div></section>
}

function onRemoveLineItemGuard(itemId: string, itemCount: number, onRemove: (itemId: string) => void) {
  if (itemCount <= 1) return
  onRemove(itemId)
}

function StatusTag({ status }: { status: SalesDocument['status'] }) {
  const tone = status === '入金待ち' ? 'warning' : status === '発行済み' ? 'normal' : 'draft'
  return <span className={`sales-status-tag sales-status-${tone}`}><span className="status-dot" />{status}</span>
}

type SalesTotals = { subtotal: number; tax: number; total: number }

function calculateTotals(document: SalesDocument, rounding: AppSettings['tax']['rounding']): SalesTotals {
  const subtotal = document.items.reduce((sum, item) => sum + item.quantity * item.unitPrice, 0)
  const taxValue = Math.max(0, subtotal) * document.taxRate
  const tax = rounding === '四捨五入' ? Math.round(taxValue) : Math.floor(taxValue)
  return { subtotal, tax, total: subtotal + tax }
}

function formatYen(amount: number) {
  return `¥${new Intl.NumberFormat('ja-JP').format(Math.round(amount))}`
}

function SalesDocumentDialog({ form, customers, creating, onChange, onClose, onSubmit }: { form: SalesCreateForm; customers: Customer[]; creating: boolean; onChange: (form: SalesCreateForm) => void; onClose: () => void; onSubmit: (event: FormEvent<HTMLFormElement>) => void }) {
  const selectedCustomer = customers.find((customer) => customer.id === form.customerId)
  const vehicles = selectedCustomer?.vehicles ?? []
  return <div className="modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose() }}><section className="modal" role="dialog" aria-modal="true" aria-labelledby="sales-modal-title"><div className="modal-header"><h2 id="sales-modal-title">販売書類を作成</h2><button className="modal-close" type="button" aria-label="閉じる" onClick={onClose}><X size={19} /></button></div><form className="modal-form" onSubmit={onSubmit}><p className="modal-description"><FileText size={16} />顧客・車両を選択して、下書きの販売書類を作成します。</p><div className="form-grid"><label className="form-field"><span>書類種別<em>必須</em></span><select required value={form.type} onChange={(event) => onChange({ ...form, type: event.target.value as SalesDocumentType })}><option>見積書</option><option>注文書</option><option>請求書</option></select></label><label className="form-field"><span>顧客<em>必須</em></span><select required value={form.customerId} onChange={(event) => { const customer = customers.find((item) => item.id === event.target.value); onChange({ ...form, customerId: event.target.value, vehicleId: customer?.vehicles[0]?.id ?? '' }) }}><option value="" disabled>顧客を選択してください</option>{customers.map((customer) => <option key={customer.id} value={customer.id}>{customer.name}（{customer.phone || '電話番号未登録'}）</option>)}</select></label><label className="form-field"><span>対象車両</span><select value={form.vehicleId} onChange={(event) => onChange({ ...form, vehicleId: event.target.value })}><option value="">車両を指定しない</option>{vehicles.map((vehicle) => <option key={vehicle.id} value={vehicle.id}>{vehicle.maker} {vehicle.model}{vehicle.plate ? `（${vehicle.plate}）` : ''}</option>)}</select></label><label className="form-field"><span>支払期限</span><input type="date" value={form.dueDate} onChange={(event) => onChange({ ...form, dueDate: event.target.value })} /></label></div><label className="form-field"><span>備考</span><textarea value={form.note} onChange={(event) => onChange({ ...form, note: event.target.value })} placeholder="販売書類に関するメモ" /></label><div className="modal-footer"><button className="button button-secondary" type="button" onClick={onClose}>キャンセル</button><button className="button button-primary" type="submit" disabled={creating || customers.length === 0}><Plus size={16} />{creating ? '作成中…' : '作成する'}</button></div></form></section></div>
}

function emptyCreateForm(): SalesCreateForm {
  return { type: '見積書', customerId: '', vehicleId: '', dueDate: dateAfter(14), note: '', taxRate: 10, taxRounding: '切り捨て', initialItemDescription: '車両本体価格' }
}

function dateAfter(days: number) {
  const date = new Date()
  date.setDate(date.getDate() + days)
  return date.toISOString().slice(0, 10)
}
