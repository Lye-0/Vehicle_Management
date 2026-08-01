import { useEffect, useMemo, useState, type FormEvent, type ReactNode } from 'react'
import {
  Archive,
  CarFront,
  ChevronRight,
  ChevronDown,
  ClipboardCheck,
  Eye,
  FileDown,
  FileText,
  Plus,
  Save,
  Search,
  UserRound,
  X,
} from 'lucide-react'
import { fetchCustomers, type Customer } from '../lib/customerApi'
import { downloadMaintenanceDocumentPdf, previewMaintenanceDocumentPdf } from '../lib/pdf'
import {
  createMaintenanceDocument,
  archiveMaintenanceDocument,
  fetchMaintenanceDocuments,
  updateMaintenanceDocument,
  defaultMaintenanceDocumentDetails,
  type IntakeCategory,
  type MandatoryFees,
  type MaintenanceFeeKey,
  type MaintenanceCustomerDetails,
  type MaintenanceDocument,
  type MaintenanceDocumentDetails,
  type MaintenanceDocumentInput,
  type MaintenanceDocumentType,
  type MaintenanceItemKind,
  type MaintenanceLineItem,
  type MaintenanceVehicleDetails,
  type MaintenanceStatus,
} from '../lib/maintenanceApi'
import { buildMaintenanceStatementSvg, calculateMaintenanceStatementTotals } from '../lib/maintenanceStatement'
import { defaultSettings, fetchSettings, type AppSettings } from '../lib/settingsApi'
import { MaintenanceStatementEditor, type MaintenanceStatementItemField } from './MaintenanceStatementEditor'

type CategoryFilter = 'すべて' | IntakeCategory
type MaintenanceDocumentView = 'edit' | 'preview'
type MaintenanceCreateForm = { type: MaintenanceDocumentType; category: IntakeCategory; customerId: string; vehicleId: string; intakeDate: string; plannedReleaseDate: string; dueDate: string }

const maintenanceDocumentTypeOptions: MaintenanceDocumentType[] = ['整備見積書', '整備請求書']
const maintenanceCategoryOptions: IntakeCategory[] = ['車検', '板金', '一般整備']
const maintenanceStatusOptions: Exclude<MaintenanceStatus, 'アーカイブ済み'>[] = ['下書き', '入金待ち', '完了']
const emptyFees: MandatoryFees = { 自賠責: 0, 重量税: 0, 印紙代: 0, リサイクル料金: 0 }
const emptyCreateForm: MaintenanceCreateForm = { type: '整備見積書', category: '一般整備', customerId: '', vehicleId: '', intakeDate: todayDisplay(), plannedReleaseDate: addDaysDisplay(2), dueDate: addDaysDisplay(14) }

export function MaintenancePage({ initialDocumentId }: { initialDocumentId?: string } = {}) {
  const [documents, setDocuments] = useState<MaintenanceDocument[]>([])
  const [customers, setCustomers] = useState<Customer[]>([])
  const [settings, setSettings] = useState<AppSettings>(defaultSettings)
  const [query, setQuery] = useState('')
  const [categoryFilter, setCategoryFilter] = useState<CategoryFilter>('すべて')
  const [selectedDocumentId, setSelectedDocumentId] = useState(initialDocumentId ?? '')
  const [createDialogOpen, setCreateDialogOpen] = useState(false)
  const [createForm, setCreateForm] = useState<MaintenanceCreateForm>(emptyCreateForm)
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [savedDocumentId, setSavedDocumentId] = useState('')
  const [error, setError] = useState('')
  const [documentView, setDocumentView] = useState<MaintenanceDocumentView>('edit')

  useEffect(() => {
    let cancelled = false
    Promise.all([fetchMaintenanceDocuments(), fetchCustomers(), fetchSettings()])
      .then(([nextDocuments, nextCustomers, nextSettings]) => {
        if (cancelled) return
        setDocuments(nextDocuments)
        setCustomers(nextCustomers)
        setSettings(nextSettings)
        setSelectedDocumentId((current) => nextDocuments.some((document) => document.id === current) ? current : nextDocuments[0]?.id ?? '')
        setCreateForm(createFormForCustomers(nextCustomers, nextSettings.document.defaultDueDays))
        setError('')
      })
      .catch((reason: unknown) => { if (!cancelled) setError(reason instanceof Error ? reason.message : '整備データを読み込めませんでした。') })
      .finally(() => { if (!cancelled) setLoading(false) })
    return () => { cancelled = true }
  }, [])

  const filteredDocuments = useMemo(() => {
    const normalizedQuery = query.trim().toLocaleLowerCase()
    return documents.filter((document) => {
      const matchesCategory = categoryFilter === 'すべて' || document.category === categoryFilter
      const searchableText = `${document.number} ${document.customerName} ${document.vehicle} ${document.plate}`.toLocaleLowerCase()
      return matchesCategory && (!normalizedQuery || searchableText.includes(normalizedQuery))
    })
  }, [categoryFilter, documents, query])

  const selectedDocument = filteredDocuments.find((document) => document.id === selectedDocumentId) ?? filteredDocuments[0] ?? null
  const totals = selectedDocument ? calculateMaintenanceStatementTotals(selectedDocument, settings.tax.rounding) : null

  function updateItem(itemId: string, field: 'kind' | 'description' | 'quantity' | 'unit' | 'unitPrice' | 'technicalFee' | 'summary', value: string) {
    if (!selectedDocument) return
    const nextValue = field === 'kind' ? value as MaintenanceItemKind : field === 'description' || field === 'unit' || field === 'summary' ? value : Number(value) || 0
    setDocuments((current) => current.map((document) => document.id !== selectedDocument.id ? document : { ...document, items: document.items.map((item) => item.id === itemId ? { ...item, [field]: nextValue } : item) }))
    setSavedDocumentId('')
  }

  function addItem() {
    if (!selectedDocument || selectedDocument.items.length >= 18) return
    const newItem: MaintenanceLineItem = { id: `maintenance-item-${Date.now()}`, kind: '作業', description: '', quantity: 1, unit: '式', unitPrice: 0, technicalFee: 0, summary: '' }
    setDocuments((current) => current.map((document) => document.id === selectedDocument.id ? { ...document, items: [...document.items, newItem] } : document))
    setSavedDocumentId('')
  }

  function removeItem(itemId: string) {
    if (!selectedDocument) return
    setDocuments((current) => current.map((document) => document.id === selectedDocument.id ? { ...document, items: document.items.filter((item) => item.id !== itemId) } : document))
    setSavedDocumentId('')
  }

  function updateFee(key: MaintenanceFeeKey, value: string) {
    if (!selectedDocument) return
    const nextValue = Number(value) || 0
    setDocuments((current) => current.map((document) => {
      if (document.id !== selectedDocument.id) return document
      return key === '調整額' ? { ...document, adjustment: nextValue } : { ...document, fees: { ...document.fees, [key]: nextValue } }
    }))
    setSavedDocumentId('')
  }

  function updateDetails(details: MaintenanceDocumentDetails) {
    if (!selectedDocument) return
    setDocuments((current) => current.map((document) => document.id === selectedDocument.id ? { ...document, details } : document))
    setSavedDocumentId('')
  }

  function updateHeader(field: 'number' | 'type' | 'status' | 'category' | 'customerId' | 'vehicleId' | 'intakeDate' | 'plannedReleaseDate' | 'completionDate' | 'issuedAt' | 'dueDate' | 'note', value: string) {
    if (!selectedDocument) return
    setDocuments((current) => current.map((document) => document.id !== selectedDocument.id ? document : updateMaintenanceHeader(document, field, value, customers)))
    setSavedDocumentId('')
  }

  async function archiveSelectedDocument() {
    if (!selectedDocument || saving) return
    if (!window.confirm(`${selectedDocument.number}をアーカイブしますか？`)) return
    setSaving(true)
    setError('')
    try {
      await archiveMaintenanceDocument(selectedDocument.id)
      setDocuments((current) => current.filter((document) => document.id !== selectedDocument.id))
      setSelectedDocumentId('')
      setSavedDocumentId('')
    } catch (reason: unknown) {
      setError(reason instanceof Error ? reason.message : '整備書類をアーカイブできませんでした。')
    } finally {
      setSaving(false)
    }
  }

  async function saveSelectedDocument() {
    if (!selectedDocument) return
    setSaving(true)
    setError('')
    try {
      const saved = await updateMaintenanceDocument(selectedDocument.id, toMaintenanceInput(selectedDocument, settings.tax.rounding))
      setDocuments((current) => current.map((document) => document.id === saved.id ? saved : document))
      setSavedDocumentId(saved.id)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : '整備書類を保存できませんでした。')
    } finally {
      setSaving(false)
    }
  }

  async function createDocument(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setSaving(true)
    setError('')
    try {
      const created = await createMaintenanceDocument({
        type: createForm.type,
        status: '下書き',
        category: createForm.category,
        customerId: createForm.customerId,
        vehicleId: createForm.vehicleId,
        intakeDate: createForm.intakeDate,
        plannedReleaseDate: createForm.plannedReleaseDate,
        completionDate: '',
        dueDate: createForm.dueDate,
        taxRate: settings.tax.consumptionTaxRate / 100,
        taxRounding: settings.tax.rounding,
        fees: { ...emptyFees },
        adjustment: 0,
        note: '',
        details: structuredClone(defaultMaintenanceDocumentDetails),
        items: [{ kind: '作業', description: '', quantity: 1, unit: '式', unitPrice: 0, technicalFee: 0, summary: '' }],
      })
      setDocuments((current) => [created, ...current])
      setSelectedDocumentId(created.id)
      setCreateForm(createFormForCustomers(customers, settings.document.defaultDueDays))
      setCreateDialogOpen(false)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : '整備書類を作成できませんでした。')
    } finally {
      setSaving(false)
    }
  }

  return <>
    <div className="page-header maintenance-page-header"><div><span className="page-eyebrow">整備書類</span><h1>車検・点検・一般</h1><p>整備の受付から作業明細、見積書・請求書まで管理します。</p></div><button className="button button-primary" type="button" disabled={!customers.length} onClick={() => setCreateDialogOpen(true)}><Plus size={18} />整備書類を作成</button></div>
    {error && <div className="customer-sync-status is-error"><span>{error}</span><button className="text-button" type="button" onClick={() => window.location.reload()}>再読み込み</button></div>}
    {loading && <div className="customer-sync-status"><span>整備書類を読み込んでいます。</span></div>}
    <div className="maintenance-toolbar"><label className="maintenance-search"><Search size={18} /><span className="sr-only">整備書類を検索</span><input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="書類番号、顧客名、車名で検索" /></label><div className="maintenance-filter-tabs" aria-label="入庫区分"><button className={categoryFilter === 'すべて' ? 'is-active' : ''} type="button" onClick={() => setCategoryFilter('すべて')}>すべて</button>{maintenanceCategoryOptions.map((category) => <button className={categoryFilter === category ? 'is-active' : ''} key={category} type="button" onClick={() => setCategoryFilter(category)}>{category}</button>)}</div></div>
    <div className="maintenance-workspace"><MaintenanceDocumentList documents={filteredDocuments} selectedDocumentId={selectedDocument?.id ?? ''} onSelect={setSelectedDocumentId} />{selectedDocument && totals ? <MaintenanceDocumentDetail document={selectedDocument} customers={customers} settings={settings} itemPresets={settings.maintenanceItemPresets} view={documentView} saving={saving} saved={savedDocumentId === selectedDocument.id} onViewChange={setDocumentView} onUpdateHeader={updateHeader} onUpdateDetails={updateDetails} onSave={() => void saveSelectedDocument()} onArchive={() => void archiveSelectedDocument()} onPdfDownload={() => void downloadMaintenanceDocumentPdf(selectedDocument, settings)} onPdfPreview={() => void previewMaintenanceDocumentPdf(selectedDocument, settings)} onUpdateItem={updateItem} onAddItem={addItem} onRemoveItem={removeItem} onUpdateFee={updateFee} /> : <div className="panel maintenance-empty"><ClipboardCheck size={30} /><strong>整備書類が見つかりません</strong><span>{loading ? '読み込み中です。' : '検索条件または入庫区分を変更してください。'}</span></div>}</div>
    {createDialogOpen && <MaintenanceDocumentDialog form={createForm} customers={customers} onChange={setCreateForm} onClose={() => { setCreateDialogOpen(false); setCreateForm(createFormForCustomers(customers, settings.document.defaultDueDays)) }} onSubmit={createDocument} />}
  </>
}

function MaintenanceDocumentList({ documents, selectedDocumentId, onSelect }: { documents: MaintenanceDocument[]; selectedDocumentId: string; onSelect: (id: string) => void }) {
  return <section className="panel maintenance-list-panel"><div className="maintenance-list-header"><div><h2>整備書類</h2><span>書類を選択すると詳細を表示します</span></div><span className="results-count">{documents.length}件</span></div><div className="maintenance-document-list">{documents.map((document) => <button className={`maintenance-document-card${document.id === selectedDocumentId ? ' is-selected' : ''}`} key={document.id} type="button" onClick={() => onSelect(document.id)}><div className="maintenance-card-top"><MaintenanceDocumentTypeTag type={document.type} /><span className={`maintenance-category-badge maintenance-category-${document.category}`}>{document.category}</span><MaintenanceStatusTag status={document.status} /><ChevronRight size={16} /></div><strong className="maintenance-card-number">{document.number}</strong><span className="maintenance-card-customer"><UserRound size={14} />{document.customerName}</span><span className="maintenance-card-vehicle"><CarFront size={14} />{document.vehicle} ・ {document.plate}</span><div className="maintenance-card-bottom"><span>入庫 {document.intakeDate || '未定'}</span></div></button>)}</div></section>
}

type MaintenanceHeaderField = 'number' | 'type' | 'status' | 'category' | 'customerId' | 'vehicleId' | 'intakeDate' | 'plannedReleaseDate' | 'completionDate' | 'issuedAt' | 'dueDate' | 'note'

function MaintenanceDocumentDetail({ document, customers, settings, itemPresets, view, saving, saved, onViewChange, onUpdateHeader, onUpdateDetails, onSave, onArchive, onPdfDownload, onPdfPreview, onUpdateItem, onAddItem, onRemoveItem, onUpdateFee }: MaintenanceDocumentDetailProps) {
  return <section className="panel maintenance-detail-panel">
    <div className="maintenance-detail-header">
      <div className="maintenance-detail-title"><div><div className="maintenance-detail-badges"><MaintenanceDocumentTypeTag type={document.type} /><span className={`maintenance-category-badge maintenance-category-${document.category}`}>{document.category}</span></div><h2>{document.number}</h2><small>{document.type} ・ 発行元 {settings.shop.name}</small></div><MaintenanceStatusTag status={document.status} /></div>
      <div className="maintenance-detail-actions"><button className="button button-secondary" type="button" onClick={onPdfPreview}><Eye size={16} />PDFで確認</button><button className="button button-secondary" type="button" disabled={saving} onClick={onSave}><Save size={16} />{saving ? '保存中…' : saved ? '保存済み' : '保存'}</button><button className="button button-secondary" type="button" onClick={onPdfDownload}><FileDown size={16} />出力</button><button className="button button-danger" type="button" disabled={saving} onClick={onArchive}><Archive size={16} />アーカイブ</button></div>
    </div>
    <div className="maintenance-document-tabs" role="tablist" aria-label="整備書類の表示"><button id="maintenance-document-edit-tab" className={view === 'edit' ? 'is-active' : ''} type="button" role="tab" aria-selected={view === 'edit'} aria-controls="maintenance-document-edit-panel" onClick={() => onViewChange('edit')}><FileText size={16} />入力</button><button id="maintenance-document-preview-tab" className={view === 'preview' ? 'is-active' : ''} type="button" role="tab" aria-selected={view === 'preview'} aria-controls="maintenance-document-preview-panel" onClick={() => onViewChange('preview')}><Eye size={16} />プレビュー</button></div>
    {view === 'edit'
      ? <div id="maintenance-document-edit-panel" className="maintenance-detail-content" role="tabpanel" aria-labelledby="maintenance-document-edit-tab"><MaintenanceDocumentEditor document={document} customers={customers} onUpdateHeader={onUpdateHeader} /></div>
      : <div id="maintenance-document-preview-panel" className="maintenance-detail-content maintenance-preview-content" role="tabpanel" aria-labelledby="maintenance-document-preview-tab"><MaintenancePreview document={document} settings={settings} itemPresets={itemPresets} onUpdateHeader={onUpdateHeader} onUpdateDetails={onUpdateDetails} onUpdateItem={onUpdateItem} onRemoveItem={onRemoveItem} onUpdateFee={onUpdateFee} onAddItem={onAddItem} /></div>}
  </section>
}

type MaintenanceItemField = MaintenanceStatementItemField
type MaintenanceDocumentDetailProps = {
  document: MaintenanceDocument
  customers: Customer[]
  settings: AppSettings
  itemPresets: string[]
  view: MaintenanceDocumentView
  saving: boolean
  saved: boolean
  onViewChange: (view: MaintenanceDocumentView) => void
  onUpdateHeader: (field: MaintenanceHeaderField, value: string) => void
  onUpdateDetails: (details: MaintenanceDocumentDetails) => void
  onSave: () => void
  onArchive: () => void
  onPdfDownload: () => void
  onPdfPreview: () => void
  onUpdateItem: (itemId: string, field: MaintenanceItemField, value: string) => void
  onAddItem: () => void
  onRemoveItem: (itemId: string) => void
  onUpdateFee: (key: MaintenanceFeeKey, value: string) => void
}

function MaintenanceDocumentEditor({ document, customers, onUpdateHeader }: { document: MaintenanceDocument; customers: Customer[]; onUpdateHeader: (field: MaintenanceHeaderField, value: string) => void }) {
  const selectedCustomer = customers.find((customer) => customer.id === document.customerId)
  return <>
    <section className="document-header-editor maintenance-input-panel"><div className="document-header-editor-title"><div><h3>整備書類基本情報</h3><span>書類種別、顧客・車両、入庫日・出庫予定日などの基本情報を入力できます。</span></div></div><div className="form-grid"><label className="form-field"><span>書類種別</span><select value={document.type} onChange={(event) => onUpdateHeader('type', event.target.value)}>{maintenanceDocumentTypeOptions.map((type) => <option key={type}>{type}</option>)}</select></label><label className="form-field"><span>状態</span><select value={document.status} onChange={(event) => onUpdateHeader('status', event.target.value)}>{maintenanceStatusOptions.map((status) => <option key={status}>{status}</option>)}</select></label><label className="form-field"><span>入庫区分</span><select value={document.category} onChange={(event) => onUpdateHeader('category', event.target.value)}>{maintenanceCategoryOptions.map((category) => <option key={category}>{category}</option>)}</select></label><label className="form-field"><span>顧客</span><select value={document.customerId} onChange={(event) => onUpdateHeader('customerId', event.target.value)}>{customers.map((customer) => <option key={customer.id} value={customer.id}>{customer.name}</option>)}</select></label><label className="form-field"><span>対象車両</span><select value={document.vehicleId} onChange={(event) => onUpdateHeader('vehicleId', event.target.value)}><option value="">車両を指定しない</option>{selectedCustomer?.vehicles.map((vehicle) => <option key={vehicle.id} value={vehicle.id}>{vehicle.maker} {vehicle.model} ・ {vehicle.plate || '登録番号なし'}</option>)}</select></label><label className="form-field"><span>書類日付</span><input type="date" value={document.issuedAt.replaceAll('/', '-')} onChange={(event) => onUpdateHeader('issuedAt', event.target.value.replaceAll('-', '/'))} /></label><label className="form-field"><span>入庫日</span><input type="date" value={document.intakeDate.replaceAll('/', '-')} onChange={(event) => onUpdateHeader('intakeDate', event.target.value.replaceAll('-', '/'))} /></label><label className="form-field"><span>出庫予定日</span><input type="date" value={document.plannedReleaseDate.replaceAll('/', '-')} onChange={(event) => onUpdateHeader('plannedReleaseDate', event.target.value.replaceAll('-', '/'))} /></label><label className="form-field"><span>完了日</span><input type="date" value={document.completionDate.replaceAll('/', '-')} onChange={(event) => onUpdateHeader('completionDate', event.target.value.replaceAll('-', '/'))} /></label><label className="form-field"><span>支払期限</span><input type="date" value={document.dueDate.replaceAll('/', '-')} onChange={(event) => onUpdateHeader('dueDate', event.target.value.replaceAll('-', '/'))} /></label></div></section>
    <details className="maintenance-details-accordion"><summary><span>詳細設定</span><ChevronDown size={16} aria-hidden="true" /></summary><div className="maintenance-details-accordion-content" /></details>
  </>
}

function MaintenancePreview({ document, settings, itemPresets, onUpdateHeader, onUpdateDetails, onUpdateItem, onRemoveItem, onUpdateFee, onAddItem }: { document: MaintenanceDocument; settings: AppSettings; itemPresets: string[]; onUpdateHeader: (field: MaintenanceHeaderField, value: string) => void; onUpdateDetails: (details: MaintenanceDocumentDetails) => void; onUpdateItem: (itemId: string, field: MaintenanceItemField, value: string) => void; onRemoveItem: (itemId: string) => void; onUpdateFee: (key: MaintenanceFeeKey, value: string) => void; onAddItem: () => void }) {
  const svg = useMemo(() => buildMaintenanceStatementSvg(document, settings, { hideEditableValues: true }), [document, settings])
  return <div className="maintenance-preview-shell">
    <div className="maintenance-statement-frame"><div className="maintenance-statement"><div dangerouslySetInnerHTML={{ __html: svg }} /><MaintenanceStatementEditor document={document} itemPresets={itemPresets} onUpdateHeader={onUpdateHeader} onUpdateDetails={onUpdateDetails} onUpdateItem={onUpdateItem} onRemoveItem={onRemoveItem} onUpdateFee={onUpdateFee} onAddItem={onAddItem} /></div></div>
  </div>
}

function MaintenanceStatusTag({ status }: { status: MaintenanceStatus }) { const tone = status === '完了' ? 'normal' : status === '入金待ち' ? 'warning' : status === 'アーカイブ済み' ? 'danger' : 'open'; return <span className={`maintenance-status-tag maintenance-status-${tone}`}><span className="status-dot" />{status}</span> }

function MaintenanceDocumentTypeTag({ type }: { type: MaintenanceDocumentType }) { const tone = type === '整備請求書' ? 'invoice' : 'estimate'; return <span className={`maintenance-document-type-badge maintenance-document-type-${tone}`}>{type === '整備請求書' ? '請求書' : '見積書'}</span> }

function MaintenanceDocumentDialog({ form, customers, onChange, onClose, onSubmit }: { form: MaintenanceCreateForm; customers: Customer[]; onChange: (form: MaintenanceCreateForm) => void; onClose: () => void; onSubmit: (event: FormEvent<HTMLFormElement>) => void }) {
  const selectedCustomer = customers.find((customer) => customer.id === form.customerId)
  const vehicles = selectedCustomer?.vehicles ?? []
  return <div className="modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose() }}><section className="modal" role="dialog" aria-modal="true" aria-labelledby="maintenance-modal-title"><div className="modal-header"><h2 id="maintenance-modal-title">整備書類を作成</h2><button className="modal-close" type="button" aria-label="閉じる" onClick={onClose}><X size={19} /></button></div><form className="modal-form" onSubmit={onSubmit}><p className="modal-description"><ClipboardCheck size={16} />入庫する顧客・車両と整備内容を登録します。</p><div className="form-grid"><label className="form-field"><span>書類種別<em>必須</em></span><select required value={form.type} onChange={(event) => onChange({ ...form, type: event.target.value as MaintenanceDocumentType })}>{maintenanceDocumentTypeOptions.map((type) => <option key={type}>{type}</option>)}</select></label><label className="form-field"><span>入庫区分<em>必須</em></span><select required value={form.category} onChange={(event) => onChange({ ...form, category: event.target.value as IntakeCategory })}>{maintenanceCategoryOptions.map((category) => <option key={category}>{category}</option>)}</select></label><label className="form-field"><span>顧客<em>必須</em></span><select required autoFocus value={form.customerId} onChange={(event) => { const nextCustomer = customers.find((customer) => customer.id === event.target.value); onChange({ ...form, customerId: event.target.value, vehicleId: nextCustomer?.vehicles[0]?.id ?? '' }) }}><option value="">顧客を選択してください</option>{customers.map((customer) => <option key={customer.id} value={customer.id}>{customer.name}（{customer.phone || '電話番号なし'}）</option>)}</select></label><label className="form-field"><span>対象車両<em>必須</em></span><select required value={form.vehicleId} disabled={!vehicles.length} onChange={(event) => onChange({ ...form, vehicleId: event.target.value })}><option value="">車両を選択してください</option>{vehicles.map((vehicle) => <option key={vehicle.id} value={vehicle.id}>{vehicle.maker} {vehicle.model} ・ {vehicle.plate || '登録番号なし'}</option>)}</select></label><FormField label="入庫日"><input type="date" value={form.intakeDate.replaceAll('/', '-')} onChange={(event) => onChange({ ...form, intakeDate: event.target.value.replaceAll('-', '/') })} /></FormField><FormField label="出庫予定日"><input type="date" value={form.plannedReleaseDate.replaceAll('/', '-')} onChange={(event) => onChange({ ...form, plannedReleaseDate: event.target.value.replaceAll('-', '/') })} /></FormField><FormField label="支払期限"><input type="date" value={form.dueDate.replaceAll('/', '-')} onChange={(event) => onChange({ ...form, dueDate: event.target.value.replaceAll('-', '/') })} /></FormField></div><div className="modal-footer"><button className="button button-secondary" type="button" onClick={onClose}>キャンセル</button><button className="button button-primary" type="submit"><Plus size={16} />作成する</button></div></form></section></div>
}

function FormField({ label, required, children }: { label: string; required?: boolean; children: ReactNode }) { return <label className="form-field"><span>{label}{required && <em>必須</em>}</span>{children}</label> }

function toMaintenanceInput(document: MaintenanceDocument, taxRounding: AppSettings['tax']['rounding']): MaintenanceDocumentInput { return { number: document.number, type: document.type, status: document.status, category: document.category, customerId: document.customerId, vehicleId: document.vehicleId, issuedAt: document.issuedAt, intakeDate: document.intakeDate, plannedReleaseDate: document.plannedReleaseDate, completionDate: document.completionDate, dueDate: document.dueDate, taxRate: document.taxRate, taxRounding, fees: document.fees, adjustment: document.adjustment, note: document.note, details: document.details, items: document.items.map(({ id: _id, ...item }) => item) } }
function updateMaintenanceHeader(document: MaintenanceDocument, field: MaintenanceHeaderField, value: string, customers: Customer[]): MaintenanceDocument {
  if (field !== 'customerId' && field !== 'vehicleId') return { ...document, [field]: value }

  const nextCustomer = customers.find((customer) => customer.id === (field === 'customerId' ? value : document.customerId))
  const nextVehicleId = field === 'customerId' ? nextCustomer?.vehicles[0]?.id ?? '' : value
  const nextVehicle = nextCustomer?.vehicles.find((vehicle) => vehicle.id === nextVehicleId)
  const nextDetails = field === 'customerId'
    ? { ...document.details, customerOverride: null, vehicleOverride: null }
    : { ...document.details, vehicleOverride: null }

  return {
    ...document,
    [field]: value,
    customerId: field === 'customerId' ? value : document.customerId,
    vehicleId: nextVehicleId,
    customerName: nextCustomer?.name ?? '',
    phone: nextCustomer?.phone ?? '',
    customerDetails: mapMaintenanceCustomerDetails(nextCustomer),
    vehicle: nextVehicle ? [nextVehicle.maker, nextVehicle.model].filter(Boolean).join(' ') : '',
    plate: nextVehicle?.plate ?? '',
    mileage: nextVehicle?.mileage ?? '',
    vehicleDetails: mapMaintenanceVehicleDetails(nextVehicle),
    details: nextDetails,
  }
}

function mapMaintenanceCustomerDetails(customer: Customer | undefined): MaintenanceCustomerDetails {
  return {
    name: customer?.name ?? '',
    kana: customer?.kana ?? '',
    phone: customer?.phone ?? '',
    postalCode: customer?.postalCode ?? '',
    address: customer?.address ?? '',
  }
}

function mapMaintenanceVehicleDetails(vehicle: Customer['vehicles'][number] | undefined): MaintenanceVehicleDetails | null {
  if (!vehicle) return null
  return {
    maker: vehicle.maker,
    name: vehicle.model,
    modelType: vehicle.modelType,
    plate: vehicle.plate,
    vin: vehicle.vin,
    year: vehicle.year,
    inspectionDate: vehicle.inspectionDate,
    mileage: vehicle.mileage,
    color: vehicle.color,
    displacement: vehicle.displacement,
    transmission: vehicle.transmission,
    inspectionRecordAvailable: vehicle.inspectionRecordAvailable,
  }
}
function createFormForCustomers(customers: Customer[], defaultDueDays: number): MaintenanceCreateForm { const customer = customers[0]; return { ...emptyCreateForm, dueDate: addDaysDisplay(defaultDueDays), customerId: customer?.id ?? '', vehicleId: customer?.vehicles[0]?.id ?? '' } }
function todayDisplay() { return new Date().toISOString().slice(0, 10).replaceAll('-', '/') }
function addDaysDisplay(days: number) { const date = new Date(); date.setDate(date.getDate() + days); return date.toISOString().slice(0, 10).replaceAll('-', '/') }
