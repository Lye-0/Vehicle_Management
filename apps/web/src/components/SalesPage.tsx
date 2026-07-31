import { useEffect, useMemo, useRef, useState, type ChangeEvent, type CSSProperties, type FormEvent } from 'react'
import {
  Archive,
  CarFront,
  ChevronDown,
  ChevronRight,
  Eye,
  FileDown,
  FileText,
  Image as ImageIcon,
  Plus,
  RefreshCw,
  Save,
  Search,
  Trash2,
  UserRound,
  X,
} from 'lucide-react'
import { createCustomer, createVehicle, fetchCustomers, fetchVehicleFile, type Customer, type CustomerInput, type Vehicle, type VehicleInput } from '../lib/customerApi'
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
import { defaultSettings, fetchSettings, type AppSettings, type SalesItemPresetGroupKey, type SalesItemPresetGroups } from '../lib/settingsApi'
import { buildSalesEstimateSections, calculateSalesEstimateTotals, calculateSalesLineAmount, type SalesEstimateEditableBucket, type SalesEstimateSections, type SalesTotals } from '../lib/salesEstimate'
import { buildSalesEstimateSheetSvg, salesEstimateSheetLayout } from '../lib/salesEstimateSheet'

type DocumentFilter = 'すべて' | SalesDocumentType
type SalesDocumentView = 'edit' | 'preview'
type SalesHeaderField = 'number' | 'type' | 'status' | 'customerId' | 'vehicleId' | 'issuedAt' | 'dueDate' | 'note'
type SalesItemField = 'itemType' | 'description' | 'quantity' | 'unit' | 'unitPrice' | 'taxCategory' | 'otherAmount' | 'summary'
type SalesTaxCategoryField = keyof SalesDocumentDetails['requiredDocuments']

const salesDocumentTypeOptions: SalesDocumentType[] = ['見積書', '請求書']
const salesTaxCategories: SalesTaxCategory[] = ['課税', '非課税', '対象外']
const sheetYenFormatter = new Intl.NumberFormat('ja-JP')
const requiredDocumentFields: Array<{ key: keyof SalesDocumentDetails['requiredDocuments']; label: string }> = [
  { key: 'sealCertificate', label: '印鑑証明' },
  { key: 'selfDeclaration', label: '自認書・承諾書' },
  { key: 'residentCard', label: '住民票' },
  { key: 'powerOfAttorney', label: '委任状' },
  { key: 'lightVehicleCertificate', label: '軽自動車住所証明' },
  { key: 'transferCertificate', label: '譲渡証明' },
  { key: 'taxPaymentCertificate', label: '納税証明（下取車）' },
  { key: 'guarantorSealCertificate', label: '保証人印鑑証明' },
]

const estimateBucketDefaults: Record<SalesEstimateEditableBucket, { itemType: string; label: string; taxCategory: SalesTaxCategory }> = {
  vehicleBase: { itemType: '車両本体価格', label: '車両本体価格', taxCategory: '課税' },
  discounts: { itemType: '値引き', label: '値引等', taxCategory: '課税' },
  accessories: { itemType: '付属品・特別仕様', label: '付属品・特別仕様', taxCategory: '課税' },
  vehicleSideLabor: { itemType: '車両販売工賃', label: '工賃', taxCategory: '課税' },
  legalNonTaxable: { itemType: '法定費用', label: '法定費用', taxCategory: '非課税' },
  taxableFees: { itemType: '手続代行費用', label: '手続代行費用', taxCategory: '課税' },
  nonTaxableFees: { itemType: '実費・預託金', label: '実費・預託金', taxCategory: '非課税' },
  tradeIns: { itemType: '下取車', label: '下取車価格', taxCategory: '対象外' },
}

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
  const documentsRef = useRef<SalesDocument[]>([])

  function replaceDocuments(updater: (current: SalesDocument[]) => SalesDocument[]) {
    const nextDocuments = updater(documentsRef.current)
    documentsRef.current = nextDocuments
    setDocuments(nextDocuments)
  }

  useEffect(() => {
    let cancelled = false
    setLoading(true)
    Promise.all([fetchSalesDocuments(), fetchCustomers(), fetchSettings()])
      .then(([nextDocuments, nextCustomers, nextSettings]) => {
        if (cancelled) return
        documentsRef.current = nextDocuments
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
  const selectedTotals = selectedDocument ? calculateSalesEstimateTotals(selectedDocument, settings.tax.rounding) : null

  function updateLineItem(itemId: string, field: SalesItemField, value: string) {
    if (!selectedDocument) return
    const nextValue = field === 'description' || field === 'itemType' || field === 'unit' || field === 'taxCategory' || field === 'summary' ? value : Number(value)
    replaceDocuments((current) => current.map((document) => document.id !== selectedDocument.id ? document : {
      ...document,
      items: document.items.map((item) => item.id === itemId ? { ...item, [field]: nextValue } : item),
    }))
    markDirty()
  }

  function updateEstimateSheetLine(bucket: SalesEstimateEditableBucket, index: number, patch: { label?: string; amount?: number }) {
    if (!selectedDocument) return
    const defaults = estimateBucketDefaults[bucket]
    replaceDocuments((current) => current.map((document) => {
      if (document.id !== selectedDocument.id) return document
      const line = buildSalesEstimateSections(document)[bucket][index]
      if (line) {
        const nextLabel = patch.label ?? line.label
        const nextAmount = patch.amount ?? line.amount
        if (line.id === 'recycle-fee') {
          if (patch.label !== undefined && !patch.label.trim()) return { ...document, details: { ...document.details, recycleFee: 0 } }
          if (patch.label !== undefined && patch.label.trim() !== line.label) {
            const item: SalesLineItem = { id: `item-${Date.now()}-${bucket}-${index}`, itemType: defaults.itemType, description: patch.label.trim(), quantity: 1, unit: '式', unitPrice: nextAmount, taxCategory: defaults.taxCategory, otherAmount: 0, summary: '' }
            return { ...document, details: { ...document.details, recycleFee: 0 }, items: [...document.items, item] }
          }
          return { ...document, details: { ...document.details, recycleFee: nextAmount } }
        }
        if ((patch.label !== undefined && !patch.label.trim()) || (!nextLabel.trim() && nextAmount === 0)) return { ...document, items: document.items.filter((item) => item.id !== line.id) }
        return {
          ...document,
          items: document.items.map((item) => item.id === line.id ? {
            ...item,
            itemType: defaults.itemType,
            description: nextLabel,
            quantity: 1,
            unit: item.unit || '式',
            unitPrice: nextAmount,
            otherAmount: 0,
            taxCategory: defaults.taxCategory,
          } : item),
        }
      }
      const nextLabel = patch.label?.trim() || defaults.label
      const nextAmount = patch.amount ?? 0
      if (!patch.label?.trim() && patch.amount === undefined) return document
      const newItem: SalesLineItem = {
        id: `item-${Date.now()}-${bucket}-${index}`,
        itemType: defaults.itemType,
        description: nextLabel,
        quantity: 1,
        unit: '式',
        unitPrice: nextAmount,
        taxCategory: defaults.taxCategory,
        otherAmount: 0,
        summary: '',
      }
      return { ...document, items: [...document.items, newItem] }
    }))
    markDirty()
  }

  function addLineItem() {
    if (!selectedDocument) return
    const newItem: SalesLineItem = { id: `item-${Date.now()}`, itemType: 'その他', description: '', quantity: 1, unit: '式', unitPrice: 0, taxCategory: '課税', otherAmount: 0, summary: '' }
    replaceDocuments((current) => current.map((document) => document.id === selectedDocument.id ? { ...document, items: [...document.items, newItem] } : document))
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
      details: { ...selectedDocument.details, selectedImageAttachmentId: '', customerOverride: null, vehicleOverride: null },
    } : {}
    replaceDocuments((current) => current.map((document) => document.id !== selectedDocument.id ? document : { ...document, [field]: value, ...relationPatch }))
    markDirty()
  }

  function updateDetails(patch: Partial<SalesDocumentDetails>) {
    if (!selectedDocument) return
    replaceDocuments((current) => current.map((document) => document.id !== selectedDocument.id ? document : { ...document, details: { ...document.details, ...patch } }))
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
    updateDetails({ requiredDocuments: { ...selectedDocument.details.requiredDocuments, [field]: value, ...(field === 'selfDeclaration' ? { warrantyCertificate: value === true } : {}) } })
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
      replaceDocuments((current) => current.filter((document) => document.id !== selectedDocument.id))
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
    replaceDocuments((current) => current.map((document) => document.id === selectedDocument.id ? { ...document, items: document.items.filter((item) => item.id !== itemId) } : document))
    markDirty()
  }

  async function saveSelectedDocument() {
    if (!selectedDocument || saving) return
    const documentToSave = documentsRef.current.find((document) => document.id === selectedDocument.id)
    if (!documentToSave) return
    setSaving(true)
    setSaved(false)
    try {
      const nextDocument = await updateSalesDocument(documentToSave, settings.tax.rounding)
      replaceDocuments((current) => current.map((document) => document.id === nextDocument.id ? nextDocument : document))
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
      replaceDocuments((current) => [newDocument, ...current])
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
      <div className="page-header sales-page-header"><div><span className="page-eyebrow">販売書類</span><h1>販売</h1><p>見積書・請求書を車両情報と連動して管理します。</p></div><button className="button button-primary" type="button" onClick={openCreateDialog}><Plus size={18} />販売書類を作成</button></div>
      {syncError && <div className="customer-sync-status is-error"><span>{syncError}</span><button className="text-button" type="button" onClick={() => window.location.reload()}>再読み込み</button></div>}
      {loading && <div className="customer-sync-status"><span>販売書類を読み込んでいます。</span></div>}
      <div className="sales-toolbar"><label className="sales-search"><Search size={18} /><span className="sr-only">販売書類を検索</span><input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="書類番号、顧客名、車名で検索" /></label><div className="sales-filter-tabs" aria-label="書類種別"><button className={filterType === 'すべて' ? 'is-active' : ''} type="button" onClick={() => setFilterType('すべて')}>すべて</button>{salesDocumentTypeOptions.map((type) => <button className={filterType === type ? 'is-active' : ''} key={type} type="button" onClick={() => setFilterType(type)}>{type}</button>)}</div></div>
      <div className="sales-workspace"><SalesDocumentList documents={filteredDocuments} selectedDocumentId={selectedDocument?.id ?? ''} rounding={settings.tax.rounding} onSelect={setSelectedDocumentId} />{selectedDocument && selectedTotals ? <SalesDocumentDetail document={selectedDocument} totals={selectedTotals} shopName={settings.shop.name} settings={settings} itemPresets={settings.salesItemPresets} customers={customers} view={documentView} dirty={dirty} saving={saving} saved={saved} onViewChange={setDocumentView} onUpdateHeader={updateHeader} onUpdateDetails={updateDetails} onUpdateTradeIn={updateTradeIn} onUpdateCredit={updateCredit} onUpdateRequiredDocument={updateRequiredDocument} onUpdateItem={updateLineItem} onUpdateSheetLine={updateEstimateSheetLine} onAddItem={addLineItem} onRemoveItem={removeLineItem} onSave={saveSelectedDocument} onArchive={() => void archiveSelectedDocument()} onPdfDownload={() => void downloadSalesDocumentPdf(selectedDocument, settings)} onPdfPreview={() => void previewSalesDocumentPdf(selectedDocument, settings)} /> : <div className="panel sales-empty"><FileText size={30} /><strong>{loading ? '販売書類を読み込んでいます' : '販売書類が見つかりません'}</strong><span>{loading ? 'しばらくお待ちください。' : '検索条件または書類種別を変更してください。'}</span></div>}</div>
      {createDialogOpen && <SalesDocumentDialog form={createForm} customers={customers} creating={creating} onChange={setCreateForm} onClose={() => setCreateDialogOpen(false)} onSubmit={createDocument} onCreateCustomer={registerCustomer} onCreateVehicle={registerVehicle} />}
    </>
  )
}

function SalesDocumentList({ documents, selectedDocumentId, rounding, onSelect }: { documents: SalesDocument[]; selectedDocumentId: string; rounding: AppSettings['tax']['rounding']; onSelect: (id: string) => void }) {
  return <section className="panel sales-list-panel"><div className="sales-list-header"><div><h2>販売書類</h2><span>書類を選択すると詳細を表示します</span></div><span className="results-count">{documents.length}件</span></div><div className="sales-document-list">{documents.map((document) => <button className={`sales-document-card${document.id === selectedDocumentId ? ' is-selected' : ''}`} key={document.id} type="button" onClick={() => onSelect(document.id)}><div className="sales-card-top"><span className={`sales-type-badge sales-type-${document.type}`}>{document.type}</span><StatusTag status={document.status} /><ChevronRight size={16} /></div><strong className="sales-card-number">{document.number}</strong><span className="sales-card-customer"><UserRound size={14} /><strong>{document.customerName}</strong></span><span className="sales-card-vehicle"><CarFront size={14} />{document.vehicle || '車両未指定'}{document.plate ? ` ・ ${document.plate}` : ''}</span><div className="sales-card-bottom"><span>{document.issuedAt}</span><strong>{formatYen(calculateTotals(document, rounding).total)}</strong></div></button>)}</div></section>
}

function SalesDocumentDetail({ document, totals, shopName, settings, itemPresets, customers, view, dirty, saving, saved, onViewChange, onUpdateHeader, onUpdateDetails, onUpdateTradeIn, onUpdateCredit, onUpdateRequiredDocument, onUpdateItem, onUpdateSheetLine, onAddItem, onRemoveItem, onSave, onArchive, onPdfDownload, onPdfPreview }: { document: SalesDocument; totals: SalesTotals; shopName: string; settings: AppSettings; itemPresets: string[]; customers: Customer[]; view: SalesDocumentView; dirty: boolean; saving: boolean; saved: boolean; onViewChange: (view: SalesDocumentView) => void; onUpdateHeader: (field: SalesHeaderField, value: string) => void; onUpdateDetails: (patch: Partial<SalesDocumentDetails>) => void; onUpdateTradeIn: (field: keyof SalesDocumentDetails['tradeIn'], value: string) => void; onUpdateCredit: (field: keyof SalesDocumentDetails['credit'], value: string | boolean) => void; onUpdateRequiredDocument: (field: keyof SalesDocumentDetails['requiredDocuments'], value: string | boolean) => void; onUpdateItem: (itemId: string, field: SalesItemField, value: string) => void; onUpdateSheetLine: SalesPreviewProps['onUpdateSheetLine']; onAddItem: () => void; onRemoveItem: (itemId: string) => void; onSave: () => void; onArchive: () => void; onPdfDownload: () => void; onPdfPreview: () => void }) {
  return <section className="panel sales-detail-panel"><div className="sales-detail-header"><div className="sales-detail-title"><div><span className={`sales-type-badge sales-type-${document.type}`}>{document.type}</span><h2>{document.number}</h2><small>{document.issuedAt} 作成 ・ 発行元 {shopName}</small></div><StatusTag status={document.status} /></div><div className="sales-detail-actions"><button className="button button-secondary" type="button" onClick={onPdfPreview}><Eye size={16} />PDFで確認</button><button className="button button-secondary" type="button" disabled={!dirty || saving} onClick={onSave}><Save size={16} />{saving ? '保存中…' : saved ? '保存済み' : '保存'}</button><button className="button button-secondary" type="button" onClick={onPdfDownload}><FileDown size={16} />PDF保存</button><button className="button button-danger" type="button" disabled={saving} onClick={onArchive}><Archive size={16} />アーカイブ</button></div></div><div className="sales-document-tabs" role="tablist" aria-label="販売書類の表示"><button id="sales-document-edit-tab" className={view === 'edit' ? 'is-active' : ''} type="button" role="tab" aria-selected={view === 'edit'} aria-controls="sales-document-edit-panel" onClick={() => onViewChange('edit')}><FileText size={16} />入力</button><button id="sales-document-preview-tab" className={view === 'preview' ? 'is-active' : ''} type="button" role="tab" aria-selected={view === 'preview'} aria-controls="sales-document-preview-panel" onClick={() => onViewChange('preview')}><Eye size={16} />プレビュー</button></div>{view === 'edit' ? <div id="sales-document-edit-panel" className="sales-detail-content" role="tabpanel" aria-labelledby="sales-document-edit-tab"><SalesDocumentEditor document={document} totals={totals} itemPresets={itemPresets} customers={customers} onUpdateHeader={onUpdateHeader} onUpdateDetails={onUpdateDetails} onUpdateTradeIn={onUpdateTradeIn} onUpdateCredit={onUpdateCredit} onUpdateRequiredDocument={onUpdateRequiredDocument} onUpdateItem={onUpdateItem} onAddItem={onAddItem} onRemoveItem={onRemoveItem} /></div> : <div id="sales-document-preview-panel" className="sales-detail-content" role="tabpanel" aria-labelledby="sales-document-preview-tab"><SalesDocumentPreview document={document} totals={totals} settings={settings} itemPresets={itemPresets} customers={customers} onUpdateHeader={onUpdateHeader} onUpdateDetails={onUpdateDetails} onUpdateItem={onUpdateItem} onUpdateSheetLine={onUpdateSheetLine} onAddItem={onAddItem} onRemoveItem={onRemoveItem} /></div>}</section>
}

function SalesDocumentEditor(props: { document: SalesDocument; totals: SalesTotals; itemPresets: string[]; customers: Customer[]; onUpdateHeader: (field: SalesHeaderField, value: string) => void; onUpdateDetails: (patch: Partial<SalesDocumentDetails>) => void; onUpdateTradeIn: (field: keyof SalesDocumentDetails['tradeIn'], value: string) => void; onUpdateCredit: (field: keyof SalesDocumentDetails['credit'], value: string | boolean) => void; onUpdateRequiredDocument: (field: keyof SalesDocumentDetails['requiredDocuments'], value: string | boolean) => void; onUpdateItem: (itemId: string, field: SalesItemField, value: string) => void; onAddItem: () => void; onRemoveItem: (itemId: string) => void }) {
  const { document, customers, onUpdateHeader } = props
  const selectedCustomer = customers.find((customer) => customer.id === document.customerId)
  return <>
    <section className="document-header-editor">
      <div className="document-header-editor-title"><div><h3>書類基本情報</h3><span>顧客・車両、日付、状態などの基本情報を入力できます。</span></div></div>
      <div className="form-grid">
        <label className="form-field"><span>書類種別</span><select value={document.type} onChange={(event) => onUpdateHeader('type', event.target.value)}><option>見積書</option><option>請求書</option></select></label>
        <label className="form-field"><span>状態</span><select value={document.status} onChange={(event) => onUpdateHeader('status', event.target.value)}><option>下書き</option><option>発行済み</option><option>入金待ち</option></select></label>
        <label className="form-field"><span>顧客</span><select value={document.customerId} onChange={(event) => onUpdateHeader('customerId', event.target.value)}>{customers.map((customer) => <option key={customer.id} value={customer.id}>{customer.name}</option>)}</select></label>
        <label className="form-field"><span>対象車両</span><select value={document.vehicleId ?? ''} onChange={(event) => onUpdateHeader('vehicleId', event.target.value)}><option value="">車両を指定しない</option>{selectedCustomer?.vehicles.map((vehicle) => <option key={vehicle.id} value={vehicle.id}>{vehicle.maker} {vehicle.model} ・ {vehicle.plate || '登録番号なし'}</option>)}</select></label>
        <label className="form-field"><span>書類日付</span><input type="date" value={document.issuedAt.replaceAll('/', '-')} onChange={(event) => onUpdateHeader('issuedAt', event.target.value.replaceAll('-', '/'))} /></label>
        <label className="form-field"><span>支払期限</span><input type="date" value={document.dueDate.replaceAll('/', '-')} onChange={(event) => onUpdateHeader('dueDate', event.target.value.replaceAll('-', '/'))} /></label>
      </div>
    </section>
    <details className="sales-details-accordion">
      <summary><span>詳細</span><ChevronDown size={16} aria-hidden="true" /></summary>
      <div className="sales-details-accordion-content" />
    </details>
  </>
}

function SalesDocumentPreview({ document, totals, settings, itemPresets, customers, onUpdateHeader, onUpdateDetails, onUpdateItem, onUpdateSheetLine, onAddItem, onRemoveItem }: { document: SalesDocument; totals: SalesTotals; settings: AppSettings; itemPresets: string[]; customers: Customer[]; onUpdateHeader: (field: SalesHeaderField, value: string) => void; onUpdateDetails: (patch: Partial<SalesDocumentDetails>) => void; onUpdateItem: (itemId: string, field: SalesItemField, value: string) => void; onUpdateSheetLine: SalesPreviewProps['onUpdateSheetLine']; onAddItem: () => void; onRemoveItem: (itemId: string) => void }) {
  return <SalesEstimatePreview document={document} totals={totals} settings={settings} itemPresets={itemPresets} customers={customers} onUpdateHeader={onUpdateHeader} onUpdateDetails={onUpdateDetails} onUpdateItem={onUpdateItem} onUpdateSheetLine={onUpdateSheetLine} onAddItem={onAddItem} onRemoveItem={onRemoveItem} />
  /*
  const selectedCustomer = customers.find((customer) => customer.id === document.customerId)
  const selectedVehicle = selectedCustomer?.vehicles.find((vehicle) => vehicle.id === document.vehicleId)
  const customer = document.customerDetails ?? mapCustomerDetails(selectedCustomer)
  const vehicle = document.vehicleDetails ?? (selectedVehicle ? mapVehicleDetails(selectedVehicle) : null)
  const details = document.details
  const shopLines = [settings.shop.postalCode ? `〒${settings.shop.postalCode}` : '', settings.shop.address, settings.shop.phone ? `TEL ${settings.shop.phone}` : '', settings.shop.representative ? `担当 ${settings.shop.representative}` : '', settings.shop.registrationNumber ? `登録番号 ${settings.shop.registrationNumber}` : ''].filter(Boolean)
  const paymentNote = settings.document.paymentNote || '店頭または指定口座へお支払いください。'
  const bankAccount = [settings.shop.bankName, settings.shop.bankAccount].filter(Boolean).join(' / ') || '未設定'
  return <div className="sales-preview-area"><div className="sales-preview-toolbar"><div><strong>見積書プレビュー</strong><span>実際のPDFと同じ情報配置で確認できます。基本項目と明細はこの画面から編集できます。</span></div><button className="button button-secondary" type="button" onClick={onPdfPreview}><Eye size={16} />PDFで確認</button></div><article className="sales-document-paper sales-estimate-paper"><header className="sales-estimate-paper-header"><div className="sales-estimate-title-block"><select className="sales-estimate-title" aria-label="書類種別" value={document.type} onChange={(event) => onUpdateHeader('type', event.target.value)}><option>見積書</option><option>請求書</option></select><span>{details.salesCategory || '販売書類'}</span></div><div className="sales-estimate-meta-table"><div><span>日付</span><input type="date" aria-label="発行日" value={document.issuedAt.replaceAll('/', '-')} onChange={(event) => onUpdateHeader('issuedAt', event.target.value.replaceAll('-', '/'))} /></div><div><span>販売区分</span><strong>{details.salesCategory || '未設定'}</strong></div><div><span>担当</span><strong>{details.staffName || '未設定'}</strong></div><div><span>見積番号</span><input aria-label="書類番号" value={document.number} onChange={(event) => onUpdateHeader('number', event.target.value)} /></div><div><span>ページ</span><strong>1</strong></div></div></header><section className="sales-estimate-customer-grid"><div className="sales-estimate-customer-box"><div className="sales-estimate-cell-label">お名前</div><div className="sales-estimate-cell-value"><strong>{customer.name || '未設定'} {details.customerHonorific}</strong><small>{customer.kana || 'ふりがな未登録'}</small></div><div className="sales-estimate-cell-label">ご住所</div><div className="sales-estimate-cell-value"><span>{customer.postalCode ? `〒${customer.postalCode}` : ''}</span><span>{customer.address || '住所未登録'}</span></div></div><div className="sales-estimate-contact-box"><div className="sales-estimate-cell-label">生年月日</div><div>{details.customerBirthDate || '未設定'}</div><div className="sales-estimate-cell-label">電話番号</div><div>{customer.phone || '未登録'}</div><div className="sales-estimate-cell-label">勤務先等</div><div>{details.customerEmployer || '未設定'}</div><div className="sales-estimate-cell-label">連絡先TEL</div><div>{details.customerContactPhone || '未設定'}</div></div></section><EstimateVehicleTable vehicle={vehicle} /><EstimateTradeInTable details={details} /><section className="sales-estimate-summary-top"><div className="sales-estimate-amount-card"><span>お見積金額</span><strong>{formatYen(totals.total)}</strong></div><div className="sales-estimate-tax-card"><div><span>課税対象額</span><strong>{formatYen(totals.taxableSubtotal)}</strong></div><div><span>消費税（{formatPercent(document.taxRate)}）</span><strong>{formatYen(totals.tax)}</strong></div><div><span>非課税・対象外</span><strong>{formatYen(totals.nonTaxableSubtotal + totals.outOfScopeSubtotal)}</strong></div></div></section><div className="sales-estimate-section-title"><h3>明細</h3><span /><button className="text-button" type="button" onClick={onAddItem}><Plus size={15} />明細を追加</button></div><div className="sales-estimate-items-table"><div className="sales-estimate-items-head"><span>No.</span><span>作業内容／部品名等</span><span>数量</span><span>単位</span><span>部品単価</span><span>部品金額</span><span>技術料・他</span><span>摘要</span></div>{document.items.map((item, index) => <div className="sales-estimate-item-row" key={item.id}><span>{index + 1}</span><input list="sales-preview-item-presets" aria-label="プレビューの明細内容" value={item.description} onChange={(event) => onUpdateItem(item.id, 'description', event.target.value)} placeholder="明細内容" /><input aria-label="プレビューの数量" type="number" min="0" value={item.quantity} onChange={(event) => onUpdateItem(item.id, 'quantity', event.target.value)} /><input aria-label="プレビューの単位" value={item.unit} onChange={(event) => onUpdateItem(item.id, 'unit', event.target.value)} /><input aria-label="プレビューの単価" type="number" value={item.unitPrice} onChange={(event) => onUpdateItem(item.id, 'unitPrice', event.target.value)} /><strong>{formatYen(Math.round(item.quantity * item.unitPrice))}</strong><strong>{formatYen(item.otherAmount)}</strong><input aria-label="プレビューの摘要" value={item.summary} onChange={(event) => onUpdateItem(item.id, 'summary', event.target.value)} /><button className="sales-estimate-item-remove" type="button" aria-label="明細を削除" onClick={() => onRemoveLineItemGuard(item.id, document.items.length, onRemoveItem)}><Trash2 size={14} /></button></div>)}</div><datalist id="sales-preview-item-presets">{itemPresets.map((preset) => <option key={preset} value={preset} />)}</datalist><div className="sales-estimate-items-total"><span>明細合計</span><strong>{formatYen(totals.subtotal)}</strong><span>消費税</span><strong>{formatYen(totals.tax)}</strong><span>合計</span><strong>{formatYen(totals.total)}</strong></div><section className="sales-estimate-bottom-grid"><div><div className="sales-estimate-recycle"><span>リサイクル料金（預託金）</span><strong>{formatYen(details.recycleFee)}</strong></div><div className="sales-estimate-credit"><h4>クレジットお支払いプラン</h4>{details.credit.enabled ? <div><span>{details.credit.paymentCount || '回数未設定'}</span><span>月々 {formatYen(details.credit.monthlyPayment)}</span><span>初回 {formatYen(details.credit.initialPayment)}</span><span>ボーナス {details.credit.bonusMonths || '月未設定'} / {formatYen(details.credit.bonusPayment)}</span></div> : <p>利用なし</p>}</div><div className="sales-estimate-required"><h4>必要書類</h4><p>{requiredDocumentLabels(details).join(' ／ ') || '未確認'}</p></div></div><div className="sales-estimate-company"><strong>{settings.shop.name || '店舗名未設定'}</strong>{shopLines.slice(0, 4).map((line) => <span key={line}>{line}</span>)}<div className="sales-estimate-company-payment"><span>お支払いについて</span><p>{paymentNote}</p><span>振込先</span><p>{bankAccount}</p></div></div></section><footer className="sales-paper-footer"><span>{document.note || settings.document.footerNote || '見積条件は担当者へご確認ください。'}</span><span>ページ 1</span></footer></article></div>
  */
}

type SalesPreviewProps = {
  document: SalesDocument
  totals: SalesTotals
  settings: AppSettings
  itemPresets: string[]
  customers: Customer[]
  onUpdateHeader: (field: SalesHeaderField, value: string) => void
  onUpdateDetails: (patch: Partial<SalesDocumentDetails>) => void
  onUpdateItem: (itemId: string, field: SalesItemField, value: string) => void
  onUpdateSheetLine: (bucket: SalesEstimateEditableBucket, index: number, patch: { label?: string; amount?: number }) => void
  onAddItem: () => void
  onRemoveItem: (itemId: string) => void
  onPdfPreview?: () => void
}

function SalesEstimatePreview(props: SalesPreviewProps) {
  return <SalesEstimateExactPreview {...props} />
  /*
  const selectedCustomer = customers.find((customer) => customer.id === document.customerId)
  const selectedVehicle = selectedCustomer?.vehicles.find((vehicle) => vehicle.id === document.vehicleId)
  const customer = document.customerDetails ?? mapCustomerDetails(selectedCustomer)
  const vehicle = document.vehicleDetails ?? (selectedVehicle ? mapVehicleDetails(selectedVehicle) : null)
  const details = document.details
  const shopLines = [settings.shop.postalCode ? `〒${settings.shop.postalCode}` : '', settings.shop.address, settings.shop.phone ? `TEL ${settings.shop.phone}` : '', settings.shop.representative ? `担当 ${settings.shop.representative}` : '', settings.shop.registrationNumber ? `登録番号 ${settings.shop.registrationNumber}` : ''].filter(Boolean)
  const paymentNote = settings.document.paymentNote || '店頭または指定口座へお支払いください。'
  const bankAccount = [settings.shop.bankName, settings.shop.bankAccount].filter(Boolean).join(' / ') || '未設定'
  return <div className="sales-preview-area"><div className="sales-preview-toolbar"><div><strong>見積書プレビュー</strong><span>実際のPDFと同じ情報配置で確認できます。基本項目と明細はこの画面から編集できます。</span></div><button className="button button-secondary" type="button" onClick={onPdfPreview}><Eye size={16} />PDFで確認</button></div><article className="sales-document-paper sales-estimate-paper"><header className="sales-estimate-paper-header"><div className="sales-estimate-title-block"><select className="sales-estimate-title" aria-label="書類種別" value={document.type} onChange={(event) => onUpdateHeader('type', event.target.value)}><option>見積書</option><option>請求書</option></select><span>{details.salesCategory || '販売書類'}</span></div><div className="sales-estimate-meta-table"><div><span>日付</span><input type="date" aria-label="発行日" value={document.issuedAt.replaceAll('/', '-')} onChange={(event) => onUpdateHeader('issuedAt', event.target.value.replaceAll('-', '/'))} /></div><div><span>販売区分</span><strong>{details.salesCategory || '未設定'}</strong></div><div><span>担当</span><strong>{details.staffName || '未設定'}</strong></div><div><span>見積番号</span><input aria-label="書類番号" value={document.number} onChange={(event) => onUpdateHeader('number', event.target.value)} /></div><div><span>ページ</span><strong>1</strong></div></div></header><section className="sales-estimate-customer-grid"><div className="sales-estimate-customer-box"><div className="sales-estimate-cell-label">お名前</div><div className="sales-estimate-cell-value"><strong>{customer.name || '未設定'} {details.customerHonorific}</strong><small>{customer.kana || 'ふりがな未登録'}</small></div><div className="sales-estimate-cell-label">ご住所</div><div className="sales-estimate-cell-value"><span>{customer.postalCode ? `〒${customer.postalCode}` : ''}</span><span>{customer.address || '住所未登録'}</span></div></div><div className="sales-estimate-contact-box"><div className="sales-estimate-cell-label">生年月日</div><div>{details.customerBirthDate || '未設定'}</div><div className="sales-estimate-cell-label">電話番号</div><div>{customer.phone || '未登録'}</div><div className="sales-estimate-cell-label">勤務先等</div><div>{details.customerEmployer || '未設定'}</div><div className="sales-estimate-cell-label">連絡先TEL</div><div>{details.customerContactPhone || '未設定'}</div></div></section><EstimateVehicleTable vehicle={vehicle} /><EstimateTradeInTable details={details} /><section className="sales-estimate-summary-top"><div className="sales-estimate-amount-card"><span>お見積金額</span><strong>{formatYen(totals.total)}</strong></div><div className="sales-estimate-tax-card"><div><span>課税対象額</span><strong>{formatYen(totals.taxableSubtotal)}</strong></div><div><span>消費税（{formatPercent(document.taxRate)}）</span><strong>{formatYen(totals.tax)}</strong></div><div><span>非課税・対象外</span><strong>{formatYen(totals.nonTaxableSubtotal + totals.outOfScopeSubtotal)}</strong></div></div></section><div className="sales-estimate-status-line"><span>支払期限：{document.dueDate || '未設定'}</span><span>状態：{document.status}</span></div><div className="sales-estimate-section-title"><h3>明細</h3><span /><button className="text-button" type="button" onClick={onAddItem}><Plus size={15} />明細を追加</button></div><div className="sales-estimate-items-table"><div className="sales-estimate-items-head"><span>No.</span><span>作業内容／部品名等</span><span>数量</span><span>単位</span><span>部品単価</span><span>部品金額</span><span>技術料・他</span><span>摘要・課税</span></div>{document.items.map((item, index) => <div className="sales-estimate-item-row" key={item.id}><span>{index + 1}</span><input list="sales-preview-item-presets" aria-label="プレビューの明細内容" value={item.description} onChange={(event) => onUpdateItem(item.id, 'description', event.target.value)} placeholder="明細内容" /><input aria-label="プレビューの数量" type="number" min="0" value={item.quantity} onChange={(event) => onUpdateItem(item.id, 'quantity', event.target.value)} /><input aria-label="プレビューの単位" value={item.unit} onChange={(event) => onUpdateItem(item.id, 'unit', event.target.value)} /><input aria-label="プレビューの単価" type="number" value={item.unitPrice} onChange={(event) => onUpdateItem(item.id, 'unitPrice', event.target.value)} /><strong>{formatYen(Math.round(item.quantity * item.unitPrice))}</strong><strong>{formatYen(item.otherAmount)}</strong><div className="sales-estimate-summary-cell"><input aria-label="プレビューの摘要" value={item.summary} onChange={(event) => onUpdateItem(item.id, 'summary', event.target.value)} /><select aria-label="プレビューの課税区分" value={item.taxCategory} onChange={(event) => onUpdateItem(item.id, 'taxCategory', event.target.value)}>{salesTaxCategories.map((category) => <option key={category}>{category}</option>)}</select></div><button className="sales-estimate-item-remove" type="button" aria-label="明細を削除" onClick={() => onRemoveLineItemGuard(item.id, document.items.length, onRemoveItem)}><Trash2 size={14} /></button></div>)}</div><datalist id="sales-preview-item-presets">{itemPresets.map((preset) => <option key={preset} value={preset} />)}</datalist><div className="sales-estimate-items-total"><span>明細合計</span><strong>{formatYen(totals.subtotal)}</strong><span>消費税</span><strong>{formatYen(totals.tax)}</strong><span>合計</span><strong>{formatYen(totals.total)}</strong></div><section className="sales-estimate-bottom-grid"><div><div className="sales-estimate-recycle"><span>リサイクル料金（預託金）</span><strong>{formatYen(details.recycleFee)}</strong></div><div className="sales-estimate-payment-summary"><span>頭金・現金・他</span><strong>{formatYen(details.downPayment)}</strong><span>残金・所要資金</span><strong>{formatYen(details.remainingPayment)}</strong></div><div className="sales-estimate-credit"><h4>クレジットお支払いプラン</h4>{details.credit.enabled ? <div><span>{details.credit.paymentCount || '回数未設定'}</span><span>手数料 {formatYen(details.credit.fee)}</span><span>月々 {formatYen(details.credit.monthlyPayment)}</span><span>初回 {formatYen(details.credit.initialPayment)}</span><span>ボーナス {details.credit.bonusMonths || '月未設定'} / {formatYen(details.credit.bonusPayment)}</span></div> : <p>利用なし</p>}</div><div className="sales-estimate-required"><h4>必要書類</h4><p>{requiredDocumentLabels(details).join(' ／ ') || '未確認'}</p></div></div><div className="sales-estimate-company"><strong>{settings.shop.name || '店舗名未設定'}</strong>{shopLines.slice(0, 4).map((line) => <span key={line}>{line}</span>)}<div className="sales-estimate-company-payment"><span>お支払いについて</span><p>{paymentNote}</p><span>振込先</span><p>{bankAccount}</p></div></div></section><footer className="sales-paper-footer"><span>{document.note || settings.document.footerNote || '見積条件は担当者へご確認ください。'}</span><span>ページ 1</span></footer></article></div>
  */
}

function SalesEstimateExactPreview({ document, customers, onUpdateHeader, onUpdateDetails, onUpdateSheetLine, settings }: SalesPreviewProps) {
  const selectedCustomer = customers.find((customer) => customer.id === document.customerId)
  const selectedVehicle = selectedCustomer?.vehicles.find((vehicle) => vehicle.id === document.vehicleId)
  const imageAttachments = selectedVehicle?.attachments.filter((attachment) => attachment.type === 'image') ?? []
  const selectedAttachment = imageAttachments.find((attachment) => attachment.id === document.details.selectedImageAttachmentId)
  const imageState = useVehicleAttachmentUrl(document.vehicleId, selectedAttachment?.id ?? '')
  const sheetSvg = buildSalesEstimateSheetSvg(document, settings, { imageHref: imageState.url })
  const sections = buildSalesEstimateSections(document)
  // Keep the previous implementation available while the new fixed A4 sheet is stabilized.
  void SalesEstimatePreviewLayout

  return <div className="sales-preview-area">
    <div className="sales-estimate-image-control">
      <div><strong><ImageIcon size={16} />帳票に表示する車両画像</strong><span>{selectedVehicle ? `${selectedVehicle.maker} ${selectedVehicle.model}の添付画像から選択できます。` : '対象車両を選択すると添付画像を選択できます。'}</span></div>
      <div className="sales-estimate-image-select">
        <select aria-label="帳票に表示する車両画像" value={document.details.selectedImageAttachmentId} disabled={!imageAttachments.length} onChange={(event) => onUpdateDetails({ selectedImageAttachmentId: event.target.value })}>
          <option value="">画像なし（顧客情報を拡張）</option>
          {imageAttachments.map((attachment) => <option key={attachment.id} value={attachment.id}>{attachment.name}</option>)}
        </select>
        {imageState.loading && <small><RefreshCw size={13} className="is-spinning" />画像を読み込んでいます…</small>}
        {imageState.error && <small className="is-error">画像を表示できないため、顧客情報表示に切り替えています。</small>}
        {!imageAttachments.length && <small>画像ファイルが登録されていません。</small>}
      </div>
    </div>
    <div className="sales-estimate-sheet-frame">
      <div className="sales-estimate-sheet" dangerouslySetInnerHTML={{ __html: sheetSvg }} />
      <SalesEstimateSheetEditor
        document={document}
        hasImage={Boolean(imageState.url)}
        sections={sections}
        itemPresetGroups={settings.salesItemPresetGroups}
        onUpdateDetails={onUpdateDetails}
        onUpdateHeader={onUpdateHeader}
        onUpdateLine={onUpdateSheetLine}
      />
    </div>
  </div>
}

type SheetLinePosition = {
  bucket: SalesEstimateEditableBucket
  presetGroup: SalesItemPresetGroupKey
  index: number
  x: number
  y: number
  width: number
  labelWidth: number
  height: number
  fixedLabel?: string
  menuUp?: boolean
}

const salesEstimateSheetLinePositions: SheetLinePosition[] = [
  { bucket: 'vehicleBase', presetGroup: 'vehiclePrice', index: 0, x: salesEstimateSheetLayout.vehicle.x, y: salesEstimateSheetLayout.lowerY + 39, width: salesEstimateSheetLayout.vehicle.width, labelWidth: 198, height: 35, fixedLabel: '車両本体価格' },
  { bucket: 'discounts', presetGroup: 'vehiclePrice', index: 0, x: salesEstimateSheetLayout.vehicle.x, y: salesEstimateSheetLayout.lowerY + 74, width: salesEstimateSheetLayout.vehicle.width, labelWidth: 198, height: 35, fixedLabel: '値引等' },
  { bucket: 'vehicleSideLabor', presetGroup: 'vehiclePrice', index: 0, x: salesEstimateSheetLayout.vehicle.x, y: salesEstimateSheetLayout.lowerY + 179, width: salesEstimateSheetLayout.vehicle.width, labelWidth: 198, height: 35 },
  ...salesEstimateSheetLayout.fee.groups.flatMap((group) => Array.from({ length: group.rows }, (_, index) => ({
    bucket: group.bucket,
    presetGroup: 'fees' as const,
    index,
    x: salesEstimateSheetLayout.fee.detailX,
    y: group.startY + index * 26,
    width: salesEstimateSheetLayout.fee.detailWidth,
    labelWidth: salesEstimateSheetLayout.fee.detailLabelWidth,
    height: 26,
    menuUp: group.startY + index * 26 >= 1040,
  }))),
  ...Array.from({ length: salesEstimateSheetLayout.accessory.rowCount }, (_, index) => ({
    bucket: 'accessories' as const,
    presetGroup: 'accessories' as const,
    index,
    x: salesEstimateSheetLayout.accessory.x,
    y: salesEstimateSheetLayout.accessory.detailY + index * salesEstimateSheetLayout.accessory.rowHeight,
    width: salesEstimateSheetLayout.accessory.width,
    labelWidth: salesEstimateSheetLayout.accessory.nameWidth,
    height: salesEstimateSheetLayout.accessory.rowHeight,
    menuUp: index > 8,
  })),
]

export function SalesEstimateSheetEditor({ document, hasImage, sections, itemPresetGroups, onUpdateDetails, onUpdateHeader, onUpdateLine }: { document: SalesDocument; hasImage: boolean; sections: SalesEstimateSections; itemPresetGroups: SalesItemPresetGroups; onUpdateDetails: SalesPreviewProps['onUpdateDetails']; onUpdateHeader: SalesPreviewProps['onUpdateHeader']; onUpdateLine: SalesPreviewProps['onUpdateSheetLine'] }) {
  const customer = document.details.customerOverride ?? pickCustomerOverride(document.customerDetails)
  const vehicle = document.details.vehicleOverride ?? document.vehicleDetails ?? emptyVehicleDetails()
  const tradeInLine = sections.tradeIns[0]

  function updateCustomer(field: keyof NonNullable<SalesDocumentDetails['customerOverride']>, value: string) {
    onUpdateDetails({ customerOverride: { ...customer, [field]: value } })
  }

  function updateVehicle(field: keyof NonNullable<SalesDocumentDetails['vehicleOverride']>, value: string | boolean) {
    onUpdateDetails({ vehicleOverride: { ...vehicle, [field]: value } })
  }

  function updateTradeIn(field: keyof SalesDocumentDetails['tradeIn'], value: string) {
    onUpdateDetails({ tradeIn: { ...document.details.tradeIn, [field]: value } })
  }

  function updateRequiredDocument(field: keyof SalesDocumentDetails['requiredDocuments'], checked: boolean) {
    onUpdateDetails({ requiredDocuments: { ...document.details.requiredDocuments, [field]: checked, ...(field === 'selfDeclaration' ? { warrantyCertificate: checked } : {}) } })
  }

  function updateCredit(field: 'paymentCount' | 'bonusPayment' | 'fee' | 'bonusMonths', value: string) {
    const credit = document.details.credit
    const nextCredit = {
      ...credit,
      [field]: field === 'bonusPayment' || field === 'fee' ? Number(value || 0) : value,
    }
    nextCredit.enabled = Boolean(
      nextCredit.paymentCount
      || nextCredit.bonusPayment
      || nextCredit.fee
      || nextCredit.monthlyPayment
      || nextCredit.initialPayment
      || nextCredit.bonusMonths,
    )
    onUpdateDetails({ credit: nextCredit })
  }

  return <div className="sales-estimate-sheet-editor" aria-label="見積書の明細を直接編集">
    <SalesSheetCustomerEditor document={document} hasImage={hasImage} customer={customer} onUpdateCustomer={updateCustomer} onUpdateDetails={onUpdateDetails} />
    <SalesSheetVehicleEditor hasImage={hasImage} vehicle={vehicle} onUpdate={updateVehicle} />
    <SalesSheetTradeInEditor hasImage={hasImage} tradeIn={document.details.tradeIn} onUpdate={updateTradeIn} />
    <SalesSheetRequiredDocumentsEditor requiredDocuments={document.details.requiredDocuments} onUpdate={updateRequiredDocument} />
    <SalesSheetCreditEditor credit={document.details.credit} onUpdate={updateCredit} />
    <SheetTextControl multiline ariaLabel="備考" value={document.note} x={713} y={salesEstimateSheetLayout.noteY + 37} width={318} height={27} onChange={(value) => onUpdateHeader('note', value)} />
    {salesEstimateSheetLinePositions.map((position) => {
      const line = sections[position.bucket][position.index]
      const candidates = Array.from(new Set(itemPresetGroups[position.presetGroup].filter(Boolean)))
      return <SheetLineControl
        key={`${position.bucket}-${position.index}`}
        position={position}
        label={line?.label ?? ''}
        amount={line?.amount ?? 0}
        exists={Boolean(line)}
        candidates={candidates}
        onChange={(patch) => onUpdateLine(position.bucket, position.index, patch)}
      />
    })}
    <div className="sales-estimate-sheet-line-control is-amount-only" style={{ left: `${221 / 10.55}%`, top: `${salesEstimateSheetLayout.vehicle.paymentY / 14.91}%`, width: `${126 / 10.55}%`, height: `${31 / 14.91}%` }}>
      <SheetAmountInput value={tradeInLine?.amount ?? 0} exists={Boolean(tradeInLine)} onCommit={(amount) => onUpdateLine('tradeIns', 0, { amount })} />
    </div>
    <div className="sales-estimate-sheet-line-control is-amount-only" style={{ left: `${221 / 10.55}%`, top: `${(salesEstimateSheetLayout.vehicle.paymentY + 31) / 14.91}%`, width: `${126 / 10.55}%`, height: `${31 / 14.91}%` }}>
      <SheetAmountInput value={document.details.downPayment} exists={document.details.downPayment !== 0} onCommit={(downPayment) => onUpdateDetails({ downPayment })} />
    </div>
  </div>
}

function SalesSheetCustomerEditor({ document, hasImage, customer, onUpdateCustomer, onUpdateDetails }: { document: SalesDocument; hasImage: boolean; customer: NonNullable<SalesDocumentDetails['customerOverride']>; onUpdateCustomer: (field: keyof NonNullable<SalesDocumentDetails['customerOverride']>, value: string) => void; onUpdateDetails: SalesPreviewProps['onUpdateDetails'] }) {
  const customerLayout = salesEstimateSheetLayout.customer
  const left = hasImage
    ? { name: [84, customerLayout.y + 16, 230, 35], postalCode: [38, customerLayout.y + 61, 312, 27], address: [38, customerLayout.y + 93, 312, 27], phone: [38, customerLayout.y + 129, 312, 28] }
    : { name: [customerLayout.x + 116, customerLayout.y + 14, 235, 35], postalCode: [customerLayout.x + 116, customerLayout.y + 86, 235, 31], address: [customerLayout.x + 116, customerLayout.y + 114, 235, 31] }
  return <>
    <SheetTextControl variant="customer-name" ariaLabel="お客様名" value={customer.name} x={left.name[0]} y={left.name[1]} width={left.name[2]} height={left.name[3]} onChange={(value) => onUpdateCustomer('name', value)} />
    <SalesSheetCustomerHonorific hasImage={hasImage} value={document.details.customerHonorific || '様'} y={left.name[1]} height={left.name[3]} />
    <SheetTextControl variant="customer-value" displayPrefix="〒" ariaLabel="郵便番号" value={customer.postalCode} x={left.postalCode[0]} y={left.postalCode[1]} width={left.postalCode[2]} height={left.postalCode[3]} onChange={(value) => onUpdateCustomer('postalCode', value)} />
    <SheetTextControl variant="customer-value" ariaLabel="住所" value={customer.address} x={left.address[0]} y={left.address[1]} width={left.address[2]} height={left.address[3]} onChange={(value) => onUpdateCustomer('address', value)} />
    {hasImage && left.phone ? <SheetTextControl variant="customer-value" displayPrefix="TEL：" ariaLabel="電話番号" value={customer.phone} x={left.phone[0]} y={left.phone[1]} width={left.phone[2]} height={left.phone[3]} onChange={(value) => onUpdateCustomer('phone', value)} /> : null}
    {!hasImage ? <>
      <SheetTextControl grid ariaLabel="生年月日" value={document.details.customerBirthDate} x={478} y={customerLayout.y + 1} width={207} height={41} onChange={(customerBirthDate) => onUpdateDetails({ customerBirthDate })} />
      <SheetTextControl grid ariaLabel="お客様電話番号" value={customer.phone} x={478} y={customerLayout.y + 42} width={207} height={41} onChange={(value) => onUpdateCustomer('phone', value)} />
      <SheetTextControl grid ariaLabel="勤務先等" value={document.details.customerEmployer} x={478} y={customerLayout.y + 83} width={207} height={41} onChange={(customerEmployer) => onUpdateDetails({ customerEmployer })} />
      <SheetTextControl grid ariaLabel="連絡先電話番号" value={document.details.customerContactPhone} x={478} y={customerLayout.y + 124} width={207} height={43} onChange={(customerContactPhone) => onUpdateDetails({ customerContactPhone })} />
    </> : null}
  </>
}

function SalesSheetCustomerHonorific({ hasImage, value, y, height }: { hasImage: boolean; value: string; y: number; height: number }) {
  const customerLayout = salesEstimateSheetLayout.customer
  const rightEdge = hasImage ? customerLayout.x + customerLayout.imageWidth - 18 : customerLayout.x + 353 - 16
  return <span className="sales-estimate-sheet-customer-honorific" style={sheetPositionStyle(rightEdge - 60, y, 60, height)}>{value}</span>
}

function SalesSheetVehicleEditor({ hasImage, vehicle, onUpdate }: { hasImage: boolean; vehicle: NonNullable<SalesDocumentDetails['vehicleOverride']>; onUpdate: (field: keyof NonNullable<SalesDocumentDetails['vehicleOverride']>, value: string | boolean) => void }) {
  const y = hasImage ? salesEstimateSheetLayout.imageVehicleY + 39 : salesEstimateSheetLayout.expandedVehicleY + 39
  const fields: Array<{ field: keyof typeof vehicle; x: number; y: number; width: number; height: number }> = [
    { field: 'maker', x: 116, y, width: 100, height: 37 },
    { field: 'name', x: 311, y, width: 100, height: 37 },
    { field: 'year', x: 469, y, width: 82, height: 37 },
    { field: 'displacement', x: 618, y, width: 67, height: 37 },
    { field: 'transmission', x: 116, y: y + 38, width: 100, height: 37 },
    { field: 'color', x: 311, y: y + 38, width: 274, height: 37 },
    { field: 'modelType', x: 116, y: y + 75, width: 277, height: 37 },
    { field: 'vin', x: 483, y: y + 75, width: 202, height: 37 },
    { field: 'plate', x: 116, y: y + 113, width: 277, height: 37 },
    { field: 'mileage', x: 483, y: y + 113, width: 202, height: 37 },
    { field: 'inspectionDate', x: 116, y: y + 150, width: 277, height: 37 },
  ]
  return <>
    {fields.map(({ field, ...position }) => <SheetTextControl grid key={field} ariaLabel={`車両${field}`} value={String(vehicle[field] ?? '')} {...position} onChange={(value) => onUpdate(field, value)} />)}
    <SheetRecordControl value={vehicle.inspectionRecordAvailable} x={483} y={y + 150} width={202} height={37} onChange={(value) => onUpdate('inspectionRecordAvailable', value)} />
  </>
}

function SalesSheetTradeInEditor({ hasImage, tradeIn, onUpdate }: { hasImage: boolean; tradeIn: SalesDocumentDetails['tradeIn']; onUpdate: (field: keyof SalesDocumentDetails['tradeIn'], value: string) => void }) {
  const y = (hasImage ? salesEstimateSheetLayout.imageTradeInY : salesEstimateSheetLayout.expandedTradeInY) + 68
  const fields: Array<{ field: keyof typeof tradeIn; x: number; width: number }> = [
    { field: 'name', x: 24, width: 180 },
    { field: 'modelYear', x: 204, width: 105 },
    { field: 'inspectionDate', x: 309, width: 118 },
    { field: 'mileage', x: 427, width: 137 },
    { field: 'color', x: 564, width: 121 },
  ]
  return <>{fields.map(({ field, x, width }) => <SheetTextControl grid key={field} ariaLabel={`下取車${field}`} value={tradeIn[field]} x={x} y={y} width={width} height={32} centered onChange={(value) => onUpdate(field, value)} />)}</>
}

function SalesSheetRequiredDocumentsEditor({ requiredDocuments, onUpdate }: { requiredDocuments: SalesDocumentDetails['requiredDocuments']; onUpdate: (field: keyof SalesDocumentDetails['requiredDocuments'], checked: boolean) => void }) {
  const fields: Array<keyof SalesDocumentDetails['requiredDocuments']> = ['sealCertificate', 'selfDeclaration', 'residentCard', 'powerOfAttorney', 'lightVehicleCertificate', 'transferCertificate', 'taxPaymentCertificate', 'guarantorSealCertificate']
  return <>{fields.map((field, index) => {
    const col = index % 2
    const row = Math.floor(index / 2)
    return <label key={field} className="sales-estimate-sheet-checkbox" style={sheetPositionStyle(724 + col * 156, salesEstimateSheetLayout.requiredY + 50 + row * 26, 16, 16)}><input aria-label={requiredDocumentFields.find((item) => item.key === field)?.label ?? field} type="checkbox" checked={Boolean(requiredDocuments[field])} onChange={(event) => onUpdate(field, event.target.checked)} /></label>
  })}</>
}

function SalesSheetCreditEditor({ credit, onUpdate }: { credit: SalesDocumentDetails['credit']; onUpdate: (field: 'paymentCount' | 'bonusPayment' | 'fee' | 'bonusMonths', value: string) => void }) {
  return <>
    <SheetCreditInput ariaLabel="クレジット支払回数" value={credit.paymentCount} x={23} width={119} onCommit={(value) => onUpdate('paymentCount', value)} />
    <SheetCreditInput currency ariaLabel="クレジットボーナス払" value={credit.bonusPayment ? String(credit.bonusPayment) : ''} x={142} width={119} onCommit={(value) => onUpdate('bonusPayment', value)} />
    <SheetCreditInput decimal ariaLabel="クレジット金利" value={credit.fee ? String(credit.fee) : ''} x={261} width={119} onCommit={(value) => onUpdate('fee', value)} />
    <SheetCreditInput ariaLabel="クレジット支払開始月" value={credit.bonusMonths} x={380} width={118} onCommit={(value) => onUpdate('bonusMonths', value)} />
  </>
}

function SheetCreditInput({ ariaLabel, value, x, width, currency = false, decimal = false, onCommit }: { ariaLabel: string; value: string; x: number; width: number; currency?: boolean; decimal?: boolean; onCommit: (value: string) => void }) {
  const [draft, setDraft] = useState(value)
  const [focused, setFocused] = useState(false)
  useEffect(() => setDraft(value), [value])

  function update(nextValue: string) {
    const pattern = decimal ? /^\d*(?:\.\d{0,2})?$/ : /^[\d./-]*$/
    if (pattern.test(nextValue)) setDraft(nextValue)
  }

  function finish() {
    setFocused(false)
    if (draft !== value) onCommit(draft)
  }

  return <input
    className="sales-estimate-sheet-field-control has-grid is-centered"
    aria-label={ariaLabel}
    inputMode={decimal ? 'decimal' : 'numeric'}
    value={currency && !focused && draft ? formatSheetYen(Number(draft)) : draft}
    style={sheetPositionStyle(x, salesEstimateSheetLayout.creditY + 76, width, 34)}
    onFocus={() => setFocused(true)}
    onChange={(event) => update(event.target.value)}
    onBlur={finish}
  />
}

function SheetTextControl({ ariaLabel, value, x, y, width, height, centered = false, multiline = false, grid = false, variant, displayPrefix = '', onChange }: { ariaLabel: string; value: string; x: number; y: number; width: number; height: number; centered?: boolean; multiline?: boolean; grid?: boolean; variant?: 'customer-name' | 'customer-value'; displayPrefix?: string; onChange: (value: string) => void }) {
  const className = `sales-estimate-sheet-field-control${centered ? ' is-centered' : ''}${multiline ? ' is-multiline' : ''}${grid ? ' has-grid' : ''}${variant ? ` is-${variant}` : ''}`
  const displayValue = value ? `${displayPrefix}${value}` : value
  function handleChange(nextValue: string) {
    const withoutPrefix = displayPrefix && nextValue.startsWith(displayPrefix) ? nextValue.slice(displayPrefix.length) : nextValue
    onChange(withoutPrefix)
  }
  const props = { className, 'aria-label': ariaLabel, spellCheck: false, value: displayValue, style: sheetPositionStyle(x, y, width, height), onChange: (event: ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) => handleChange(event.target.value) }
  return multiline ? <textarea {...props} /> : <input {...props} />
}

function SheetRecordControl({ value, x, y, width, height, onChange }: { value: boolean; x: number; y: number; width: number; height: number; onChange: (value: boolean) => void }) {
  return <select
    aria-label="記録簿"
    className="sales-estimate-sheet-field-control has-grid is-select"
    value={value ? 'あり' : 'なし'}
    style={sheetPositionStyle(x, y, width, height)}
    onChange={(event) => onChange(event.target.value === 'あり')}
  >
    <option value="あり">あり</option>
    <option value="なし">なし</option>
  </select>
}

function sheetPositionStyle(x: number, y: number, width: number, height: number): CSSProperties {
  return { left: `${x / 10.55}%`, top: `${y / 14.91}%`, width: `${width / 10.55}%`, height: `${height / 14.91}%` }
}

function SheetLineControl({ position, label, amount, exists, candidates, onChange }: { position: SheetLinePosition; label: string; amount: number; exists: boolean; candidates: string[]; onChange: (patch: { label?: string; amount?: number }) => void }) {
  const style = {
    left: `${position.x / 10.55}%`,
    top: `${position.y / 14.91}%`,
    width: `${position.width / 10.55}%`,
    height: `${position.height / 14.91}%`,
    '--sheet-label-width': `${position.labelWidth / position.width * 100}%`,
  } as CSSProperties
  return <div className={`sales-estimate-sheet-line-control${position.bucket === 'accessories' ? ' is-accessory-line' : ''}`} style={style}>
    {position.fixedLabel
      ? <span className="sales-sheet-fixed-label">{position.fixedLabel}</span>
      : <SheetNameCombobox value={label} candidates={candidates} menuUp={position.menuUp} onCommit={(value) => onChange({ label: value })} />}
    <SheetAmountInput value={amount} exists={exists} onCommit={(value) => onChange({ amount: value })} />
  </div>
}

function SheetNameCombobox({ value, candidates, menuUp = false, onCommit }: { value: string; candidates: string[]; menuUp?: boolean; onCommit: (value: string) => void }) {
  const [draft, setDraft] = useState(value)
  const [open, setOpen] = useState(false)
  const rootRef = useRef<HTMLDivElement>(null)
  useEffect(() => setDraft(value), [value])
  useEffect(() => {
    if (!open) return
    const closeOnOutsidePointer = (event: PointerEvent) => {
      if (!rootRef.current?.contains(event.target as Node)) setOpen(false)
    }
    document.addEventListener('pointerdown', closeOnOutsidePointer)
    return () => document.removeEventListener('pointerdown', closeOnOutsidePointer)
  }, [open])

  function commit() {
    setOpen(false)
    if (draft !== value) onCommit(draft.trim())
  }

  return <div ref={rootRef} className="sales-sheet-name-combobox">
    <input
      aria-label="費用名・品名"
      role="combobox"
      aria-expanded={open}
      value={draft}
      onChange={(event) => setDraft(event.target.value)}
      onFocus={() => setOpen(false)}
      onBlur={commit}
    />
    <button type="button" aria-label="明細候補を表示" onMouseDown={(event) => event.preventDefault()} onClick={() => setOpen((current) => !current)}><ChevronDown size={13} /></button>
    {open ? <div className={`sales-sheet-candidate-menu${menuUp ? ' is-up' : ''}`} role="listbox">
      {candidates.map((candidate) => <button key={candidate} type="button" role="option" aria-selected={candidate === draft} onMouseDown={(event) => event.preventDefault()} onClick={() => { setDraft(candidate); setOpen(false); onCommit(candidate) }}>{candidate}</button>)}
    </div> : null}
  </div>
}

function SheetAmountInput({ value, exists, onCommit }: { value: number; exists: boolean; onCommit: (value: number) => void }) {
  const [draft, setDraft] = useState(exists ? String(value) : '')
  const [focused, setFocused] = useState(false)
  useEffect(() => setDraft(exists ? String(value) : ''), [exists, value])

  function update(nextValue: string) {
    if (!/^-?\d*$/.test(nextValue)) return
    setDraft(nextValue)
    if (nextValue && nextValue !== '-') onCommit(Number(nextValue))
  }

  function finish() {
    if (!draft || draft === '-') {
      setDraft('')
      if (exists) onCommit(0)
    }
    setFocused(false)
  }

  return <input
    className="sales-sheet-amount-input"
    aria-label="金額"
    inputMode="numeric"
    value={focused ? draft : draft ? formatSheetYen(Number(draft)) : ''}
    onFocus={() => {
      setDraft(exists ? String(value) : '')
      setFocused(true)
    }}
    onChange={(event) => update(event.target.value)}
    onBlur={finish}
  />
}

function formatSheetYen(value: number) {
  const formatted = sheetYenFormatter.format(Math.abs(Math.round(value)))
  return value < 0 ? `-¥${formatted}` : `¥${formatted}`
}

function SalesEstimatePreviewLayout({ document, totals, settings, itemPresets, customers, onUpdateHeader, onUpdateDetails, onUpdateItem, onAddItem, onRemoveItem, onPdfPreview }: SalesPreviewProps) {
  const selectedCustomer = customers.find((customer) => customer.id === document.customerId)
  const selectedVehicle = selectedCustomer?.vehicles.find((vehicle) => vehicle.id === document.vehicleId)
  const customer = document.customerDetails ?? mapCustomerDetails(selectedCustomer)
  const vehicle = document.vehicleDetails ?? (selectedVehicle ? mapVehicleDetails(selectedVehicle) : null)
  const details = document.details
  const sections = buildSalesEstimateSections(document)
  const imageAttachments = selectedVehicle?.attachments.filter((attachment) => attachment.type === 'image') ?? []
  const selectedAttachment = imageAttachments.find((attachment) => attachment.id === details.selectedImageAttachmentId)
  const imageState = useVehicleAttachmentUrl(document.vehicleId, selectedAttachment?.id ?? '')
  const hasImage = Boolean(imageState.url && selectedAttachment)
  const shopLines = [settings.shop.postalCode ? `〒${settings.shop.postalCode}` : '', settings.shop.address, settings.shop.phone ? `TEL ${settings.shop.phone}` : '', settings.shop.representative ? `担当 ${settings.shop.representative}` : '', settings.shop.registrationNumber ? `登録番号 ${settings.shop.registrationNumber}` : ''].filter(Boolean)
  const paymentNote = settings.document.paymentNote || '店頭または指定口座へお支払いください。'
  const bankAccount = [settings.shop.bankName, settings.shop.bankAccount].filter(Boolean).join(' / ') || '未設定'

  return <div className="sales-preview-area"><div className="sales-preview-toolbar"><div><strong>見積書プレビュー</strong><span>PDFと同じ帳票構成で確認できます。表示画像と明細はこの画面から変更できます。</span></div><button className="button button-secondary" type="button" onClick={onPdfPreview}><Eye size={16} />PDFで確認</button></div><div className="sales-estimate-image-control"><div><strong><ImageIcon size={16} />帳票に表示する車両画像</strong><span>{selectedVehicle ? `${selectedVehicle.maker} ${selectedVehicle.model}の添付画像から選択できます。` : '対象車両を選択すると添付画像を選択できます。'}</span></div><div className="sales-estimate-image-select"><select aria-label="帳票に表示する車両画像" value={details.selectedImageAttachmentId} disabled={!imageAttachments.length} onChange={(event) => onUpdateDetails({ selectedImageAttachmentId: event.target.value })}><option value="">画像なし（顧客情報を拡張）</option>{imageAttachments.map((attachment) => <option key={attachment.id} value={attachment.id}>{attachment.name}</option>)}</select>{imageState.loading && <small><RefreshCw size={13} className="is-spinning" />画像を読み込んでいます…</small>}{imageState.error && <small className="is-error">画像を表示できないため、顧客情報表示に切り替えています。</small>}{!imageAttachments.length && <small>画像ファイルが登録されていません。</small>}</div></div><article className={`sales-document-paper sales-estimate-paper${hasImage ? ' has-selected-image' : ' has-expanded-customer'}`}><header className="sales-estimate-paper-header"><div className="sales-estimate-title-block"><select className="sales-estimate-title" aria-label="書類種別" value={document.type} onChange={(event) => onUpdateHeader('type', event.target.value)}><option>見積書</option><option>請求書</option></select><span>{details.salesCategory || '販売書類'}</span></div><div className="sales-estimate-meta-table"><div><span>日付</span><input type="date" aria-label="発行日" value={document.issuedAt.replaceAll('/', '-')} onChange={(event) => onUpdateHeader('issuedAt', event.target.value.replaceAll('-', '/'))} /></div><div><span>販売区分</span><strong>{details.salesCategory || '未設定'}</strong></div><div><span>担当</span><strong>{details.staffName || '未設定'}</strong></div><div><span>見積番号</span><strong>{document.number}</strong></div><div><span>ページ</span><strong>1 / 1</strong></div></div></header><section className="sales-estimate-customer-grid"><div className="sales-estimate-customer-box"><div className="sales-estimate-cell-label">お名前</div><div className="sales-estimate-cell-value"><strong>{customer.name || '未設定'} {details.customerHonorific || '様'}</strong><small>{customer.kana || 'ふりがな未登録'}</small></div><div className="sales-estimate-cell-label">ご住所</div><div className="sales-estimate-cell-value"><span>{customer.postalCode ? `〒${customer.postalCode}` : ''}</span><span>{customer.address || '住所未登録'}</span></div><div className="sales-estimate-cell-label">電話番号</div><div className="sales-estimate-cell-value"><span>{customer.phone || '未登録'}</span></div></div>{hasImage ? <div className="sales-estimate-photo-box"><img src={imageState.url} alt={`${vehicle?.name || '対象車両'}の選択画像`} /><small>{selectedAttachment?.name}</small></div> : <div className="sales-estimate-contact-box"><div className="sales-estimate-cell-label">生年月日</div><div>{details.customerBirthDate || '未設定'}</div><div className="sales-estimate-cell-label">電話番号</div><div>{customer.phone || '未登録'}</div><div className="sales-estimate-cell-label">勤務先等</div><div>{details.customerEmployer || '未設定'}</div><div className="sales-estimate-cell-label">連絡先TEL</div><div>{details.customerContactPhone || '未設定'}</div></div>}</section><EstimateVehicleTable vehicle={vehicle} /><EstimateTradeInTable details={details} /><section className="sales-estimate-summary-top"><div className="sales-estimate-amount-card"><span>お見積金額（税込）</span><strong>{formatYen(totals.total)}</strong></div><div className="sales-estimate-tax-card"><div><span>課税対象額（{formatPercent(document.taxRate)}）</span><strong>{formatYen(totals.taxableSubtotal)}</strong></div><div><span>消費税（{formatPercent(document.taxRate)}）</span><strong>{formatYen(totals.tax)}</strong></div><div><span>非課税対象額</span><strong>{formatYen(totals.nonTaxableSubtotal + totals.outOfScopeSubtotal)}</strong></div></div></section><div className="sales-estimate-status-line"><span>支払期限：{document.dueDate || '未設定'}</span><span>状態：{document.status}</span></div><div className="sales-estimate-section-title"><h3>見積金額内訳</h3><span /></div><div className="sales-estimate-breakdown-grid"><EstimateVehicleBreakdown totals={totals} taxRate={document.taxRate} /><EstimateFeeBreakdown sections={sections} totals={totals} /><EstimateAccessoryBreakdown sections={sections} totals={totals} /></div><details className="sales-estimate-edit-details"><summary><FileText size={15} />金額明細を編集</summary><div className="sales-estimate-items-table"><div className="sales-estimate-items-head"><span>No.</span><span>作業内容／部品名等</span><span>数量</span><span>単位</span><span>部品単価</span><span>部品金額</span><span>技術料・他</span><span>摘要・課税</span></div>{document.items.map((item, index) => <div className="sales-estimate-item-row" key={item.id}><span>{index + 1}</span><input list="sales-preview-item-presets" aria-label="プレビューの明細内容" value={item.description} onChange={(event) => onUpdateItem(item.id, 'description', event.target.value)} placeholder="明細内容" /><input aria-label="プレビューの数量" type="number" min="0" value={item.quantity} onChange={(event) => onUpdateItem(item.id, 'quantity', event.target.value)} /><input aria-label="プレビューの単位" value={item.unit} onChange={(event) => onUpdateItem(item.id, 'unit', event.target.value)} /><input aria-label="プレビューの単価" type="number" value={item.unitPrice} onChange={(event) => onUpdateItem(item.id, 'unitPrice', event.target.value)} /><strong>{formatYen(calculateSalesLineAmount(item))}</strong><input aria-label="プレビューの技術料・他" type="number" value={item.otherAmount} onChange={(event) => onUpdateItem(item.id, 'otherAmount', event.target.value)} /><div className="sales-estimate-summary-cell"><input aria-label="プレビューの摘要" value={item.summary} onChange={(event) => onUpdateItem(item.id, 'summary', event.target.value)} /><select aria-label="プレビューの課税区分" value={item.taxCategory} onChange={(event) => onUpdateItem(item.id, 'taxCategory', event.target.value)}>{salesTaxCategories.map((category) => <option key={category}>{category}</option>)}</select></div><button className="sales-estimate-item-remove" type="button" aria-label="明細を削除" onClick={() => onRemoveLineItemGuard(item.id, document.items.length, onRemoveItem)}><Trash2 size={14} /></button></div>)}</div><datalist id="sales-preview-item-presets">{itemPresets.map((preset) => <option key={preset} value={preset} />)}</datalist><div className="sales-estimate-edit-actions"><button className="button button-secondary" type="button" onClick={onAddItem}><Plus size={15} />明細を追加</button><span>残金・所要資金は見積総額、下取、頭金から自動計算します。</span></div></details><section className="sales-estimate-bottom-grid"><div><div className="sales-estimate-credit"><h4>クレジットお支払いプラン</h4>{details.credit.enabled ? <div><span>{details.credit.paymentCount || '回数未設定'}</span><span>手数料 {formatYen(details.credit.fee)}</span><span>月々 {formatYen(details.credit.monthlyPayment)}</span><span>初回 {formatYen(details.credit.initialPayment)}</span><span>ボーナス {details.credit.bonusMonths || '月未設定'} / {formatYen(details.credit.bonusPayment)}</span></div> : <p>利用なし</p>}</div><div className="sales-estimate-required"><h4>必要書類</h4><p>{requiredDocumentLabels(details).join(' ／ ') || '未確認'}</p></div></div><div className="sales-estimate-company"><strong>{settings.shop.name || '店舗名未設定'}</strong>{shopLines.slice(0, 4).map((line) => <span key={line}>{line}</span>)}<div className="sales-estimate-company-payment"><span>お支払いについて</span><p>{paymentNote}</p><span>振込先</span><p>{bankAccount}</p></div></div></section><footer className="sales-paper-footer"><span>{document.note || settings.document.footerNote || '見積条件は担当者へご確認ください。'}</span><span>ページ 1 / 1</span></footer></article></div>
}

function EstimateVehicleBreakdown({ totals, taxRate }: { totals: SalesTotals; taxRate: number }) {
  return <section className="sales-estimate-breakdown-card"><h4>車両販売価格内訳</h4><EstimateBreakdownRow label="車両本体価格" amount={totals.vehicleBasePrice} /><EstimateBreakdownRow label="値引等" amount={totals.discount} tone="discount" /><EstimateBreakdownRow label="本体課税対象額" amount={totals.vehicleTaxableAmount} /><EstimateBreakdownRow label="付属品／特別仕様" amount={totals.accessoryTotal} /><EstimateBreakdownRow label="車両販売合計" amount={totals.vehicleSalesTotal} emphasis /><EstimateBreakdownRow label="諸費用合計" amount={totals.feesTotal} emphasis /><div className="sales-estimate-tax-breakdown"><div className="sales-estimate-tax-breakdown-heading"><span /><span>課税対象{formatPercent(taxRate)}</span><span>非課税対象</span></div><div className="sales-estimate-tax-breakdown-row"><span>対象額合計</span><strong>{formatYen(totals.taxableSubtotal)}</strong><strong>{formatYen(totals.nonTaxableSubtotal + totals.outOfScopeSubtotal)}</strong></div><div className="sales-estimate-tax-breakdown-row"><span>消費税（{formatPercent(taxRate)}）</span><strong>{formatYen(totals.tax)}</strong><strong>−</strong></div><div className="sales-estimate-tax-breakdown-total"><span>総額</span><strong>{formatYen(totals.total)}</strong></div></div><EstimateBreakdownRow label="下取車価格" amount={totals.tradeInPrice} /><EstimateBreakdownRow label="頭金／現金／他" amount={totals.downPayment} /><EstimateBreakdownRow label="残金／所要資金" amount={totals.remainingPayment} emphasis /></section>
}

function EstimateFeeBreakdown({ sections, totals }: { sections: SalesEstimateSections; totals: SalesTotals }) {
  return <section className="sales-estimate-breakdown-card sales-estimate-fee-breakdown"><h4>諸費用内訳</h4><EstimateFeeGroup title="税金/保険料（非課税）（非課税）" lines={sections.legalNonTaxable} total={totals.legalNonTaxable} /><EstimateFeeGroup title="手続代行費用（課税）" lines={sections.taxableFees} total={totals.taxableFeeTotal} /><EstimateFeeGroup title="実費・預託金（非課税）" lines={sections.nonTaxableFees} total={totals.nonTaxableFeeTotal} /><div className="sales-estimate-fee-total"><span>諸費用合計</span><strong>{formatYen(totals.feesTotal)}</strong></div></section>
}

function EstimateAccessoryBreakdown({ sections, totals }: { sections: SalesEstimateSections; totals: SalesTotals }) {
  return <section className="sales-estimate-breakdown-card sales-estimate-accessory-breakdown"><h4>付属品・特別仕様明細</h4><div className="sales-estimate-breakdown-heading"><span>品名</span><span>金額</span></div>{sections.accessories.length ? sections.accessories.map((line) => <EstimateBreakdownRow key={line.id} label={line.label} amount={line.amount} />) : <div className="sales-estimate-breakdown-empty">登録なし</div>}<div className="sales-estimate-accessory-total"><span>付属品・特別仕様合計</span><strong>{formatYen(totals.accessoryTotal)}</strong></div></section>
}

function EstimateFeeGroup({ title, lines, total }: { title: string; lines: Array<{ id: string; label: string; amount: number }>; total: number }) {
  return <div className="sales-estimate-fee-group"><h5>{title}</h5>{lines.length ? lines.map((line) => <EstimateBreakdownRow key={line.id} label={line.label} amount={line.amount} />) : <div className="sales-estimate-breakdown-empty">なし</div>}<div className="sales-estimate-fee-subtotal"><span>小計</span><strong>{formatYen(total)}</strong></div></div>
}

function EstimateBreakdownRow({ label, amount, tone, emphasis }: { label: string; amount: number; tone?: 'discount'; emphasis?: boolean }) {
  return <div className={`sales-estimate-breakdown-row${emphasis ? ' is-emphasis' : ''}${tone ? ` is-${tone}` : ''}`}><span>{label}</span><strong>{formatYen(amount)}</strong></div>
}

function useVehicleAttachmentUrl(vehicleId: string | null, attachmentId: string) {
  const [state, setState] = useState<{ url: string; loading: boolean; error: string }>({ url: '', loading: false, error: '' })

  useEffect(() => {
    let cancelled = false
    let objectUrl = ''
    if (!vehicleId || !attachmentId) {
      setState({ url: '', loading: false, error: '' })
      return () => { cancelled = true }
    }
    setState({ url: '', loading: true, error: '' })
    fetchVehicleFile(vehicleId, attachmentId).then((blob) => {
      if (cancelled) return
      objectUrl = URL.createObjectURL(blob)
      setState({ url: objectUrl, loading: false, error: '' })
    }).catch((error: unknown) => {
      if (!cancelled) setState({ url: '', loading: false, error: error instanceof Error ? error.message : '画像を読み込めませんでした。' })
    })
    return () => {
      cancelled = true
      if (objectUrl) URL.revokeObjectURL(objectUrl)
    }
  }, [attachmentId, vehicleId])

  return state
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

function calculateTotals(document: SalesDocument, rounding: AppSettings['tax']['rounding']): SalesTotals {
  return calculateSalesEstimateTotals(document, rounding)
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

function pickCustomerOverride(customer: SalesDocument['customerDetails']): NonNullable<SalesDocumentDetails['customerOverride']> {
  return { name: customer.name, kana: customer.kana, phone: customer.phone, postalCode: customer.postalCode, address: customer.address }
}

function emptyCustomerDetails(): SalesDocument['customerDetails'] {
  return { name: '', kana: '', phone: '', postalCode: '', address: '', birthDate: '', employer: '', contactPhone: '' }
}

function emptyVehicleDetails(): NonNullable<SalesDocumentDetails['vehicleOverride']> {
  return { maker: '', name: '', modelType: '', plate: '', vin: '', year: '', inspectionDate: '', mileage: '', color: '', displacement: '', transmission: '', inspectionRecordAvailable: false }
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

  return <div className="modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose() }}><section className="modal" role="dialog" aria-modal="true" aria-labelledby="sales-modal-title"><div className="modal-header"><h2 id="sales-modal-title">販売書類を作成</h2><button className="modal-close" type="button" aria-label="閉じる" onClick={onClose}><X size={19} /></button></div><form className="modal-form" onSubmit={onSubmit}><p className="modal-description"><FileText size={16} />顧客・車両を選択して、下書きの販売書類を作成します。未登録の場合はこの画面から追加できます。</p><div className="form-grid"><label className="form-field"><span>書類種別<em>必須</em></span><select required value={form.type} onChange={(event) => onChange({ ...form, type: event.target.value as SalesDocumentType })}><option>見積書</option><option>請求書</option></select></label><div className="form-field sales-create-related-field"><span>顧客<em>必須</em></span><div className="sales-create-select-row"><select required aria-label="顧客" value={form.customerId} onChange={(event) => selectCustomer(event.target.value)}><option value="" disabled>顧客を選択してください</option>{customers.map((customer) => <option key={customer.id} value={customer.id}>{customer.name}（{customer.phone || '電話番号未登録'}）</option>)}</select><button className="button button-secondary sales-create-inline-action" type="button" onClick={openNewCustomerForm}><Plus size={14} />新しい顧客</button></div></div><div className="form-field sales-create-related-field"><span>対象車両</span><div className="sales-create-select-row"><select aria-label="対象車両" disabled={!selectedCustomer} value={form.vehicleId} onChange={(event) => onChange({ ...form, vehicleId: event.target.value })}><option value="">車両を指定しない</option>{vehicles.map((vehicle) => <option key={vehicle.id} value={vehicle.id}>{vehicle.maker} {vehicle.model}{vehicle.plate ? `（${vehicle.plate}）` : ''}</option>)}</select><button className="button button-secondary sales-create-inline-action" type="button" disabled={!selectedCustomer} onClick={openNewVehicleForm}><Plus size={14} />新しい車両</button></div></div><label className="form-field"><span>支払期限</span><input type="date" value={form.dueDate} onChange={(event) => onChange({ ...form, dueDate: event.target.value })} /></label></div>{newCustomerOpen && <div className="sales-create-inline-panel"><div><h3>新しい顧客を登録</h3><p>顧客名を登録すると、この販売書類の顧客として選択されます。</p></div><div className="form-grid"><label className="form-field"><span>顧客名<em>必須</em></span><input autoFocus value={newCustomerForm.name} onChange={(event) => setNewCustomerForm({ ...newCustomerForm, name: event.target.value })} placeholder="例：山田 太郎" /></label><label className="form-field"><span>ふりがな</span><input value={newCustomerForm.kana} onChange={(event) => setNewCustomerForm({ ...newCustomerForm, kana: event.target.value })} placeholder="例：やまだ たろう" /></label><label className="form-field"><span>電話番号</span><input type="tel" value={newCustomerForm.phone} onChange={(event) => setNewCustomerForm({ ...newCustomerForm, phone: event.target.value })} placeholder="例：090-1234-5678" /></label><label className="form-field"><span>メールアドレス</span><input type="email" value={newCustomerForm.email} onChange={(event) => setNewCustomerForm({ ...newCustomerForm, email: event.target.value })} placeholder="例：example@example.com" /></label><label className="form-field"><span>郵便番号</span><input value={newCustomerForm.postalCode ?? ''} onChange={(event) => setNewCustomerForm({ ...newCustomerForm, postalCode: event.target.value })} placeholder="例：100-0001" /></label><label className="form-field"><span>住所</span><input value={newCustomerForm.address} onChange={(event) => setNewCustomerForm({ ...newCustomerForm, address: event.target.value })} placeholder="例：東京都千代田区" /></label></div><div className="sales-create-inline-actions"><button className="button button-secondary" type="button" disabled={registeringCustomer} onClick={() => setNewCustomerOpen(false)}>閉じる</button><button className="button button-primary" type="button" disabled={registeringCustomer} onClick={() => void saveNewCustomer()}><Plus size={15} />{registeringCustomer ? '登録中…' : '顧客を登録'}</button></div></div>}{newVehicleOpen && <div className="sales-create-inline-panel"><div><h3>新しい車両を登録</h3><p>{selectedCustomer?.name} の車両情報を登録します。メーカーと車名は必須です。</p></div><div className="form-grid"><label className="form-field"><span>メーカー<em>必須</em></span><input autoFocus value={newVehicleForm.maker} onChange={(event) => setNewVehicleForm({ ...newVehicleForm, maker: event.target.value })} placeholder="例：トヨタ" /></label><label className="form-field"><span>車名<em>必須</em></span><input value={newVehicleForm.model} onChange={(event) => setNewVehicleForm({ ...newVehicleForm, model: event.target.value })} placeholder="例：プリウス" /></label><label className="form-field"><span>型式</span><input value={newVehicleForm.modelType} onChange={(event) => setNewVehicleForm({ ...newVehicleForm, modelType: event.target.value })} placeholder="例：6AA-ZVW60" /></label><label className="form-field"><span>登録番号</span><input value={newVehicleForm.plate} onChange={(event) => setNewVehicleForm({ ...newVehicleForm, plate: event.target.value })} placeholder="例：品川 500 あ 1234" /></label><label className="form-field"><span>車台番号</span><input value={newVehicleForm.vin} onChange={(event) => setNewVehicleForm({ ...newVehicleForm, vin: event.target.value })} placeholder="例：ZVW5000001" /></label><label className="form-field"><span>年式</span><input value={newVehicleForm.year} onChange={(event) => setNewVehicleForm({ ...newVehicleForm, year: event.target.value })} placeholder="例：2024" /></label><label className="form-field"><span>車検満了日</span><input type="date" value={newVehicleForm.inspectionDate.replaceAll('/', '-')} onChange={(event) => setNewVehicleForm({ ...newVehicleForm, inspectionDate: event.target.value.replaceAll('-', '/') })} /></label><label className="form-field"><span>走行距離</span><input inputMode="numeric" value={newVehicleForm.mileage} onChange={(event) => setNewVehicleForm({ ...newVehicleForm, mileage: event.target.value })} placeholder="例：30000" /></label><label className="form-field"><span>車体色</span><input value={newVehicleForm.color} onChange={(event) => setNewVehicleForm({ ...newVehicleForm, color: event.target.value })} placeholder="例：パールホワイト" /></label><label className="form-field"><span>排気量</span><input inputMode="numeric" value={newVehicleForm.displacement} onChange={(event) => setNewVehicleForm({ ...newVehicleForm, displacement: event.target.value })} placeholder="例：1800" /></label><label className="form-field"><span>ミッション</span><input value={newVehicleForm.transmission} onChange={(event) => setNewVehicleForm({ ...newVehicleForm, transmission: event.target.value })} placeholder="例：CVT" /></label></div><div className="sales-create-inline-actions"><button className="button button-secondary" type="button" disabled={registeringVehicle} onClick={() => setNewVehicleOpen(false)}>閉じる</button><button className="button button-primary" type="button" disabled={registeringVehicle} onClick={() => void saveNewVehicle()}><Plus size={15} />{registeringVehicle ? '登録中…' : '車両を登録'}</button></div></div>}{dialogError && <p className="sales-create-error" role="alert">{dialogError}</p>}<div className="modal-footer"><button className="button button-secondary" type="button" onClick={onClose}>キャンセル</button><button className="button button-primary" type="submit" disabled={creating || registeringCustomer || registeringVehicle || !form.customerId}><Plus size={16} />{creating ? '作成中…' : '作成する'}</button></div></form></section></div>
}

function emptyCreateForm(): SalesCreateForm {
  return { type: '見積書', customerId: '', vehicleId: '', dueDate: dateAfter(14), taxRate: 10, taxRounding: '切り捨て', initialItemDescription: '車両本体価格' }
}

function dateAfter(days: number) {
  const date = new Date()
  date.setDate(date.getDate() + days)
  return date.toISOString().slice(0, 10)
}
