import { useEffect, useMemo, useState, type FormEvent } from 'react'
import {
  CarFront,
  Archive,
  ChevronRight,
  CircleDollarSign,
  Eye,
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
import { downloadSalesDocumentPdf, previewSalesDocumentPdf } from '../lib/pdf'
import {
  createSalesDocument,
  archiveSalesDocument,
  fetchSalesDocuments,
  updateSalesDocument,
  type SalesDocument,
  type SalesDocumentType,
  type SalesLineItem,
} from '../lib/salesApi'
import { defaultSettings, fetchSettings, type AppSettings } from '../lib/settingsApi'

type DocumentFilter = 'すべて' | SalesDocumentType
type SalesDocumentView = 'edit' | 'preview'
type SalesHeaderField = 'number' | 'type' | 'status' | 'customerId' | 'vehicleId' | 'issuedAt' | 'dueDate' | 'note'
type SalesItemField = 'itemType' | 'description' | 'quantity' | 'unit' | 'unitPrice' | 'taxCategory' | 'otherAmount' | 'summary'
const salesLineItemTypes = ['車両本体価格', '付属品・特別仕様', '取付工賃', '値引き', '自動車税', '重量税', '自賠責保険', '環境性能割', '車庫証明費用', '登録費用', '納車費用', '下取車', 'リサイクル料金', '頭金', '残金', 'その他']

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
  const [documentView, setDocumentView] = useState<SalesDocumentView>('edit')

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

  function updateLineItem(itemId: string, field: SalesItemField, value: string) {
    if (!selectedDocument) return
    const nextValue = field === 'description' || field === 'itemType' || field === 'unit' || field === 'taxCategory' || field === 'summary' ? value : Number(value)
    setDocuments((current) => current.map((document) => document.id !== selectedDocument.id ? document : { ...document, items: document.items.map((item) => item.id === itemId ? { ...item, [field]: nextValue } : item) }))
    setDirty(true)
    setSaved(false)
  }

  function addLineItem() {
    if (!selectedDocument) return
    const newItem: SalesLineItem = { id: `item-${Date.now()}`, itemType: 'その他', description: '', quantity: 1, unit: '式', unitPrice: 0, taxCategory: '課税', otherAmount: 0, summary: '' }
    setDocuments((current) => current.map((document) => document.id === selectedDocument.id ? { ...document, items: [...document.items, newItem] } : document))
    setDirty(true)
    setSaved(false)
  }

  function updateHeader(field: SalesHeaderField, value: string) {
    if (!selectedDocument) return
    const nextCustomer = customers.find((customer) => customer.id === (field === 'customerId' ? value : selectedDocument.customerId))
    const nextVehicleId = field === 'customerId' ? nextCustomer?.vehicles[0]?.id ?? null : field === 'vehicleId' ? value || null : selectedDocument.vehicleId
    const nextVehicle = nextCustomer?.vehicles.find((vehicle) => vehicle.id === nextVehicleId)
    const relationPatch = field === 'customerId' || field === 'vehicleId' ? { customerName: nextCustomer?.name ?? '', phone: nextCustomer?.phone ?? '', vehicleId: nextVehicleId, vehicle: nextVehicle ? `${nextVehicle.maker} ${nextVehicle.model}` : '', plate: nextVehicle?.plate ?? '' } : {}
    setDocuments((current) => current.map((document) => document.id !== selectedDocument.id ? document : { ...document, [field]: value, ...relationPatch }))
    setDirty(true)
    setSaved(false)
  }

  async function archiveSelectedDocument() {
    if (!selectedDocument || saving) return
    if (!window.confirm(`${selectedDocument.number}をアーカイブしますか？`)) return
    setSaving(true)
    setSyncError('')
    try {
      await archiveSalesDocument(selectedDocument.id)
      setDocuments((current) => current.filter((document) => document.id !== selectedDocument.id))
      setSelectedDocumentId('')
      setDirty(false)
      setSaved(false)
    } catch (error: unknown) {
      setSyncError(error instanceof Error ? error.message : '販売書類をアーカイブできませんでした。')
    } finally {
      setSaving(false)
    }
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
      <div className="sales-toolbar"><label className="sales-search"><Search size={18} /><span className="sr-only">販売書類を検索</span><input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="書類番号、顧客名、車名で検索" /></label><div className="sales-filter-tabs" aria-label="書類種別"><button className={filterType === 'すべて' ? 'is-active' : ''} type="button" onClick={() => setFilterType('すべて')}>すべて</button>{(['見積書', '注文書', '請求書'] as SalesDocumentType[]).map((type) => <button className={filterType === type ? 'is-active' : ''} key={type} type="button" onClick={() => setFilterType(type)}>{type}</button>)}</div></div>
    <div className="sales-workspace"><SalesDocumentList documents={filteredDocuments} selectedDocumentId={selectedDocument?.id ?? ''} rounding={settings.tax.rounding} onSelect={setSelectedDocumentId} />{selectedDocument && selectedTotals ? <SalesDocumentDetail document={selectedDocument} totals={selectedTotals} shopName={settings.shop.name} settings={settings} itemPresets={settings.salesItemPresets} customers={customers} view={documentView} dirty={dirty} saving={saving} saved={saved} onViewChange={setDocumentView} onUpdateHeader={updateHeader} onUpdateItem={updateLineItem} onAddItem={addLineItem} onRemoveItem={removeLineItem} onSave={saveSelectedDocument} onArchive={() => void archiveSelectedDocument()} onPdfDownload={() => void downloadSalesDocumentPdf(selectedDocument, settings)} onPdfPreview={() => void previewSalesDocumentPdf(selectedDocument, settings)} /> : <div className="panel sales-empty"><FileText size={30} /><strong>{loading ? '販売書類を読み込んでいます' : '販売書類が見つかりません'}</strong><span>{loading ? 'しばらくお待ちください。' : '検索条件または書類種別を変更してください。'}</span></div>}</div>
      {createDialogOpen && <SalesDocumentDialog form={createForm} customers={customers} creating={creating} onChange={setCreateForm} onClose={() => setCreateDialogOpen(false)} onSubmit={createDocument} />}
    </>
  )
}

function SalesDocumentList({ documents, selectedDocumentId, rounding, onSelect }: { documents: SalesDocument[]; selectedDocumentId: string; rounding: AppSettings['tax']['rounding']; onSelect: (id: string) => void }) {
  return <section className="panel sales-list-panel"><div className="sales-list-header"><div><h2>販売書類</h2><span>書類を選択すると詳細を表示します</span></div><span className="results-count">{documents.length}件</span></div><div className="sales-document-list">{documents.map((document) => <button className={`sales-document-card${document.id === selectedDocumentId ? ' is-selected' : ''}`} key={document.id} type="button" onClick={() => onSelect(document.id)}><div className="sales-card-top"><span className="sales-card-customer"><UserRound size={14} /><strong>{document.customerName}</strong></span><ChevronRight size={16} /></div><span className="sales-card-vehicle"><CarFront size={14} />{document.vehicle || '車両未指定'}{document.plate ? ` ・ ${document.plate}` : ''}</span><div className="sales-card-bottom"><span>{document.issuedAt}</span><strong>{formatYen(calculateTotals(document, rounding).total)}</strong></div></button>)}</div></section>
}

function SalesDocumentDetail({ document, totals, shopName, settings, itemPresets, customers, view, dirty, saving, saved, onViewChange, onUpdateHeader, onUpdateItem, onAddItem, onRemoveItem, onSave, onArchive, onPdfDownload, onPdfPreview }: { document: SalesDocument; totals: SalesTotals; shopName: string; settings: AppSettings; itemPresets: string[]; customers: Customer[]; view: SalesDocumentView; dirty: boolean; saving: boolean; saved: boolean; onViewChange: (view: SalesDocumentView) => void; onUpdateHeader: (field: SalesHeaderField, value: string) => void; onUpdateItem: (itemId: string, field: SalesItemField, value: string) => void; onAddItem: () => void; onRemoveItem: (itemId: string) => void; onSave: () => void; onArchive: () => void; onPdfDownload: () => void; onPdfPreview: () => void }) {
  return <section className="panel sales-detail-panel"><div className="sales-detail-header"><div className="sales-detail-title"><div><span className={`sales-type-badge sales-type-${document.type}`}>{document.type}</span><h2>{document.number}</h2><small>{document.issuedAt} 作成 ・ 発行元 {shopName}</small></div><StatusTag status={document.status} /></div><div className="sales-detail-actions"><button className="button button-secondary" type="button" disabled={!dirty || saving} onClick={onSave}><Save size={16} />{saving ? '保存中…' : saved ? '保存済み' : '保存'}</button><button className="button button-secondary" type="button" onClick={onPdfDownload}><FileDown size={16} />PDF保存</button><button className="button button-danger" type="button" disabled={saving} onClick={onArchive}><Archive size={16} />アーカイブ</button></div></div><div className="sales-document-tabs" role="tablist" aria-label="販売書類の表示"><button id="sales-document-edit-tab" className={view === 'edit' ? 'is-active' : ''} type="button" role="tab" aria-selected={view === 'edit'} aria-controls="sales-document-edit-panel" onClick={() => onViewChange('edit')}><FileText size={16} />入力</button><button id="sales-document-preview-tab" className={view === 'preview' ? 'is-active' : ''} type="button" role="tab" aria-selected={view === 'preview'} aria-controls="sales-document-preview-panel" onClick={() => onViewChange('preview')}><Eye size={16} />プレビュー</button></div>{view === 'edit' ? <div id="sales-document-edit-panel" className="sales-detail-content" role="tabpanel" aria-labelledby="sales-document-edit-tab"><SalesDocumentEditor document={document} totals={totals} itemPresets={itemPresets} customers={customers} onUpdateHeader={onUpdateHeader} onUpdateItem={onUpdateItem} onAddItem={onAddItem} onRemoveItem={onRemoveItem} /></div> : <div id="sales-document-preview-panel" className="sales-detail-content" role="tabpanel" aria-labelledby="sales-document-preview-tab"><SalesDocumentPreview document={document} totals={totals} settings={settings} itemPresets={itemPresets} customers={customers} onUpdateHeader={onUpdateHeader} onUpdateItem={onUpdateItem} onAddItem={onAddItem} onRemoveItem={onRemoveItem} onPdfPreview={onPdfPreview} /></div>}</section>
}

function SalesDocumentEditor({ document, totals, itemPresets, customers, onUpdateHeader, onUpdateItem, onAddItem, onRemoveItem }: { document: SalesDocument; totals: SalesTotals; itemPresets: string[]; customers: Customer[]; onUpdateHeader: (field: SalesHeaderField, value: string) => void; onUpdateItem: (itemId: string, field: SalesItemField, value: string) => void; onAddItem: () => void; onRemoveItem: (itemId: string) => void }) {
  const selectedCustomer = customers.find((customer) => customer.id === document.customerId)
  return <><section className="document-header-editor"><div className="document-header-editor-title"><div><h3>書類ヘッダー</h3><span>番号、顧客・車両、日付、状態、備考を編集できます。</span></div></div><div className="form-grid"><label className="form-field"><span>書類番号</span><input value={document.number} onChange={(event) => onUpdateHeader('number', event.target.value)} /></label><label className="form-field"><span>書類種別</span><select value={document.type} onChange={(event) => onUpdateHeader('type', event.target.value)}><option>見積書</option><option>注文書</option><option>請求書</option></select></label><label className="form-field"><span>状態</span><select value={document.status} onChange={(event) => onUpdateHeader('status', event.target.value)}><option>下書き</option><option>発行済み</option><option>入金待ち</option></select></label><label className="form-field"><span>顧客</span><select value={document.customerId} onChange={(event) => onUpdateHeader('customerId', event.target.value)}>{customers.map((customer) => <option key={customer.id} value={customer.id}>{customer.name}</option>)}</select></label><label className="form-field"><span>対象車両</span><select value={document.vehicleId ?? ''} onChange={(event) => onUpdateHeader('vehicleId', event.target.value)}><option value="">車両を指定しない</option>{selectedCustomer?.vehicles.map((vehicle) => <option key={vehicle.id} value={vehicle.id}>{vehicle.maker} {vehicle.model} ・ {vehicle.plate || '登録番号なし'}</option>)}</select></label><label className="form-field"><span>書類日付</span><input type="date" value={document.issuedAt.replaceAll('/', '-')} onChange={(event) => onUpdateHeader('issuedAt', event.target.value.replaceAll('-', '/'))} /></label><label className="form-field"><span>支払期限</span><input type="date" value={document.dueDate.replaceAll('/', '-')} onChange={(event) => onUpdateHeader('dueDate', event.target.value.replaceAll('-', '/'))} /></label><label className="form-field"><span>備考</span><textarea value={document.note} onChange={(event) => onUpdateHeader('note', event.target.value)} /></label></div></section><div className="sales-context-grid"><div className="sales-context-card"><span className="sales-context-label"><UserRound size={15} />顧客</span><strong>{document.customerName}</strong><small>{document.phone || '電話番号未登録'}</small></div><div className="sales-context-card"><span className="sales-context-label"><CarFront size={15} />対象車両</span><strong>{document.vehicle || '車両未指定'}</strong><small>{document.plate || '登録番号未登録'}</small></div></div><SalesLineItemsEditor document={document} itemPresets={itemPresets} onUpdateItem={onUpdateItem} onAddItem={onAddItem} onRemoveItem={onRemoveItem} /><SalesSummary document={document} totals={totals} /></>
}

function SalesLineItemsEditor({ document, itemPresets, onUpdateItem, onAddItem, onRemoveItem }: { document: SalesDocument; itemPresets: string[]; onUpdateItem: (itemId: string, field: SalesItemField, value: string) => void; onAddItem: () => void; onRemoveItem: (itemId: string) => void }) {
  return <section className="sales-items-panel"><div className="sales-items-header"><div><h3>販売明細</h3><span>車両本体・付属品・諸費用・値引きを構造化して登録します</span></div><button className="text-button" type="button" onClick={onAddItem}><Plus size={15} />明細を追加</button></div><div className="sales-items-table"><div className="sales-items-head"><span>種別</span><span>内容</span><span>数量</span><span>単位</span><span>単価</span><span>金額</span><span /></div>{document.items.map((item) => <div className="sales-item-row" key={item.id}><select aria-label="明細種別" value={item.itemType} onChange={(event) => onUpdateItem(item.id, 'itemType', event.target.value)}>{salesLineItemTypes.map((type) => <option key={type}>{type}</option>)}</select><input list="sales-item-presets" aria-label="明細内容" value={item.description} onChange={(event) => onUpdateItem(item.id, 'description', event.target.value)} placeholder="明細内容" /><input className="sales-number-input" aria-label="数量" type="number" min="0" value={item.quantity} onChange={(event) => onUpdateItem(item.id, 'quantity', event.target.value)} /><input className="sales-unit-input" aria-label="単位" value={item.unit} onChange={(event) => onUpdateItem(item.id, 'unit', event.target.value)} /><input className="sales-price-input" aria-label="単価" type="number" value={item.unitPrice} onChange={(event) => onUpdateItem(item.id, 'unitPrice', event.target.value)} /><strong>{formatYen(item.quantity * item.unitPrice)}</strong><button className="sales-item-remove" type="button" aria-label="明細を削除" onClick={() => onRemoveLineItemGuard(item.id, document.items.length, onRemoveItem)}><Trash2 size={15} /></button></div>)}</div><datalist id="sales-item-presets">{itemPresets.map((preset) => <option key={preset} value={preset} />)}</datalist></section>
}

function SalesSummary({ document, totals }: { document: SalesDocument; totals: SalesTotals }) {
  return <><div className="sales-summary-grid"><div className="sales-note"><span>備考</span><p>{document.note || '備考はありません。'}</p></div><div className="sales-totals"><div><span>小計</span><strong>{formatYen(totals.subtotal)}</strong></div><div><span>消費税（{document.taxRate * 100}%）</span><strong>{formatYen(totals.tax)}</strong></div><div className="sales-total-row"><span>合計金額</span><strong>{formatYen(totals.total)}</strong></div></div></div><div className="sales-detail-footer"><span><ShoppingCart size={15} />支払期限：{document.dueDate || '未設定'}</span><span><CircleDollarSign size={15} />入金状況は入金管理で登録</span></div></>
}

function SalesDocumentPreview({ document, totals, settings, itemPresets, customers, onUpdateHeader, onUpdateItem, onAddItem, onRemoveItem, onPdfPreview }: { document: SalesDocument; totals: SalesTotals; settings: AppSettings; itemPresets: string[]; customers: Customer[]; onUpdateHeader: (field: SalesHeaderField, value: string) => void; onUpdateItem: (itemId: string, field: SalesItemField, value: string) => void; onAddItem: () => void; onRemoveItem: (itemId: string) => void; onPdfPreview: () => void }) {
  const selectedCustomer = customers.find((customer) => customer.id === document.customerId)
  const selectedVehicle = selectedCustomer?.vehicles.find((vehicle) => vehicle.id === document.vehicleId)
  const customerPhone = selectedCustomer?.phone ?? document.phone
  const vehicleName = selectedVehicle ? `${selectedVehicle.maker} ${selectedVehicle.model}` : document.vehicle
  const vehiclePlate = selectedVehicle?.plate ?? document.plate
  const shopLines = [
    settings.shop.postalCode ? `〒${settings.shop.postalCode}` : '',
    settings.shop.address,
    settings.shop.phone ? `TEL ${settings.shop.phone}` : '',
    settings.shop.representative ? `担当 ${settings.shop.representative}` : '',
    settings.shop.registrationNumber ? `登録番号 ${settings.shop.registrationNumber}` : '',
  ].filter(Boolean)
  const paymentNote = settings.document.paymentNote || '店頭または指定口座へお支払いください。'
  const bankAccount = [settings.shop.bankName, settings.shop.bankAccount].filter(Boolean).join(' / ') || '未設定'
  return (
    <div className="sales-preview-area">
      <div className="sales-preview-toolbar"><div><strong>帳票プレビュー</strong><span>PDFと同じ帳票レイアウトで確認・入力できます。</span></div><button className="button button-secondary" type="button" onClick={onPdfPreview}><Eye size={16} />PDFで確認</button></div>
      <article className="sales-document-paper">
        <header className="sales-paper-header">
          <div className="sales-paper-heading"><select className="sales-paper-type" aria-label="書類種別" value={document.type} onChange={(event) => onUpdateHeader('type', event.target.value)}><option>見積書</option><option>注文書</option><option>請求書</option></select></div>
          <div className="sales-paper-header-side">
            <label className="sales-paper-number"><span>No.</span><input aria-label="書類番号" value={document.number} onChange={(event) => onUpdateHeader('number', event.target.value)} /></label>
            <strong>{settings.shop.name || '店舗名未設定'}</strong>
            <label className="sales-paper-issued"><span>発行日</span><input type="date" aria-label="発行日" value={document.issuedAt.replaceAll('/', '-')} onChange={(event) => onUpdateHeader('issuedAt', event.target.value.replaceAll('-', '/'))} /></label>
            {shopLines.slice(0, 4).map((line) => <small key={line}>{line}</small>)}
          </div>
        </header>
        <div className="sales-paper-rule" />
        <div className="sales-paper-info-grid">
          <div className="sales-paper-info-box"><span>顧客</span><select aria-label="顧客" value={document.customerId} onChange={(event) => onUpdateHeader('customerId', event.target.value)}>{customers.map((customer) => <option key={customer.id} value={customer.id}>{customer.name}</option>)}</select><small>{customerPhone || '電話番号未登録'}</small></div>
          <div className="sales-paper-info-box"><span>対象車両</span><select aria-label="対象車両" value={document.vehicleId ?? ''} onChange={(event) => onUpdateHeader('vehicleId', event.target.value)}><option value="">車両を指定しない</option>{selectedCustomer?.vehicles.map((vehicle) => <option key={vehicle.id} value={vehicle.id}>{vehicle.maker} {vehicle.model}{vehicle.plate ? `（${vehicle.plate}）` : ''}</option>)}</select><small>{vehicleName || '車両未指定'}{vehiclePlate ? ` ・ ${vehiclePlate}` : ''}</small></div>
        </div>
        <div className="sales-paper-meta">
          <label className="sales-paper-meta-field"><span>書類日付</span><input type="date" aria-label="書類日付" value={document.issuedAt.replaceAll('/', '-')} onChange={(event) => onUpdateHeader('issuedAt', event.target.value.replaceAll('-', '/'))} /></label>
          <label className="sales-paper-meta-field"><span>支払期限</span><input type="date" aria-label="支払期限" value={document.dueDate.replaceAll('/', '-')} onChange={(event) => onUpdateHeader('dueDate', event.target.value.replaceAll('-', '/'))} /></label>
          <label className="sales-paper-meta-field"><span>状態</span><select aria-label="状態" value={document.status} onChange={(event) => onUpdateHeader('status', event.target.value)}><option>下書き</option><option>発行済み</option><option>入金待ち</option></select></label>
          <div className="sales-paper-meta-field"><span>消費税・端数</span><strong>{formatPercent(document.taxRate)} / {settings.tax.rounding}</strong></div>
        </div>
        <div className="sales-paper-section-heading"><h3>販売明細</h3><span className="sales-paper-section-line" /><button className="text-button" type="button" onClick={onAddItem}><Plus size={15} />明細を追加</button></div>
        <div className="sales-paper-items-table">
          <div className="sales-paper-items-head"><span>区分</span><span>内容</span><span>数量</span><span>単位</span><span>単価</span><span>金額</span></div>
          {document.items.map((item) => <div className="sales-paper-item-row" key={item.id}><select aria-label="プレビューの明細区分" value={item.itemType} onChange={(event) => onUpdateItem(item.id, 'itemType', event.target.value)}>{salesLineItemTypes.map((type) => <option key={type}>{type}</option>)}</select><input list="sales-paper-item-presets" aria-label="プレビューの明細内容" value={item.description} onChange={(event) => onUpdateItem(item.id, 'description', event.target.value)} placeholder="明細内容" /><input className="sales-number-input" type="number" min="0" aria-label="プレビューの数量" value={item.quantity} onChange={(event) => onUpdateItem(item.id, 'quantity', event.target.value)} /><input className="sales-unit-input" aria-label="プレビューの単位" value={item.unit} onChange={(event) => onUpdateItem(item.id, 'unit', event.target.value)} /><input className="sales-price-input" type="number" aria-label="プレビューの単価" value={item.unitPrice} onChange={(event) => onUpdateItem(item.id, 'unitPrice', event.target.value)} /><strong>{formatYen(item.quantity * item.unitPrice)}</strong><button className="sales-item-remove" type="button" aria-label="明細を削除" onClick={() => onRemoveLineItemGuard(item.id, document.items.length, onRemoveItem)}><Trash2 size={15} /></button></div>)}
        </div>
        <datalist id="sales-paper-item-presets">{itemPresets.map((preset) => <option key={preset} value={preset} />)}</datalist>
        <div className="sales-paper-totals-box"><div><span>小計</span><strong>{formatYen(totals.subtotal)}</strong></div><div><span>消費税（{formatPercent(document.taxRate)}）</span><strong>{formatYen(totals.tax)}</strong></div><div className="sales-paper-total-row"><span>合計金額</span><strong>{formatYen(totals.total)}</strong></div></div>
        <div className="sales-paper-notes">
          <label className="sales-paper-note-row"><span>備考</span><textarea value={document.note} onChange={(event) => onUpdateHeader('note', event.target.value)} placeholder="備考を入力" /></label>
          <div className="sales-paper-note-row"><span>お支払いについて</span><p>{paymentNote}</p></div>
          <div className="sales-paper-note-row"><span>振込先</span><p>{bankAccount}</p></div>
        </div>
        <footer className="sales-paper-footer"><span>{settings.document.footerNote || '車両管理システム'}</span><span>ページ 1</span></footer>
      </article>
    </div>
  )
}

function onRemoveLineItemGuard(itemId: string, itemCount: number, onRemove: (itemId: string) => void) {
  if (itemCount <= 1) return
  onRemove(itemId)
}

function StatusTag({ status }: { status: SalesDocument['status'] }) {
  const tone = status === '入金待ち' ? 'warning' : status === '発行済み' ? 'normal' : status === 'アーカイブ済み' ? 'danger' : 'draft'
  return <span className={`sales-status-tag sales-status-${tone}`}><span className="status-dot" />{status}</span>
}

type SalesTotals = { subtotal: number; tax: number; total: number }

function calculateTotals(document: SalesDocument, rounding: AppSettings['tax']['rounding']): SalesTotals {
  const subtotal = document.items.reduce((sum, item) => sum + calculateLineAmount(item), 0)
  const taxableSubtotal = document.items.filter((item) => item.taxCategory === '課税').reduce((sum, item) => sum + calculateLineAmount(item), 0)
  const taxValue = Math.max(0, taxableSubtotal) * document.taxRate
  const tax = rounding === '四捨五入' ? Math.round(taxValue) : Math.floor(taxValue)
  return { subtotal, tax, total: subtotal + tax }
}

function formatYen(amount: number) {
  return `¥${new Intl.NumberFormat('ja-JP').format(Math.round(amount))}`
}

function calculateLineAmount(item: SalesLineItem) {
  return Math.round(item.quantity * item.unitPrice + item.otherAmount)
}

function formatPercent(value: number) {
  return `${Number.isInteger(value * 100) ? value * 100 : (value * 100).toFixed(2)}%`
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
