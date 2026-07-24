import { useMemo, useState, type FormEvent, type ReactNode } from 'react'
import {
  CalendarClock,
  CarFront,
  ChevronRight,
  CircleDollarSign,
  ClipboardCheck,
  FileDown,
  Pencil,
  Plus,
  Search,
  Trash2,
  UserRound,
  X,
} from 'lucide-react'

type MaintenanceDocumentType = '整備見積書' | '納品書' | '整備請求書'
type MaintenanceStatus = '受付中' | '作業中' | '完了'
type IntakeCategory = '車検' | '法定点検' | '一般整備'
type MaintenanceItemKind = '作業' | '部品'
type CategoryFilter = 'すべて' | IntakeCategory

type MandatoryFees = {
  自賠責: number
  重量税: number
  印紙代: number
  リサイクル料金: number
}

type MaintenanceLineItem = {
  id: string
  kind: MaintenanceItemKind
  description: string
  quantity: number
  unit: string
  unitPrice: number
}

type MaintenanceDocument = {
  id: string
  number: string
  type: MaintenanceDocumentType
  status: MaintenanceStatus
  category: IntakeCategory
  customerName: string
  phone: string
  vehicle: string
  plate: string
  mileage: string
  intakeDate: string
  plannedReleaseDate: string
  taxRate: number
  fees: MandatoryFees
  adjustment: number
  note: string
  items: MaintenanceLineItem[]
}

const initialMaintenanceDocuments: MaintenanceDocument[] = [
  {
    id: 'maintenance-001', number: 'M-2026-118', type: '整備見積書', status: '受付中', category: '一般整備', customerName: '佐藤 太郎', phone: '090-1234-5678', vehicle: 'トヨタ プリウス', plate: '品川 500 あ 1234', mileage: '68,420 km', intakeDate: '2026/07/25', plannedReleaseDate: '2026/07/27', taxRate: 0.1, fees: { 自賠責: 0, 重量税: 0, 印紙代: 0, リサイクル料金: 0 }, adjustment: -3000, note: '左後ドア小傷を次回点検時に確認。',
    items: [{ id: 'maintenance-item-001', kind: '作業', description: 'エンジンオイル交換', quantity: 1, unit: '式', unitPrice: 6800 }, { id: 'maintenance-item-002', kind: '部品', description: 'オイルフィルター', quantity: 1, unit: '個', unitPrice: 1800 }, { id: 'maintenance-item-003', kind: '作業', description: '12か月点検', quantity: 1, unit: '式', unitPrice: 15000 }],
  },
  {
    id: 'maintenance-002', number: 'M-2026-114', type: '納品書', status: '作業中', category: '法定点検', customerName: '田中 花子', phone: '080-2345-6789', vehicle: 'ホンダ フィット', plate: '横浜 300 い 5678', mileage: '42,100 km', intakeDate: '2026/07/24', plannedReleaseDate: '2026/07/26', taxRate: 0.1, fees: { 自賠責: 0, 重量税: 0, 印紙代: 0, リサイクル料金: 0 }, adjustment: 0, note: '代車：軽自動車を手配。',
    items: [{ id: 'maintenance-item-004', kind: '作業', description: '24か月点検', quantity: 1, unit: '式', unitPrice: 24000 }, { id: 'maintenance-item-005', kind: '部品', description: 'ブレーキフルード', quantity: 1, unit: '個', unitPrice: 3200 }],
  },
  {
    id: 'maintenance-003', number: 'M-2026-108', type: '整備請求書', status: '完了', category: '車検', customerName: '鈴木 一郎', phone: '070-3456-7890', vehicle: 'ニッサン ノート', plate: '大宮 400 う 9012', mileage: '93,750 km', intakeDate: '2026/07/20', plannedReleaseDate: '2026/07/22', taxRate: 0.1, fees: { 自賠責: 17650, 重量税: 24600, 印紙代: 1800, リサイクル料金: 9800 }, adjustment: 0, note: '車検整備完了。次回オイル交換は3か月後。',
    items: [{ id: 'maintenance-item-006', kind: '作業', description: '車検基本整備', quantity: 1, unit: '式', unitPrice: 42000 }, { id: 'maintenance-item-007', kind: '部品', description: 'ワイパーゴム', quantity: 2, unit: '本', unitPrice: 1500 }],
  },
]

const initialCreateForm = { type: '整備見積書' as MaintenanceDocumentType, category: '一般整備' as IntakeCategory, customerName: '佐藤 太郎', phone: '090-1234-5678', vehicle: 'トヨタ プリウス', plate: '品川 500 あ 1234', mileage: '68,420 km', intakeDate: '2026/07/25', plannedReleaseDate: '2026/07/27' }

export function MaintenancePage() {
  const [documents, setDocuments] = useState<MaintenanceDocument[]>(initialMaintenanceDocuments)
  const [query, setQuery] = useState('')
  const [categoryFilter, setCategoryFilter] = useState<CategoryFilter>('すべて')
  const [selectedDocumentId, setSelectedDocumentId] = useState(initialMaintenanceDocuments[0].id)
  const [createDialogOpen, setCreateDialogOpen] = useState(false)
  const [createForm, setCreateForm] = useState(initialCreateForm)

  const filteredDocuments = useMemo(() => {
    const normalizedQuery = query.trim().toLocaleLowerCase()
    return documents.filter((document) => {
      const matchesCategory = categoryFilter === 'すべて' || document.category === categoryFilter
      const searchableText = `${document.number} ${document.customerName} ${document.vehicle} ${document.plate}`.toLocaleLowerCase()
      return matchesCategory && (!normalizedQuery || searchableText.includes(normalizedQuery))
    })
  }, [categoryFilter, documents, query])

  const selectedDocument = filteredDocuments.find((document) => document.id === selectedDocumentId) ?? filteredDocuments[0] ?? null
  const totals = selectedDocument ? calculateMaintenanceTotals(selectedDocument) : null

  function updateItem(itemId: string, field: 'kind' | 'description' | 'quantity' | 'unitPrice', value: string) {
    if (!selectedDocument) return
    const nextValue = field === 'kind' ? value as MaintenanceItemKind : field === 'description' ? value : Number(value)
    setDocuments((current) => current.map((document) => document.id !== selectedDocument.id ? document : { ...document, items: document.items.map((item) => item.id === itemId ? { ...item, [field]: nextValue } : item) }))
  }

  function addItem() {
    if (!selectedDocument) return
    const newItem: MaintenanceLineItem = { id: `maintenance-item-${Date.now()}`, kind: '作業', description: '', quantity: 1, unit: '式', unitPrice: 0 }
    setDocuments((current) => current.map((document) => document.id === selectedDocument.id ? { ...document, items: [...document.items, newItem] } : document))
  }

  function removeItem(itemId: string) {
    if (!selectedDocument) return
    setDocuments((current) => current.map((document) => document.id === selectedDocument.id ? { ...document, items: document.items.filter((item) => item.id !== itemId) } : document))
  }

  function updateFee(key: keyof MandatoryFees, value: string) {
    if (!selectedDocument) return
    setDocuments((current) => current.map((document) => document.id !== selectedDocument.id ? document : { ...document, fees: { ...document.fees, [key]: Number(value) } }))
  }

  function updateAdjustment(value: string) {
    if (!selectedDocument) return
    setDocuments((current) => current.map((document) => document.id === selectedDocument.id ? { ...document, adjustment: Number(value) } : document))
  }

  function createDocument(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const newDocument: MaintenanceDocument = {
      id: `maintenance-${Date.now()}`, number: `M-2026-${String(documents.length + 119).padStart(3, '0')}`, type: createForm.type, status: '受付中', category: createForm.category, customerName: createForm.customerName, phone: createForm.phone, vehicle: createForm.vehicle, plate: createForm.plate, mileage: createForm.mileage, intakeDate: createForm.intakeDate, plannedReleaseDate: createForm.plannedReleaseDate, taxRate: 0.1, fees: { 自賠責: 0, 重量税: 0, 印紙代: 0, リサイクル料金: 0 }, adjustment: 0, note: '', items: [{ id: `maintenance-item-${Date.now()}`, kind: '作業', description: '', quantity: 1, unit: '式', unitPrice: 0 }],
    }
    setDocuments((current) => [newDocument, ...current])
    setSelectedDocumentId(newDocument.id)
    setCreateForm(initialCreateForm)
    setCreateDialogOpen(false)
  }

  return <><div className="page-header maintenance-page-header"><div><span className="page-eyebrow">整備書類</span><h1>車検・点検・一般</h1><p>整備の受付から作業明細、納品書・請求書まで管理します。</p></div><button className="button button-primary" type="button" onClick={() => setCreateDialogOpen(true)}><Plus size={18} />整備書類を作成</button></div><div className="maintenance-toolbar"><label className="maintenance-search"><Search size={18} /><span className="sr-only">整備書類を検索</span><input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="書類番号、顧客名、車名で検索" /></label><div className="maintenance-filter-tabs" aria-label="入庫区分"><button className={categoryFilter === 'すべて' ? 'is-active' : ''} type="button" onClick={() => setCategoryFilter('すべて')}>すべて</button>{(['車検', '法定点検', '一般整備'] as IntakeCategory[]).map((category) => <button className={categoryFilter === category ? 'is-active' : ''} key={category} type="button" onClick={() => setCategoryFilter(category)}>{category}</button>)}</div><span className="maintenance-result-summary"><strong>{filteredDocuments.length}件</strong><span>整備書類</span></span></div><div className="maintenance-workspace"><MaintenanceDocumentList documents={filteredDocuments} selectedDocumentId={selectedDocument?.id ?? ''} onSelect={setSelectedDocumentId} />{selectedDocument && totals ? <MaintenanceDocumentDetail document={selectedDocument} totals={totals} onUpdateItem={updateItem} onAddItem={addItem} onRemoveItem={removeItem} onUpdateFee={updateFee} onUpdateAdjustment={updateAdjustment} /> : <div className="panel maintenance-empty"><ClipboardCheck size={30} /><strong>整備書類が見つかりません</strong><span>検索条件または入庫区分を変更してください。</span></div>}</div>{createDialogOpen && <MaintenanceDocumentDialog form={createForm} onChange={setCreateForm} onClose={() => { setCreateDialogOpen(false); setCreateForm(initialCreateForm) }} onSubmit={createDocument} />}</>
}

function MaintenanceDocumentList({ documents, selectedDocumentId, onSelect }: { documents: MaintenanceDocument[]; selectedDocumentId: string; onSelect: (id: string) => void }) {
  return <section className="panel maintenance-list-panel"><div className="maintenance-list-header"><div><h2>整備書類</h2><span>書類を選択すると詳細を表示します</span></div><span className="results-count">{documents.length}件</span></div><div className="maintenance-document-list">{documents.map((document) => <button className={`maintenance-document-card${document.id === selectedDocumentId ? ' is-selected' : ''}`} key={document.id} type="button" onClick={() => onSelect(document.id)}><div className="maintenance-card-top"><span className={`maintenance-category-badge maintenance-category-${document.category}`}>{document.category}</span><MaintenanceStatusTag status={document.status} /><ChevronRight size={16} /></div><strong className="maintenance-card-number">{document.number}</strong><span className="maintenance-card-customer"><UserRound size={14} />{document.customerName}</span><span className="maintenance-card-vehicle"><CarFront size={14} />{document.vehicle} ・ {document.plate}</span><div className="maintenance-card-bottom"><span>入庫 {document.intakeDate}</span><strong>{document.type}</strong></div></button>)}</div></section>
}

function MaintenanceDocumentDetail({ document, totals, onUpdateItem, onAddItem, onRemoveItem, onUpdateFee, onUpdateAdjustment }: { document: MaintenanceDocument; totals: MaintenanceTotals; onUpdateItem: (itemId: string, field: 'kind' | 'description' | 'quantity' | 'unitPrice', value: string) => void; onAddItem: () => void; onRemoveItem: (itemId: string) => void; onUpdateFee: (key: keyof MandatoryFees, value: string) => void; onUpdateAdjustment: (value: string) => void }) {
  return <section className="panel maintenance-detail-panel"><div className="maintenance-detail-header"><div className="maintenance-detail-title"><div><span className={`maintenance-category-badge maintenance-category-${document.category}`}>{document.category}</span><h2>{document.number}</h2><small>{document.type}</small></div><MaintenanceStatusTag status={document.status} /></div><div className="maintenance-detail-actions"><button className="button button-secondary" type="button"><Pencil size={16} />編集</button><button className="button button-secondary" type="button"><FileDown size={16} />PDF出力</button></div></div><div className="maintenance-detail-content"><div className="maintenance-context-grid"><div className="maintenance-context-card"><span className="maintenance-context-label"><UserRound size={15} />顧客</span><strong>{document.customerName}</strong><small>{document.phone}</small></div><div className="maintenance-context-card"><span className="maintenance-context-label"><CarFront size={15} />対象車両</span><strong>{document.vehicle}</strong><small>{document.plate} ・ {document.mileage}</small></div></div><section className="intake-summary-panel"><div className="intake-summary-header"><div><h3>入庫・出庫情報</h3><span>整備の予定と入庫区分を管理します</span></div><span className="intake-summary-icon"><CalendarClock size={19} /></span></div><div className="intake-summary-grid"><SummaryField label="入庫日" value={document.intakeDate} /><SummaryField label="出庫予定日" value={document.plannedReleaseDate} /><SummaryField label="入庫区分" value={document.category} /><SummaryField label="入庫時走行距離" value={document.mileage} /></div></section><section className="maintenance-items-panel"><div className="maintenance-items-header"><div><h3>作業・部品明細</h3><span>作業内容や部品名を明細行として登録します</span></div><button className="text-button" type="button" onClick={onAddItem}><Plus size={15} />明細を追加</button></div><div className="maintenance-items-table"><div className="maintenance-items-head"><span>区分</span><span>作業内容・部品名</span><span>数量</span><span>単位</span><span>単価</span><span>金額</span><span /></div>{document.items.map((item) => <div className="maintenance-item-row" key={item.id}><select aria-label="明細区分" value={item.kind} onChange={(event) => onUpdateItem(item.id, 'kind', event.target.value)}><option>作業</option><option>部品</option></select><input aria-label="作業内容・部品名" value={item.description} onChange={(event) => onUpdateItem(item.id, 'description', event.target.value)} placeholder="作業内容・部品名" /><input className="maintenance-number-input" aria-label="数量" type="number" min="0" value={item.quantity} onChange={(event) => onUpdateItem(item.id, 'quantity', event.target.value)} /><span>{item.unit}</span><input className="maintenance-price-input" aria-label="単価" type="number" value={item.unitPrice} onChange={(event) => onUpdateItem(item.id, 'unitPrice', event.target.value)} /><strong>{formatYen(item.quantity * item.unitPrice)}</strong><button className="maintenance-item-remove" type="button" aria-label="明細を削除" onClick={() => onRemoveItem(item.id)}><Trash2 size={15} /></button></div>)}</div></section><section className="maintenance-fees-panel"><div className="maintenance-fees-header"><div><h3>税金・保険料・その他</h3><span>車検時の法定費用やリサイクル料金を登録します</span></div><CircleDollarSign size={19} /></div><div className="maintenance-fees-grid"><FeeField label="自賠責保険" value={document.fees.自賠責} onChange={(value) => onUpdateFee('自賠責', value)} /><FeeField label="重量税" value={document.fees.重量税} onChange={(value) => onUpdateFee('重量税', value)} /><FeeField label="印紙代" value={document.fees.印紙代} onChange={(value) => onUpdateFee('印紙代', value)} /><FeeField label="リサイクル料金" value={document.fees.リサイクル料金} onChange={(value) => onUpdateFee('リサイクル料金', value)} /></div></section><div className="maintenance-summary-grid"><div className="maintenance-note"><span>備考</span><p>{document.note || '備考はありません。'}</p></div><div className="maintenance-totals"><div><span>作業・部品小計</span><strong>{formatYen(totals.itemsSubtotal)}</strong></div><div><span>法定費用等</span><strong>{formatYen(totals.fees)}</strong></div><div><label htmlFor="maintenance-adjustment">調整額</label><input id="maintenance-adjustment" type="number" value={document.adjustment} onChange={(event) => onUpdateAdjustment(event.target.value)} /></div><div><span>消費税（{document.taxRate * 100}%）</span><strong>{formatYen(totals.tax)}</strong></div><div className="maintenance-total-row"><span>合計金額</span><strong>{formatYen(totals.total)}</strong></div></div></div></div></section>
}

function SummaryField({ label, value }: { label: string; value: string }) {
  return <div className="intake-summary-field"><span>{label}</span><strong>{value || '未登録'}</strong></div>
}

function FeeField({ label, value, onChange }: { label: string; value: number; onChange: (value: string) => void }) {
  return <label className="maintenance-fee-field"><span>{label}</span><span className="maintenance-fee-input"><span>¥</span><input type="number" value={value} onChange={(event) => onChange(event.target.value)} /></span></label>
}

function MaintenanceStatusTag({ status }: { status: MaintenanceStatus }) {
  const tone = status === '完了' ? 'normal' : status === '作業中' ? 'warning' : 'open'
  return <span className={`maintenance-status-tag maintenance-status-${tone}`}><span className="status-dot" />{status}</span>
}

type MaintenanceTotals = { itemsSubtotal: number; fees: number; tax: number; total: number }

function calculateMaintenanceTotals(document: MaintenanceDocument): MaintenanceTotals {
  const itemsSubtotal = document.items.reduce((sum, item) => sum + item.quantity * item.unitPrice, 0)
  const fees = Object.values(document.fees).reduce((sum, fee) => sum + fee, 0)
  const taxableAmount = Math.max(0, itemsSubtotal + document.adjustment)
  const tax = Math.floor(taxableAmount * document.taxRate)
  return { itemsSubtotal, fees, tax, total: itemsSubtotal + fees + document.adjustment + tax }
}

function formatYen(amount: number) {
  return `¥${new Intl.NumberFormat('ja-JP').format(Math.round(amount))}`
}

function MaintenanceDocumentDialog({ form, onChange, onClose, onSubmit }: { form: typeof initialCreateForm; onChange: (form: typeof initialCreateForm) => void; onClose: () => void; onSubmit: (event: FormEvent<HTMLFormElement>) => void }) {
  return <div className="modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose() }}><section className="modal" role="dialog" aria-modal="true" aria-labelledby="maintenance-modal-title"><div className="modal-header"><h2 id="maintenance-modal-title">整備書類を作成</h2><button className="modal-close" type="button" aria-label="閉じる" onClick={onClose}><X size={19} /></button></div><form className="modal-form" onSubmit={onSubmit}><p className="modal-description"><ClipboardCheck size={16} />入庫する顧客・車両と整備内容を登録します。</p><div className="form-grid"><label className="form-field"><span>書類種別<em>必須</em></span><select value={form.type} onChange={(event) => onChange({ ...form, type: event.target.value as MaintenanceDocumentType })}><option>整備見積書</option><option>納品書</option><option>整備請求書</option></select></label><label className="form-field"><span>入庫区分<em>必須</em></span><select value={form.category} onChange={(event) => onChange({ ...form, category: event.target.value as IntakeCategory })}><option>車検</option><option>法定点検</option><option>一般整備</option></select></label><FormField label="顧客名" required><input autoFocus required value={form.customerName} onChange={(event) => onChange({ ...form, customerName: event.target.value })} /></FormField><FormField label="電話番号"><input value={form.phone} onChange={(event) => onChange({ ...form, phone: event.target.value })} /></FormField><FormField label="車名"><input value={form.vehicle} onChange={(event) => onChange({ ...form, vehicle: event.target.value })} /></FormField><FormField label="登録番号"><input value={form.plate} onChange={(event) => onChange({ ...form, plate: event.target.value })} /></FormField><FormField label="入庫時走行距離"><input value={form.mileage} onChange={(event) => onChange({ ...form, mileage: event.target.value })} /></FormField><FormField label="入庫日"><input type="date" value={form.intakeDate.replaceAll('/', '-')} onChange={(event) => onChange({ ...form, intakeDate: event.target.value.replaceAll('-', '/') })} /></FormField><FormField label="出庫予定日"><input type="date" value={form.plannedReleaseDate.replaceAll('/', '-')} onChange={(event) => onChange({ ...form, plannedReleaseDate: event.target.value.replaceAll('-', '/') })} /></FormField></div><div className="modal-footer"><button className="button button-secondary" type="button" onClick={onClose}>キャンセル</button><button className="button button-primary" type="submit"><Plus size={16} />作成する</button></div></form></section></div>
}

function FormField({ label, required, children }: { label: string; required?: boolean; children: ReactNode }) {
  return <label className="form-field"><span>{label}{required && <em>必須</em>}</span>{children}</label>
}
