import { useEffect, useMemo, useState, type FormEvent } from 'react'
import {
  Archive,
  CarFront,
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
import { createCustomer, createVehicle, fetchCustomers, type Customer, type CustomerInput, type Vehicle, type VehicleInput } from '../lib/customerApi'
import { downloadSalesDocumentPdf, previewSalesDocumentPdf } from '../lib/pdf'
import {
  createSalesDocument,
  archiveSalesDocument,
  fetchSalesDocuments,
  updateSalesDocument,
  type SalesDocument,
  type SalesDocumentDetails,
  type SalesDocumentType,
  type SalesLineItem,
  type SalesTaxCategory,
} from '../lib/salesApi'
import { defaultSettings, fetchSettings, type AppSettings } from '../lib/settingsApi'

type DocumentFilter = 'すべて' | SalesDocumentType
type SalesDocumentView = 'edit' | 'preview'
type SalesHeaderField = 'number' | 'type' | 'status' | 'customerId' | 'vehicleId' | 'issuedAt' | 'dueDate' | 'note'
type SalesItemField = 'itemType' | 'description' | 'quantity' | 'unit' | 'unitPrice' | 'taxCategory' | 'otherAmount' | 'summary'
type SalesTaxCategoryField = keyof SalesDocumentDetails['requiredDocuments']

const salesLineItemTypes = ['車両本体価格', '付属品・特別仕様', '取付工賃', '値引き', '自動車税', '重量税', '自賠責保険', '環境性能割', '車庫証明費用', '登録費用', '納車費用', '下取車', 'リサイクル料金', '頭金', '残金', 'その他']
const salesTaxCategories: SalesTaxCategory[] = ['課税', '非課税', '対象外']
const requiredDocumentFields: Array<{ key: keyof SalesDocumentDetails['requiredDocuments']; label: string }> = [
  { key: 'sealCertificate', label: '印鑑証明' },
  { key: 'residentCard', label: '住民票' },
  { key: 'lightVehicleCertificate', label: '軽自動車住所証明' },
  { key: 'transferCertificate', label: '譲渡証明' },
  { key: 'taxPaymentCertificate', label: '納税証明（下取車）' },
  { key: 'warrantyCertificate', label: '保証書・承諾書' },
]

type SalesCreateForm = {
  type: SalesDocumentType
  customerId: string
  vehicleId: string
  dueDate: string
  taxRate: number
  taxRounding: '切り捨て' | '四捨五入'
  initialItemDescription: string
}

const emptySalesCustomerForm: CustomerInput = { name: '', kana: '', phone: '', email: '', postalCode: '', address: '', memo: '' }
const emptySalesVehicleForm: VehicleInput = { maker: '', model: '', modelType: '', plate: '', vin: '', year: '', inspectionDate: '', mileage: '', color: '', displacement: '', transmission: '', note: '', freeItem1: '', freeItem2: '', freeItem3: '' }

export function SalesPage({ initialDocumentId }: { initialDocumentId?: string } = {}) {
  const [documents, setDocuments] = useState<SalesDocument[]>([])
  const [customers, setCustomers] = useState<Customer[]>([])
  const [settings, setSettings] = useState<AppSettings>(defaultSettings)
  const [query, setQuery] = useState('')
  const [filterType, setFilterType] = useState<DocumentFilter>('すべて')
  const [selectedDocumentId, setSelectedDocumentId] = useState(initialDocumentId ?? '')
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
    setDocuments((current) => current.map((document) => document.id !== selectedDocument.id ? document : {
      ...document,
      items: document.items.map((item) => item.id === itemId ? { ...item, [field]: nextValue } : item),
    }))
    markDirty()
  }

  function addLineItem() {
    if (!selectedDocument) return
    const newItem: SalesLineItem = { id: `item-${Date.now()}`, itemType: 'その他', description: '', quantity: 1, unit: '式', unitPrice: 0, taxCategory: '課税', otherAmount: 0, summary: '' }
    setDocuments((current) => current.map((document) => document.id === selectedDocument.id ? { ...document, items: [...document.items, newItem] } : document))
    markDirty()
  }

  function updateHeader(field: SalesHeaderField, value: string) {
    if (!selectedDocument || field === 'number') return
    const nextCustomer = customers.find((customer) => customer.id === (field === 'customerId' ? value : selectedDocument.customerId))
    const nextVehicleId = field === 'customerId' ? nextCustomer?.vehicles[0]?.id ?? null : field === 'vehicleId' ? value || null : selectedDocument.vehicleId
    const nextVehicle = nextCustomer?.vehicles.find((vehicle) => vehicle.id === nextVehicleId)
    const relationChanged = field === 'customerId' || field === 'vehicleId'
    const relationPatch = relationChanged ? {
      customerName: nextCustomer?.name ?? '',
      phone: nextCustomer?.phone ?? '',
      vehicleId: nextVehicleId,
      vehicle: nextVehicle ? `${nextVehicle.maker} ${nextVehicle.model}`.trim() : '',
      plate: nextVehicle?.plate ?? '',
      customerDetails: nextCustomer ? mapCustomerDetails(nextCustomer) : emptyCustomerDetails(),
      vehicleDetails: nextVehicle ? mapVehicleDetails(nextVehicle) : null,
    } : {}
    setDocuments((current) => current.map((document) => document.id !== selectedDocument.id ? document : { ...document, [field]: value, ...relationPatch }))
    markDirty()
  }

  function updateDetails(patch: Partial<SalesDocumentDetails>) {
    if (!selectedDocument) return
    setDocuments((current) => current.map((document) => document.id !== selectedDocument.id ? document : { ...document, details: { ...document.details, ...patch } }))
    markDirty()
  }

  function updateTradeIn(field: keyof SalesDocumentDetails['tradeIn'], value: string) {
    if (!selectedDocument) return
    updateDetails({ tradeIn: { ...selectedDocument.details.tradeIn, [field]: value } })
  }

  function updateCredit(field: keyof SalesDocumentDetails['credit'], value: string | boolean) {
    if (!selectedDocument) return
    const nextValue = typeof value === 'boolean' || field === 'paymentCount' || field === 'bonusMonths' ? value : Number(value)
    updateDetails({ credit: { ...selectedDocument.details.credit, [field]: nextValue } })
  }

  function updateRequiredDocument(field: SalesTaxCategoryField, value: string | boolean) {
    if (!selectedDocument) return
    updateDetails({ requiredDocuments: { ...selectedDocument.details.requiredDocuments, [field]: value } })
  }

  function markDirty() {
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
    markDirty()
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
    setCreateForm({ type: '見積書', customerId: customer?.id ?? '', vehicleId: customer?.vehicles[0]?.id ?? '', dueDate: dateAfter(settings.document.defaultDueDays), taxRate: settings.tax.consumptionTaxRate, taxRounding: settings.tax.rounding, initialItemDescription: settings.salesItemPresets[0] ?? '車両本体価格' })
    setCreateDialogOpen(true)
  }

  async function registerCustomer(input: CustomerInput) {
    const customer = await createCustomer(input)
    setCustomers((current) => [...current, customer])
    return customer
  }

  async function registerVehicle(customerId: string, input: VehicleInput) {
    const result = await createVehicle(customerId, input)
    setCustomers((current) => current.some((customer) => customer.id === result.customer.id)
      ? current.map((customer) => customer.id === result.customer.id ? result.customer : customer)
      : [...current, result.customer])
    return result
  }

  async function createDocument(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (creating || !createForm.customerId) return
    setCreating(true)
    try {
      const newDocument = await createSalesDocument({ ...createForm, vehicleId: createForm.vehicleId || null, note: '' })
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
      <div className="sales-workspace"><SalesDocumentList documents={filteredDocuments} selectedDocumentId={selectedDocument?.id ?? ''} rounding={settings.tax.rounding} onSelect={setSelectedDocumentId} />{selectedDocument && selectedTotals ? <SalesDocumentDetail document={selectedDocument} totals={selectedTotals} shopName={settings.shop.name} settings={settings} itemPresets={settings.salesItemPresets} customers={customers} view={documentView} dirty={dirty} saving={saving} saved={saved} onViewChange={setDocumentView} onUpdateHeader={updateHeader} onUpdateDetails={updateDetails} onUpdateTradeIn={updateTradeIn} onUpdateCredit={updateCredit} onUpdateRequiredDocument={updateRequiredDocument} onUpdateItem={updateLineItem} onAddItem={addLineItem} onRemoveItem={removeLineItem} onSave={saveSelectedDocument} onArchive={() => void archiveSelectedDocument()} onPdfDownload={() => void downloadSalesDocumentPdf(selectedDocument, settings)} onPdfPreview={() => void previewSalesDocumentPdf(selectedDocument, settings)} /> : <div className="panel sales-empty"><FileText size={30} /><strong>{loading ? '販売書類を読み込んでいます' : '販売書類が見つかりません'}</strong><span>{loading ? 'しばらくお待ちください。' : '検索条件または書類種別を変更してください。'}</span></div>}</div>
      {createDialogOpen && <SalesDocumentDialog form={createForm} customers={customers} creating={creating} onChange={setCreateForm} onClose={() => setCreateDialogOpen(false)} onSubmit={createDocument} onCreateCustomer={registerCustomer} onCreateVehicle={registerVehicle} />}
    </>
  )
}

function SalesDocumentList({ documents, selectedDocumentId, rounding, onSelect }: { documents: SalesDocument[]; selectedDocumentId: string; rounding: AppSettings['tax']['rounding']; onSelect: (id: string) => void }) {
  return <section className="panel sales-list-panel"><div className="sales-list-header"><div><h2>販売書類</h2><span>書類を選択すると詳細を表示します</span></div><span className="results-count">{documents.length}件</span></div><div className="sales-document-list">{documents.map((document) => <button className={`sales-document-card${document.id === selectedDocumentId ? ' is-selected' : ''}`} key={document.id} type="button" onClick={() => onSelect(document.id)}><div className="sales-card-top"><span className="sales-card-customer"><UserRound size={14} /><strong>{document.customerName}</strong></span><ChevronRight size={16} /></div><span className="sales-card-vehicle"><CarFront size={14} />{document.vehicle || '車両未指定'}{document.plate ? ` ・ ${document.plate}` : ''}</span><div className="sales-card-bottom"><span>{document.issuedAt}</span><strong>{formatYen(calculateTotals(document, rounding).total)}</strong></div></button>)}</div></section>
}

function SalesDocumentDetail({ document, totals, shopName, settings, itemPresets, customers, view, dirty, saving, saved, onViewChange, onUpdateHeader, onUpdateDetails, onUpdateTradeIn, onUpdateCredit, onUpdateRequiredDocument, onUpdateItem, onAddItem, onRemoveItem, onSave, onArchive, onPdfDownload, onPdfPreview }: { document: SalesDocument; totals: SalesTotals; shopName: string; settings: AppSettings; itemPresets: string[]; customers: Customer[]; view: SalesDocumentView; dirty: boolean; saving: boolean; saved: boolean; onViewChange: (view: SalesDocumentView) => void; onUpdateHeader: (field: SalesHeaderField, value: string) => void; onUpdateDetails: (patch: Partial<SalesDocumentDetails>) => void; onUpdateTradeIn: (field: keyof SalesDocumentDetails['tradeIn'], value: string) => void; onUpdateCredit: (field: keyof SalesDocumentDetails['credit'], value: string | boolean) => void; onUpdateRequiredDocument: (field: keyof SalesDocumentDetails['requiredDocuments'], value: string | boolean) => void; onUpdateItem: (itemId: string, field: SalesItemField, value: string) => void; onAddItem: () => void; onRemoveItem: (itemId: string) => void; onSave: () => void; onArchive: () => void; onPdfDownload: () => void; onPdfPreview: () => void }) {
  return <section className="panel sales-detail-panel"><div className="sales-detail-header"><div className="sales-detail-title"><div><span className={`sales-type-badge sales-type-${document.type}`}>{document.type}</span><h2>{document.number}</h2><small>{document.issuedAt} 作成 ・ 発行元 {shopName}</small></div><StatusTag status={document.status} /></div><div className="sales-detail-actions"><button className="button button-secondary" type="button" disabled={!dirty || saving} onClick={onSave}><Save size={16} />{saving ? '保存中…' : saved ? '保存済み' : '保存'}</button><button className="button button-secondary" type="button" onClick={onPdfDownload}><FileDown size={16} />PDF保存</button><button className="button button-danger" type="button" disabled={saving} onClick={onArchive}><Archive size={16} />アーカイブ</button></div></div><div className="sales-document-tabs" role="tablist" aria-label="販売書類の表示"><button id="sales-document-edit-tab" className={view === 'edit' ? 'is-active' : ''} type="button" role="tab" aria-selected={view === 'edit'} aria-controls="sales-document-edit-panel" onClick={() => onViewChange('edit')}><FileText size={16} />入力</button><button id="sales-document-preview-tab" className={view === 'preview' ? 'is-active' : ''} type="button" role="tab" aria-selected={view === 'preview'} aria-controls="sales-document-preview-panel" onClick={() => onViewChange('preview')}><Eye size={16} />プレビュー</button></div>{view === 'edit' ? <div id="sales-document-edit-panel" className="sales-detail-content" role="tabpanel" aria-labelledby="sales-document-edit-tab"><SalesDocumentEditor document={document} totals={totals} itemPresets={itemPresets} customers={customers} onUpdateHeader={onUpdateHeader} onUpdateDetails={onUpdateDetails} onUpdateTradeIn={onUpdateTradeIn} onUpdateCredit={onUpdateCredit} onUpdateRequiredDocument={onUpdateRequiredDocument} onUpdateItem={onUpdateItem} onAddItem={onAddItem} onRemoveItem={onRemoveItem} /></div> : <div id="sales-document-preview-panel" className="sales-detail-content" role="tabpanel" aria-labelledby="sales-document-preview-tab"><SalesDocumentPreview document={document} totals={totals} settings={settings} itemPresets={itemPresets} customers={customers} onUpdateHeader={onUpdateHeader} onUpdateItem={onUpdateItem} onAddItem={onAddItem} onRemoveItem={onRemoveItem} onPdfPreview={onPdfPreview} /></div>}</section>
}

function SalesDocumentEditor({ document, totals, itemPresets, customers, onUpdateHeader, onUpdateDetails, onUpdateTradeIn, onUpdateCredit, onUpdateRequiredDocument, onUpdateItem, onAddItem, onRemoveItem }: { document: SalesDocument; totals: SalesTotals; itemPresets: string[]; customers: Customer[]; onUpdateHeader: (field: SalesHeaderField, value: string) => void; onUpdateDetails: (patch: Partial<SalesDocumentDetails>) => void; onUpdateTradeIn: (field: keyof SalesDocumentDetails['tradeIn'], value: string) => void; onUpdateCredit: (field: keyof SalesDocumentDetails['credit'], value: string | boolean) => void; onUpdateRequiredDocument: (field: keyof SalesDocumentDetails['requiredDocuments'], value: string | boolean) => void; onUpdateItem: (itemId: string, field: SalesItemField, value: string) => void; onAddItem: () => void; onRemoveItem: (itemId: string) => void }) {
  const selectedCustomer = customers.find((customer) => customer.id === document.customerId)
  return <><section className="document-header-editor"><div className="document-header-editor-title"><div><h3>書類基本情報</h3><span>見積番号、顧客・車両、日付、状態を入力できます。</span></div></div><div className="form-grid"><label className="form-field"><span>書類番号</span><input value={document.number} onChange={(event) => onUpdateHeader('number', event.target.value)} /></label><label className="form-field"><span>書類種別</span><select value={document.type} onChange={(event) => onUpdateHeader('type', event.target.value)}><option>見積書</option><option>注文書</option><option>請求書</option></select></label><label className="form-field"><span>状態</span><select value={document.status} onChange={(event) => onUpdateHeader('status', event.target.value)}><option>下書き</option><option>発行済み</option><option>入金待ち</option></select></label><label className="form-field"><span>顧客</span><select value={document.customerId} onChange={(event) => onUpdateHeader('customerId', event.target.value)}>{customers.map((customer) => <option key={customer.id} value={customer.id}>{customer.name}</option>)}</select></label><label className="form-field"><span>対象車両</span><select value={document.vehicleId ?? ''} onChange={(event) => onUpdateHeader('vehicleId', event.target.value)}><option value="">車両を指定しない</option>{selectedCustomer?.vehicles.map((vehicle) => <option key={vehicle.id} value={vehicle.id}>{vehicle.maker} {vehicle.model} ・ {vehicle.plate || '登録番号なし'}</option>)}</select></label><label className="form-field"><span>書類日付</span><input type="date" value={document.issuedAt.replaceAll('/', '-')} onChange={(event) => onUpdateHeader('issuedAt', event.target.value.replaceAll('-', '/'))} /></label><label className="form-field"><span>支払期限</span><input type="date" value={document.dueDate.replaceAll('/', '-')} onChange={(event) => onUpdateHeader('dueDate', event.target.value.replaceAll('-', '/'))} /></label></div><label className="form-field sales-editor-note"><span>備考・特記事項</span><textarea value={document.note} onChange={(event) => onUpdateHeader('note', event.target.value)} placeholder="見積条件や注意事項を入力" /></label></section><SalesEstimateDetailsEditor details={document.details} onUpdateDetails={onUpdateDetails} onUpdateTradeIn={onUpdateTradeIn} onUpdateCredit={onUpdateCredit} onUpdateRequiredDocument={onUpdateRequiredDocument} /><div className="sales-context-grid"><div className="sales-context-card"><span className="sales-context-label"><UserRound size={15} />顧客マスター情報</span><strong>{document.customerName}</strong><small>{document.customerDetails.address || '住所未登録'} ・ {document.phone || '電話番号未登録'}</small></div><div className="sales-context-card"><span className="sales-context-label"><CarFront size={15} />車両マスター情報</span><strong>{document.vehicle || '車両未指定'}</strong><small>{document.vehicleDetails?.vin || document.plate || '登録情報未登録'}</small></div></div><SalesLineItemsEditor document={document} itemPresets={itemPresets} onUpdateItem={onUpdateItem} onAddItem={onAddItem} onRemoveItem={onRemoveItem} /><SalesSummary document={document} totals={totals} /></>
}

function SalesEstimateDetailsEditor({ details, onUpdateDetails, onUpdateTradeIn, onUpdateCredit, onUpdateRequiredDocument }: { details: SalesDocumentDetails; onUpdateDetails: (patch: Partial<SalesDocumentDetails>) => void; onUpdateTradeIn: (field: keyof SalesDocumentDetails['tradeIn'], value: string) => void; onUpdateCredit: (field: keyof SalesDocumentDetails['credit'], value: string | boolean) => void; onUpdateRequiredDocument: (field: keyof SalesDocumentDetails['requiredDocuments'], value: string | boolean) => void }) {
  return <section className="sales-estimate-input-panel"><div className="sales-estimate-input-heading"><div><h3>見積書の追加情報</h3><span>参照テンプレートの販売区分、下取車、支払計画、必要書類を入力できます。</span></div></div><div className="sales-estimate-input-grid"><label className="form-field"><span>販売区分</span><input value={details.salesCategory} onChange={(event) => onUpdateDetails({ salesCategory: event.target.value })} placeholder="中古車" /></label><label className="form-field"><span>担当</span><input value={details.staffName} onChange={(event) => onUpdateDetails({ staffName: event.target.value })} placeholder="担当者名" /></label><label className="form-field"><span>顧客敬称</span><input value={details.customerHonorific} onChange={(event) => onUpdateDetails({ customerHonorific: event.target.value })} /></label><label className="form-field"><span>生年月日</span><input type="date" value={details.customerBirthDate} onChange={(event) => onUpdateDetails({ customerBirthDate: event.target.value })} /></label><label className="form-field"><span>勤務先等</span><input value={details.customerEmployer} onChange={(event) => onUpdateDetails({ customerEmployer: event.target.value })} /></label><label className="form-field"><span>連絡先TEL</span><input value={details.customerContactPhone} onChange={(event) => onUpdateDetails({ customerContactPhone: event.target.value })} /></label><label className="form-field"><span>リサイクル料金（預託金）</span><input type="number" min="0" value={details.recycleFee} onChange={(event) => onUpdateDetails({ recycleFee: Number(event.target.value) })} /></label><label className="form-field"><span>頭金・現金・他</span><input type="number" min="0" value={details.downPayment} onChange={(event) => onUpdateDetails({ downPayment: Number(event.target.value) })} /></label><label className="form-field"><span>残金・所要資金</span><input type="number" min="0" value={details.remainingPayment} onChange={(event) => onUpdateDetails({ remainingPayment: Number(event.target.value) })} /></label></div><fieldset className="sales-estimate-fieldset"><legend>下取車</legend><div className="sales-estimate-input-grid sales-estimate-tradein-grid"><label className="form-field"><span>車名（型式等）</span><input value={details.tradeIn.name} onChange={(event) => onUpdateTradeIn('name', event.target.value)} /></label><label className="form-field"><span>年式</span><input value={details.tradeIn.modelYear} onChange={(event) => onUpdateTradeIn('modelYear', event.target.value)} /></label><label className="form-field"><span>車検日</span><input type="date" value={details.tradeIn.inspectionDate} onChange={(event) => onUpdateTradeIn('inspectionDate', event.target.value)} /></label><label className="form-field"><span>走行距離</span><input value={details.tradeIn.mileage} onChange={(event) => onUpdateTradeIn('mileage', event.target.value)} /></label><label className="form-field"><span>車体色</span><input value={details.tradeIn.color} onChange={(event) => onUpdateTradeIn('color', event.target.value)} /></label></div></fieldset><fieldset className="sales-estimate-fieldset"><legend>クレジットお支払いプラン</legend><label className="sales-checkbox-field"><input type="checkbox" checked={details.credit.enabled} onChange={(event) => onUpdateCredit('enabled', event.target.checked)} /><span>クレジット支払計画を表示する</span></label><div className="sales-estimate-input-grid sales-estimate-credit-grid"><label className="form-field"><span>支払回数</span><input value={details.credit.paymentCount} onChange={(event) => onUpdateCredit('paymentCount', event.target.value)} placeholder="例：36回" /></label><label className="form-field"><span>分割手数料</span><input type="number" min="0" value={details.credit.fee} onChange={(event) => onUpdateCredit('fee', event.target.value)} /></label><label className="form-field"><span>月々</span><input type="number" min="0" value={details.credit.monthlyPayment} onChange={(event) => onUpdateCredit('monthlyPayment', event.target.value)} /></label><label className="form-field"><span>初回</span><input type="number" min="0" value={details.credit.initialPayment} onChange={(event) => onUpdateCredit('initialPayment', event.target.value)} /></label><label className="form-field"><span>ボーナス月</span><input value={details.credit.bonusMonths} onChange={(event) => onUpdateCredit('bonusMonths', event.target.value)} placeholder="例：6月・12月" /></label><label className="form-field"><span>ボーナス加算</span><input type="number" min="0" value={details.credit.bonusPayment} onChange={(event) => onUpdateCredit('bonusPayment', event.target.value)} /></label></div></fieldset><fieldset className="sales-estimate-fieldset"><legend>必要書類</legend><div className="sales-required-documents">{requiredDocumentFields.map(({ key, label }) => <label className="sales-checkbox-field" key={key}><input type="checkbox" checked={details.requiredDocuments[key] === true} onChange={(event) => onUpdateRequiredDocument(key, event.target.checked)} /><span>{label}</span></label>)}</div><label className="form-field"><span>その他</span><input value={details.requiredDocuments.other} onChange={(event) => onUpdateRequiredDocument('other', event.target.value)} /></label></fieldset></section>
}

function SalesLineItemsEditor({ document, itemPresets, onUpdateItem, onAddItem, onRemoveItem }: { document: SalesDocument; itemPresets: string[]; onUpdateItem: (itemId: string, field: SalesItemField, value: string) => void; onAddItem: () => void; onRemoveItem: (itemId: string) => void }) {
  return <section className="sales-items-panel"><div className="sales-items-header"><div><h3>見積明細</h3><span>車両本体・付属品・諸費用・値引きを、課税区分と金額内訳付きで登録します。</span></div><button className="text-button" type="button" onClick={onAddItem}><Plus size={15} />明細を追加</button></div><div className="sales-items-table"><div className="sales-items-head"><span>区分</span><span>内容</span><span>数量</span><span>単位</span><span>単価</span><span>技術料・他</span><span>課税区分</span><span>摘要</span><span>金額</span><span /></div>{document.items.map((item) => <div className="sales-item-row" key={item.id}><select aria-label="明細種別" value={item.itemType} onChange={(event) => onUpdateItem(item.id, 'itemType', event.target.value)}>{salesLineItemTypes.map((type) => <option key={type}>{type}</option>)}</select><input list="sales-item-presets" aria-label="明細内容" value={item.description} onChange={(event) => onUpdateItem(item.id, 'description', event.target.value)} placeholder="明細内容" /><input className="sales-number-input" aria-label="数量" type="number" min="0" value={item.quantity} onChange={(event) => onUpdateItem(item.id, 'quantity', event.target.value)} /><input className="sales-unit-input" aria-label="単位" value={item.unit} onChange={(event) => onUpdateItem(item.id, 'unit', event.target.value)} /><input className="sales-price-input" aria-label="単価" type="number" value={item.unitPrice} onChange={(event) => onUpdateItem(item.id, 'unitPrice', event.target.value)} /><input className="sales-other-input" aria-label="技術料・他" type="number" value={item.otherAmount} onChange={(event) => onUpdateItem(item.id, 'otherAmount', event.target.value)} /><select aria-label="課税区分" value={item.taxCategory} onChange={(event) => onUpdateItem(item.id, 'taxCategory', event.target.value)}>{salesTaxCategories.map((category) => <option key={category}>{category}</option>)}</select><input aria-label="摘要" value={item.summary} onChange={(event) => onUpdateItem(item.id, 'summary', event.target.value)} /><strong>{formatYen(calculateLineAmount(item))}</strong><button className="sales-item-remove" type="button" aria-label="明細を削除" onClick={() => onRemoveLineItemGuard(item.id, document.items.length, onRemoveItem)}><Trash2 size={15} /></button></div>)}</div><datalist id="sales-item-presets">{itemPresets.map((preset) => <option key={preset} value={preset} />)}</datalist></section>
}

function SalesSummary({ document, totals }: { document: SalesDocument; totals: SalesTotals }) {
  return <><div className="sales-summary-grid"><div className="sales-note"><span>備考・特記事項</span><p>{document.note || '備考はありません。'}</p></div><div className="sales-totals"><div><span>課税対象額</span><strong>{formatYen(totals.taxableSubtotal)}</strong></div><div><span>非課税・対象外</span><strong>{formatYen(totals.nonTaxableSubtotal + totals.outOfScopeSubtotal)}</strong></div><div><span>消費税（{formatPercent(document.taxRate)}）</span><strong>{formatYen(totals.tax)}</strong></div><div className="sales-total-row"><span>見積金額</span><strong>{formatYen(totals.total)}</strong></div></div></div><div className="sales-detail-footer"><span><ShoppingCart size={15} />支払期限：{document.dueDate || '未設定'}</span><span><CircleDollarSign size={15} />入金状況は入金管理で登録</span></div></>
}

function SalesDocumentPreview({ document, totals, settings, itemPresets, customers, onUpdateHeader, onUpdateItem, onAddItem, onRemoveItem, onPdfPreview }: { document: SalesDocument; totals: SalesTotals; settings: AppSettings; itemPresets: string[]; customers: Customer[]; onUpdateHeader: (field: SalesHeaderField, value: string) => void; onUpdateItem: (itemId: string, field: SalesItemField, value: string) => void; onAddItem: () => void; onRemoveItem: (itemId: string) => void; onPdfPreview: () => void }) {
  return <SalesEstimatePreview document={document} totals={totals} settings={settings} itemPresets={itemPresets} customers={customers} onUpdateHeader={onUpdateHeader} onUpdateItem={onUpdateItem} onAddItem={onAddItem} onRemoveItem={onRemoveItem} onPdfPreview={onPdfPreview} />
  /*
  const selectedCustomer = customers.find((customer) => customer.id === document.customerId)
  const selectedVehicle = selectedCustomer?.vehicles.find((vehicle) => vehicle.id === document.vehicleId)
  const customer = document.customerDetails ?? mapCustomerDetails(selectedCustomer)
  const vehicle = document.vehicleDetails ?? (selectedVehicle ? mapVehicleDetails(selectedVehicle) : null)
  const details = document.details
  const shopLines = [settings.shop.postalCode ? `〒${settings.shop.postalCode}` : '', settings.shop.address, settings.shop.phone ? `TEL ${settings.shop.phone}` : '', settings.shop.representative ? `担当 ${settings.shop.representative}` : '', settings.shop.registrationNumber ? `登録番号 ${settings.shop.registrationNumber}` : ''].filter(Boolean)
  const paymentNote = settings.document.paymentNote || '店頭または指定口座へお支払いください。'
  const bankAccount = [settings.shop.bankName, settings.shop.bankAccount].filter(Boolean).join(' / ') || '未設定'
  return <div className="sales-preview-area"><div className="sales-preview-toolbar"><div><strong>見積書プレビュー</strong><span>実際のPDFと同じ情報配置で確認できます。基本項目と明細はこの画面から編集できます。</span></div><button className="button button-secondary" type="button" onClick={onPdfPreview}><Eye size={16} />PDFで確認</button></div><article className="sales-document-paper sales-estimate-paper"><header className="sales-estimate-paper-header"><div className="sales-estimate-title-block"><select className="sales-estimate-title" aria-label="書類種別" value={document.type} onChange={(event) => onUpdateHeader('type', event.target.value)}><option>見積書</option><option>注文書</option><option>請求書</option></select><span>{details.salesCategory || '販売書類'}</span></div><div className="sales-estimate-meta-table"><div><span>日付</span><input type="date" aria-label="発行日" value={document.issuedAt.replaceAll('/', '-')} onChange={(event) => onUpdateHeader('issuedAt', event.target.value.replaceAll('-', '/'))} /></div><div><span>販売区分</span><strong>{details.salesCategory || '未設定'}</strong></div><div><span>担当</span><strong>{details.staffName || '未設定'}</strong></div><div><span>見積番号</span><input aria-label="書類番号" value={document.number} onChange={(event) => onUpdateHeader('number', event.target.value)} /></div><div><span>ページ</span><strong>1</strong></div></div></header><section className="sales-estimate-customer-grid"><div className="sales-estimate-customer-box"><div className="sales-estimate-cell-label">お名前</div><div className="sales-estimate-cell-value"><strong>{customer.name || '未設定'} {details.customerHonorific}</strong><small>{customer.kana || 'ふりがな未登録'}</small></div><div className="sales-estimate-cell-label">ご住所</div><div className="sales-estimate-cell-value"><span>{customer.postalCode ? `〒${customer.postalCode}` : ''}</span><span>{customer.address || '住所未登録'}</span></div></div><div className="sales-estimate-contact-box"><div className="sales-estimate-cell-label">生年月日</div><div>{details.customerBirthDate || '未設定'}</div><div className="sales-estimate-cell-label">電話番号</div><div>{customer.phone || '未登録'}</div><div className="sales-estimate-cell-label">勤務先等</div><div>{details.customerEmployer || '未設定'}</div><div className="sales-estimate-cell-label">連絡先TEL</div><div>{details.customerContactPhone || '未設定'}</div></div></section><EstimateVehicleTable vehicle={vehicle} /><EstimateTradeInTable details={details} /><section className="sales-estimate-summary-top"><div className="sales-estimate-amount-card"><span>お見積金額</span><strong>{formatYen(totals.total)}</strong></div><div className="sales-estimate-tax-card"><div><span>課税対象額</span><strong>{formatYen(totals.taxableSubtotal)}</strong></div><div><span>消費税（{formatPercent(document.taxRate)}）</span><strong>{formatYen(totals.tax)}</strong></div><div><span>非課税・対象外</span><strong>{formatYen(totals.nonTaxableSubtotal + totals.outOfScopeSubtotal)}</strong></div></div></section><div className="sales-estimate-section-title"><h3>明細</h3><span /><button className="text-button" type="button" onClick={onAddItem}><Plus size={15} />明細を追加</button></div><div className="sales-estimate-items-table"><div className="sales-estimate-items-head"><span>No.</span><span>作業内容／部品名等</span><span>数量</span><span>単位</span><span>部品単価</span><span>部品金額</span><span>技術料・他</span><span>摘要</span></div>{document.items.map((item, index) => <div className="sales-estimate-item-row" key={item.id}><span>{index + 1}</span><input list="sales-preview-item-presets" aria-label="プレビューの明細内容" value={item.description} onChange={(event) => onUpdateItem(item.id, 'description', event.target.value)} placeholder="明細内容" /><input aria-label="プレビューの数量" type="number" min="0" value={item.quantity} onChange={(event) => onUpdateItem(item.id, 'quantity', event.target.value)} /><input aria-label="プレビューの単位" value={item.unit} onChange={(event) => onUpdateItem(item.id, 'unit', event.target.value)} /><input aria-label="プレビューの単価" type="number" value={item.unitPrice} onChange={(event) => onUpdateItem(item.id, 'unitPrice', event.target.value)} /><strong>{formatYen(Math.round(item.quantity * item.unitPrice))}</strong><strong>{formatYen(item.otherAmount)}</strong><input aria-label="プレビューの摘要" value={item.summary} onChange={(event) => onUpdateItem(item.id, 'summary', event.target.value)} /><button className="sales-estimate-item-remove" type="button" aria-label="明細を削除" onClick={() => onRemoveLineItemGuard(item.id, document.items.length, onRemoveItem)}><Trash2 size={14} /></button></div>)}</div><datalist id="sales-preview-item-presets">{itemPresets.map((preset) => <option key={preset} value={preset} />)}</datalist><div className="sales-estimate-items-total"><span>明細合計</span><strong>{formatYen(totals.subtotal)}</strong><span>消費税</span><strong>{formatYen(totals.tax)}</strong><span>合計</span><strong>{formatYen(totals.total)}</strong></div><section className="sales-estimate-bottom-grid"><div><div className="sales-estimate-recycle"><span>リサイクル料金（預託金）</span><strong>{formatYen(details.recycleFee)}</strong></div><div className="sales-estimate-credit"><h4>クレジットお支払いプラン</h4>{details.credit.enabled ? <div><span>{details.credit.paymentCount || '回数未設定'}</span><span>月々 {formatYen(details.credit.monthlyPayment)}</span><span>初回 {formatYen(details.credit.initialPayment)}</span><span>ボーナス {details.credit.bonusMonths || '月未設定'} / {formatYen(details.credit.bonusPayment)}</span></div> : <p>利用なし</p>}</div><div className="sales-estimate-required"><h4>必要書類</h4><p>{requiredDocumentLabels(details).join(' ／ ') || '未確認'}</p></div></div><div className="sales-estimate-company"><strong>{settings.shop.name || '店舗名未設定'}</strong>{shopLines.slice(0, 4).map((line) => <span key={line}>{line}</span>)}<div className="sales-estimate-company-payment"><span>お支払いについて</span><p>{paymentNote}</p><span>振込先</span><p>{bankAccount}</p></div></div></section><footer className="sales-paper-footer"><span>{document.note || settings.document.footerNote || '見積条件は担当者へご確認ください。'}</span><span>ページ 1</span></footer></article></div>
  */
}

type SalesPreviewProps = {
  document: SalesDocument
  totals: SalesTotals
  settings: AppSettings
  itemPresets: string[]
  customers: Customer[]
  onUpdateHeader: (field: SalesHeaderField, value: string) => void
  onUpdateItem: (itemId: string, field: SalesItemField, value: string) => void
  onAddItem: () => void
  onRemoveItem: (itemId: string) => void
  onPdfPreview: () => void
}

function SalesEstimatePreview({ document, totals, settings, itemPresets, customers, onUpdateHeader, onUpdateItem, onAddItem, onRemoveItem, onPdfPreview }: SalesPreviewProps) {
  const selectedCustomer = customers.find((customer) => customer.id === document.customerId)
  const selectedVehicle = selectedCustomer?.vehicles.find((vehicle) => vehicle.id === document.vehicleId)
  const customer = document.customerDetails ?? mapCustomerDetails(selectedCustomer)
  const vehicle = document.vehicleDetails ?? (selectedVehicle ? mapVehicleDetails(selectedVehicle) : null)
  const details = document.details
  const shopLines = [settings.shop.postalCode ? `〒${settings.shop.postalCode}` : '', settings.shop.address, settings.shop.phone ? `TEL ${settings.shop.phone}` : '', settings.shop.representative ? `担当 ${settings.shop.representative}` : '', settings.shop.registrationNumber ? `登録番号 ${settings.shop.registrationNumber}` : ''].filter(Boolean)
  const paymentNote = settings.document.paymentNote || '店頭または指定口座へお支払いください。'
  const bankAccount = [settings.shop.bankName, settings.shop.bankAccount].filter(Boolean).join(' / ') || '未設定'
  return <div className="sales-preview-area"><div className="sales-preview-toolbar"><div><strong>見積書プレビュー</strong><span>実際のPDFと同じ情報配置で確認できます。基本項目と明細はこの画面から編集できます。</span></div><button className="button button-secondary" type="button" onClick={onPdfPreview}><Eye size={16} />PDFで確認</button></div><article className="sales-document-paper sales-estimate-paper"><header className="sales-estimate-paper-header"><div className="sales-estimate-title-block"><select className="sales-estimate-title" aria-label="書類種別" value={document.type} onChange={(event) => onUpdateHeader('type', event.target.value)}><option>見積書</option><option>注文書</option><option>請求書</option></select><span>{details.salesCategory || '販売書類'}</span></div><div className="sales-estimate-meta-table"><div><span>日付</span><input type="date" aria-label="発行日" value={document.issuedAt.replaceAll('/', '-')} onChange={(event) => onUpdateHeader('issuedAt', event.target.value.replaceAll('-', '/'))} /></div><div><span>販売区分</span><strong>{details.salesCategory || '未設定'}</strong></div><div><span>担当</span><strong>{details.staffName || '未設定'}</strong></div><div><span>見積番号</span><input aria-label="書類番号" value={document.number} onChange={(event) => onUpdateHeader('number', event.target.value)} /></div><div><span>ページ</span><strong>1</strong></div></div></header><section className="sales-estimate-customer-grid"><div className="sales-estimate-customer-box"><div className="sales-estimate-cell-label">お名前</div><div className="sales-estimate-cell-value"><strong>{customer.name || '未設定'} {details.customerHonorific}</strong><small>{customer.kana || 'ふりがな未登録'}</small></div><div className="sales-estimate-cell-label">ご住所</div><div className="sales-estimate-cell-value"><span>{customer.postalCode ? `〒${customer.postalCode}` : ''}</span><span>{customer.address || '住所未登録'}</span></div></div><div className="sales-estimate-contact-box"><div className="sales-estimate-cell-label">生年月日</div><div>{details.customerBirthDate || '未設定'}</div><div className="sales-estimate-cell-label">電話番号</div><div>{customer.phone || '未登録'}</div><div className="sales-estimate-cell-label">勤務先等</div><div>{details.customerEmployer || '未設定'}</div><div className="sales-estimate-cell-label">連絡先TEL</div><div>{details.customerContactPhone || '未設定'}</div></div></section><EstimateVehicleTable vehicle={vehicle} /><EstimateTradeInTable details={details} /><section className="sales-estimate-summary-top"><div className="sales-estimate-amount-card"><span>お見積金額</span><strong>{formatYen(totals.total)}</strong></div><div className="sales-estimate-tax-card"><div><span>課税対象額</span><strong>{formatYen(totals.taxableSubtotal)}</strong></div><div><span>消費税（{formatPercent(document.taxRate)}）</span><strong>{formatYen(totals.tax)}</strong></div><div><span>非課税・対象外</span><strong>{formatYen(totals.nonTaxableSubtotal + totals.outOfScopeSubtotal)}</strong></div></div></section><div className="sales-estimate-status-line"><span>支払期限：{document.dueDate || '未設定'}</span><span>状態：{document.status}</span></div><div className="sales-estimate-section-title"><h3>明細</h3><span /><button className="text-button" type="button" onClick={onAddItem}><Plus size={15} />明細を追加</button></div><div className="sales-estimate-items-table"><div className="sales-estimate-items-head"><span>No.</span><span>作業内容／部品名等</span><span>数量</span><span>単位</span><span>部品単価</span><span>部品金額</span><span>技術料・他</span><span>摘要・課税</span></div>{document.items.map((item, index) => <div className="sales-estimate-item-row" key={item.id}><span>{index + 1}</span><input list="sales-preview-item-presets" aria-label="プレビューの明細内容" value={item.description} onChange={(event) => onUpdateItem(item.id, 'description', event.target.value)} placeholder="明細内容" /><input aria-label="プレビューの数量" type="number" min="0" value={item.quantity} onChange={(event) => onUpdateItem(item.id, 'quantity', event.target.value)} /><input aria-label="プレビューの単位" value={item.unit} onChange={(event) => onUpdateItem(item.id, 'unit', event.target.value)} /><input aria-label="プレビューの単価" type="number" value={item.unitPrice} onChange={(event) => onUpdateItem(item.id, 'unitPrice', event.target.value)} /><strong>{formatYen(Math.round(item.quantity * item.unitPrice))}</strong><strong>{formatYen(item.otherAmount)}</strong><div className="sales-estimate-summary-cell"><input aria-label="プレビューの摘要" value={item.summary} onChange={(event) => onUpdateItem(item.id, 'summary', event.target.value)} /><select aria-label="プレビューの課税区分" value={item.taxCategory} onChange={(event) => onUpdateItem(item.id, 'taxCategory', event.target.value)}>{salesTaxCategories.map((category) => <option key={category}>{category}</option>)}</select></div><button className="sales-estimate-item-remove" type="button" aria-label="明細を削除" onClick={() => onRemoveLineItemGuard(item.id, document.items.length, onRemoveItem)}><Trash2 size={14} /></button></div>)}</div><datalist id="sales-preview-item-presets">{itemPresets.map((preset) => <option key={preset} value={preset} />)}</datalist><div className="sales-estimate-items-total"><span>明細合計</span><strong>{formatYen(totals.subtotal)}</strong><span>消費税</span><strong>{formatYen(totals.tax)}</strong><span>合計</span><strong>{formatYen(totals.total)}</strong></div><section className="sales-estimate-bottom-grid"><div><div className="sales-estimate-recycle"><span>リサイクル料金（預託金）</span><strong>{formatYen(details.recycleFee)}</strong></div><div className="sales-estimate-payment-summary"><span>頭金・現金・他</span><strong>{formatYen(details.downPayment)}</strong><span>残金・所要資金</span><strong>{formatYen(details.remainingPayment)}</strong></div><div className="sales-estimate-credit"><h4>クレジットお支払いプラン</h4>{details.credit.enabled ? <div><span>{details.credit.paymentCount || '回数未設定'}</span><span>手数料 {formatYen(details.credit.fee)}</span><span>月々 {formatYen(details.credit.monthlyPayment)}</span><span>初回 {formatYen(details.credit.initialPayment)}</span><span>ボーナス {details.credit.bonusMonths || '月未設定'} / {formatYen(details.credit.bonusPayment)}</span></div> : <p>利用なし</p>}</div><div className="sales-estimate-required"><h4>必要書類</h4><p>{requiredDocumentLabels(details).join(' ／ ') || '未確認'}</p></div></div><div className="sales-estimate-company"><strong>{settings.shop.name || '店舗名未設定'}</strong>{shopLines.slice(0, 4).map((line) => <span key={line}>{line}</span>)}<div className="sales-estimate-company-payment"><span>お支払いについて</span><p>{paymentNote}</p><span>振込先</span><p>{bankAccount}</p></div></div></section><footer className="sales-paper-footer"><span>{document.note || settings.document.footerNote || '見積条件は担当者へご確認ください。'}</span><span>ページ 1</span></footer></article></div>
}

function EstimateVehicleTable({ vehicle }: { vehicle: SalesDocument['vehicleDetails'] }) {
  const values = vehicle ?? { maker: '', name: '', modelType: '', plate: '', vin: '', year: '', inspectionDate: '', mileage: '', color: '', displacement: '', transmission: '', inspectionRecordAvailable: false }
  return <section className="sales-estimate-vehicle-table"><div className="sales-estimate-vehicle-row sales-estimate-vehicle-labels"><span>メーカー</span><span>車名・仕様</span><span>年式</span><span>排気量</span><span>ミッション</span><span>車体色</span></div><div className="sales-estimate-vehicle-row"><span>{values.maker || '未設定'}</span><span>{values.name || '未設定'}</span><span>{values.year || '未設定'}</span><span>{values.displacement || '未設定'}</span><span>{values.transmission || '未設定'}</span><span>{values.color || '未設定'}</span></div><div className="sales-estimate-vehicle-row sales-estimate-vehicle-labels"><span>型式</span><span>車台番号</span><span>登録番号</span><span>走行距離</span><span>車検日</span><span>記録簿</span></div><div className="sales-estimate-vehicle-row"><span>{values.modelType || '未設定'}</span><span>{values.vin || '未設定'}</span><span>{values.plate || '未設定'}</span><span>{values.mileage || '未設定'}</span><span>{values.inspectionDate || '未設定'}</span><span>{values.inspectionRecordAvailable ? 'あり' : 'なし'}</span></div></section>
}

function EstimateTradeInTable({ details }: { details: SalesDocumentDetails }) {
  const values = details.tradeIn
  return <section className="sales-estimate-tradein-table"><div><span>下取車名（型式等）</span><span>年式</span><span>車検日</span><span>走行距離</span><span>車体色</span></div><div><strong>{values.name || 'なし'}</strong><span>{values.modelYear || '-'}</span><span>{values.inspectionDate || '-'}</span><span>{values.mileage || '-'}</span><span>{values.color || '-'}</span></div></section>
}

function onRemoveLineItemGuard(itemId: string, itemCount: number, onRemove: (itemId: string) => void) {
  if (itemCount <= 1) return
  onRemove(itemId)
}

function StatusTag({ status }: { status: SalesDocument['status'] }) {
  const tone = status === '入金待ち' ? 'warning' : status === '発行済み' ? 'normal' : status === 'アーカイブ済み' ? 'danger' : 'draft'
  return <span className={`sales-status-tag sales-status-${tone}`}><span className="status-dot" />{status}</span>
}

type SalesTotals = { subtotal: number; taxableSubtotal: number; nonTaxableSubtotal: number; outOfScopeSubtotal: number; tax: number; total: number }

function calculateTotals(document: SalesDocument, rounding: AppSettings['tax']['rounding']): SalesTotals {
  const subtotal = document.items.reduce((sum, item) => sum + calculateLineAmount(item), 0)
  const taxableSubtotal = document.items.filter((item) => item.taxCategory === '課税').reduce((sum, item) => sum + calculateLineAmount(item), 0)
  const nonTaxableSubtotal = document.items.filter((item) => item.taxCategory === '非課税').reduce((sum, item) => sum + calculateLineAmount(item), 0)
  const outOfScopeSubtotal = document.items.filter((item) => item.taxCategory === '対象外').reduce((sum, item) => sum + calculateLineAmount(item), 0)
  const taxValue = Math.max(0, taxableSubtotal) * document.taxRate
  const tax = rounding === '四捨五入' ? Math.round(taxValue) : Math.floor(taxValue)
  return { subtotal, taxableSubtotal, nonTaxableSubtotal, outOfScopeSubtotal, tax, total: subtotal + tax }
}

function calculateLineAmount(item: SalesLineItem) {
  return Math.round(item.quantity * item.unitPrice + item.otherAmount)
}

function formatYen(amount: number) {
  return `¥${new Intl.NumberFormat('ja-JP').format(Math.round(amount))}`
}

function formatPercent(value: number) {
  return `${Number.isInteger(value * 100) ? value * 100 : (value * 100).toFixed(2)}%`
}

function requiredDocumentLabels(details: SalesDocumentDetails) {
  return requiredDocumentFields.filter(({ key }) => details.requiredDocuments[key] === true).map(({ label }) => label).concat(details.requiredDocuments.other ? [details.requiredDocuments.other] : [])
}

function mapCustomerDetails(customer: Customer | undefined): SalesDocument['customerDetails'] {
  return customer ? { name: customer.name, kana: customer.kana, phone: customer.phone, postalCode: customer.postalCode, address: customer.address, birthDate: '', employer: '', contactPhone: '' } : emptyCustomerDetails()
}

function emptyCustomerDetails(): SalesDocument['customerDetails'] {
  return { name: '', kana: '', phone: '', postalCode: '', address: '', birthDate: '', employer: '', contactPhone: '' }
}

function mapVehicleDetails(vehicle: Vehicle): NonNullable<SalesDocument['vehicleDetails']> {
  return { maker: vehicle.maker, name: vehicle.model, modelType: vehicle.modelType, plate: vehicle.plate, vin: vehicle.vin, year: vehicle.year, inspectionDate: vehicle.inspectionDate, mileage: vehicle.mileage, color: vehicle.color, displacement: vehicle.displacement, transmission: vehicle.transmission, inspectionRecordAvailable: vehicle.inspectionRecordAvailable }
}

function SalesDocumentDialog({ form, customers, creating, onChange, onClose, onSubmit, onCreateCustomer, onCreateVehicle }: { form: SalesCreateForm; customers: Customer[]; creating: boolean; onChange: (form: SalesCreateForm) => void; onClose: () => void; onSubmit: (event: FormEvent<HTMLFormElement>) => void; onCreateCustomer: (input: CustomerInput) => Promise<Customer>; onCreateVehicle: (customerId: string, input: VehicleInput) => Promise<{ customer: Customer; vehicleId: string }> }) {
  const selectedCustomer = customers.find((customer) => customer.id === form.customerId)
  const vehicles = selectedCustomer?.vehicles ?? []
  const [newCustomerOpen, setNewCustomerOpen] = useState(false)
  const [newVehicleOpen, setNewVehicleOpen] = useState(false)
  const [newCustomerForm, setNewCustomerForm] = useState<CustomerInput>({ ...emptySalesCustomerForm })
  const [newVehicleForm, setNewVehicleForm] = useState<VehicleInput>({ ...emptySalesVehicleForm })
  const [registeringCustomer, setRegisteringCustomer] = useState(false)
  const [registeringVehicle, setRegisteringVehicle] = useState(false)
  const [dialogError, setDialogError] = useState('')

  function selectCustomer(customerId: string) {
    const customer = customers.find((item) => item.id === customerId)
    onChange({ ...form, customerId, vehicleId: customer?.vehicles[0]?.id ?? '' })
    setNewVehicleOpen(false)
    setDialogError('')
  }

  function openNewCustomerForm() {
    setNewCustomerForm({ ...emptySalesCustomerForm })
    setNewCustomerOpen(true)
    setNewVehicleOpen(false)
    setDialogError('')
  }

  function openNewVehicleForm() {
    if (!selectedCustomer) return
    setNewVehicleForm({ ...emptySalesVehicleForm })
    setNewVehicleOpen(true)
    setNewCustomerOpen(false)
    setDialogError('')
  }

  async function saveNewCustomer() {
    const name = newCustomerForm.name.trim()
    if (!name) {
      setDialogError('顧客名を入力してください。')
      return
    }
    setRegisteringCustomer(true)
    setDialogError('')
    try {
      const customer = await onCreateCustomer({ ...newCustomerForm, name, memo: '' })
      onChange({ ...form, customerId: customer.id, vehicleId: customer.vehicles[0]?.id ?? '' })
      setNewCustomerForm({ ...emptySalesCustomerForm })
      setNewCustomerOpen(false)
    } catch (error: unknown) {
      setDialogError(error instanceof Error ? error.message : '顧客を登録できませんでした。')
    } finally {
      setRegisteringCustomer(false)
    }
  }

  async function saveNewVehicle() {
    if (!selectedCustomer) return
    const maker = newVehicleForm.maker.trim()
    const model = newVehicleForm.model.trim()
    if (!maker || !model) {
      setDialogError('メーカーと車名を入力してください。')
      return
    }
    setRegisteringVehicle(true)
    setDialogError('')
    try {
      const result = await onCreateVehicle(selectedCustomer.id, { ...newVehicleForm, maker, model })
      onChange({ ...form, vehicleId: result.vehicleId })
      setNewVehicleForm({ ...emptySalesVehicleForm })
      setNewVehicleOpen(false)
    } catch (error: unknown) {
      setDialogError(error instanceof Error ? error.message : '車両を登録できませんでした。')
    } finally {
      setRegisteringVehicle(false)
    }
  }

  return <div className="modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose() }}><section className="modal" role="dialog" aria-modal="true" aria-labelledby="sales-modal-title"><div className="modal-header"><h2 id="sales-modal-title">販売書類を作成</h2><button className="modal-close" type="button" aria-label="閉じる" onClick={onClose}><X size={19} /></button></div><form className="modal-form" onSubmit={onSubmit}><p className="modal-description"><FileText size={16} />顧客・車両を選択して、下書きの販売書類を作成します。未登録の場合はこの画面から追加できます。</p><div className="form-grid"><label className="form-field"><span>書類種別<em>必須</em></span><select required value={form.type} onChange={(event) => onChange({ ...form, type: event.target.value as SalesDocumentType })}><option>見積書</option><option>注文書</option><option>請求書</option></select></label><div className="form-field sales-create-related-field"><span>顧客<em>必須</em></span><div className="sales-create-select-row"><select required aria-label="顧客" value={form.customerId} onChange={(event) => selectCustomer(event.target.value)}><option value="" disabled>顧客を選択してください</option>{customers.map((customer) => <option key={customer.id} value={customer.id}>{customer.name}（{customer.phone || '電話番号未登録'}）</option>)}</select><button className="button button-secondary sales-create-inline-action" type="button" onClick={openNewCustomerForm}><Plus size={14} />新しい顧客</button></div></div><div className="form-field sales-create-related-field"><span>対象車両</span><div className="sales-create-select-row"><select aria-label="対象車両" disabled={!selectedCustomer} value={form.vehicleId} onChange={(event) => onChange({ ...form, vehicleId: event.target.value })}><option value="">車両を指定しない</option>{vehicles.map((vehicle) => <option key={vehicle.id} value={vehicle.id}>{vehicle.maker} {vehicle.model}{vehicle.plate ? `（${vehicle.plate}）` : ''}</option>)}</select><button className="button button-secondary sales-create-inline-action" type="button" disabled={!selectedCustomer} onClick={openNewVehicleForm}><Plus size={14} />新しい車両</button></div></div><label className="form-field"><span>支払期限</span><input type="date" value={form.dueDate} onChange={(event) => onChange({ ...form, dueDate: event.target.value })} /></label></div>{newCustomerOpen && <div className="sales-create-inline-panel"><div><h3>新しい顧客を登録</h3><p>顧客名を登録すると、この販売書類の顧客として選択されます。</p></div><div className="form-grid"><label className="form-field"><span>顧客名<em>必須</em></span><input autoFocus value={newCustomerForm.name} onChange={(event) => setNewCustomerForm({ ...newCustomerForm, name: event.target.value })} placeholder="例：山田 太郎" /></label><label className="form-field"><span>ふりがな</span><input value={newCustomerForm.kana} onChange={(event) => setNewCustomerForm({ ...newCustomerForm, kana: event.target.value })} placeholder="例：やまだ たろう" /></label><label className="form-field"><span>電話番号</span><input type="tel" value={newCustomerForm.phone} onChange={(event) => setNewCustomerForm({ ...newCustomerForm, phone: event.target.value })} placeholder="例：090-1234-5678" /></label><label className="form-field"><span>メールアドレス</span><input type="email" value={newCustomerForm.email} onChange={(event) => setNewCustomerForm({ ...newCustomerForm, email: event.target.value })} placeholder="例：example@example.com" /></label><label className="form-field"><span>郵便番号</span><input value={newCustomerForm.postalCode ?? ''} onChange={(event) => setNewCustomerForm({ ...newCustomerForm, postalCode: event.target.value })} placeholder="例：100-0001" /></label><label className="form-field"><span>住所</span><input value={newCustomerForm.address} onChange={(event) => setNewCustomerForm({ ...newCustomerForm, address: event.target.value })} placeholder="例：東京都千代田区" /></label></div><div className="sales-create-inline-actions"><button className="button button-secondary" type="button" disabled={registeringCustomer} onClick={() => setNewCustomerOpen(false)}>閉じる</button><button className="button button-primary" type="button" disabled={registeringCustomer} onClick={() => void saveNewCustomer()}><Plus size={15} />{registeringCustomer ? '登録中…' : '顧客を登録'}</button></div></div>}{newVehicleOpen && <div className="sales-create-inline-panel"><div><h3>新しい車両を登録</h3><p>{selectedCustomer?.name} の車両情報を登録します。メーカーと車名は必須です。</p></div><div className="form-grid"><label className="form-field"><span>メーカー<em>必須</em></span><input autoFocus value={newVehicleForm.maker} onChange={(event) => setNewVehicleForm({ ...newVehicleForm, maker: event.target.value })} placeholder="例：トヨタ" /></label><label className="form-field"><span>車名<em>必須</em></span><input value={newVehicleForm.model} onChange={(event) => setNewVehicleForm({ ...newVehicleForm, model: event.target.value })} placeholder="例：プリウス" /></label><label className="form-field"><span>型式</span><input value={newVehicleForm.modelType} onChange={(event) => setNewVehicleForm({ ...newVehicleForm, modelType: event.target.value })} placeholder="例：6AA-ZVW60" /></label><label className="form-field"><span>登録番号</span><input value={newVehicleForm.plate} onChange={(event) => setNewVehicleForm({ ...newVehicleForm, plate: event.target.value })} placeholder="例：品川 500 あ 1234" /></label><label className="form-field"><span>車台番号</span><input value={newVehicleForm.vin} onChange={(event) => setNewVehicleForm({ ...newVehicleForm, vin: event.target.value })} placeholder="例：ZVW5000001" /></label><label className="form-field"><span>年式</span><input value={newVehicleForm.year} onChange={(event) => setNewVehicleForm({ ...newVehicleForm, year: event.target.value })} placeholder="例：2024" /></label><label className="form-field"><span>車検満了日</span><input type="date" value={newVehicleForm.inspectionDate.replaceAll('/', '-')} onChange={(event) => setNewVehicleForm({ ...newVehicleForm, inspectionDate: event.target.value.replaceAll('-', '/') })} /></label><label className="form-field"><span>走行距離</span><input inputMode="numeric" value={newVehicleForm.mileage} onChange={(event) => setNewVehicleForm({ ...newVehicleForm, mileage: event.target.value })} placeholder="例：30000" /></label><label className="form-field"><span>車体色</span><input value={newVehicleForm.color} onChange={(event) => setNewVehicleForm({ ...newVehicleForm, color: event.target.value })} placeholder="例：パールホワイト" /></label><label className="form-field"><span>排気量</span><input inputMode="numeric" value={newVehicleForm.displacement} onChange={(event) => setNewVehicleForm({ ...newVehicleForm, displacement: event.target.value })} placeholder="例：1800" /></label><label className="form-field"><span>ミッション</span><input value={newVehicleForm.transmission} onChange={(event) => setNewVehicleForm({ ...newVehicleForm, transmission: event.target.value })} placeholder="例：CVT" /></label></div><div className="sales-create-inline-actions"><button className="button button-secondary" type="button" disabled={registeringVehicle} onClick={() => setNewVehicleOpen(false)}>閉じる</button><button className="button button-primary" type="button" disabled={registeringVehicle} onClick={() => void saveNewVehicle()}><Plus size={15} />{registeringVehicle ? '登録中…' : '車両を登録'}</button></div></div>}{dialogError && <p className="sales-create-error" role="alert">{dialogError}</p>}<div className="modal-footer"><button className="button button-secondary" type="button" onClick={onClose}>キャンセル</button><button className="button button-primary" type="submit" disabled={creating || registeringCustomer || registeringVehicle || !form.customerId}><Plus size={16} />{creating ? '作成中…' : '作成する'}</button></div></form></section></div>
}

function emptyCreateForm(): SalesCreateForm {
  return { type: '見積書', customerId: '', vehicleId: '', dueDate: dateAfter(14), taxRate: 10, taxRounding: '切り捨て', initialItemDescription: '車両本体価格' }
}

function dateAfter(days: number) {
  const date = new Date()
  date.setDate(date.getDate() + days)
  return date.toISOString().slice(0, 10)
}
