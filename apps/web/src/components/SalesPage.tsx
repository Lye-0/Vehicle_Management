import { useMemo, useState, type FormEvent, type ReactNode } from 'react'
import {
  CarFront,
  ChevronRight,
  CircleDollarSign,
  FileDown,
  FileText,
  Pencil,
  Plus,
  Search,
  ShoppingCart,
  Trash2,
  UserRound,
  X,
} from 'lucide-react'

type SalesDocumentType = '見積書' | '注文書' | '請求書'
type SalesStatus = '下書き' | '発行済み' | '入金待ち'
type DocumentFilter = 'すべて' | SalesDocumentType

type SalesLineItem = {
  id: string
  description: string
  quantity: number
  unit: string
  unitPrice: number
}

type SalesDocument = {
  id: string
  number: string
  type: SalesDocumentType
  status: SalesStatus
  customerName: string
  phone: string
  vehicle: string
  plate: string
  issuedAt: string
  dueDate: string
  taxRate: number
  note: string
  items: SalesLineItem[]
}

const initialSalesDocuments: SalesDocument[] = [
  {
    id: 'sales-quote-001', number: 'S-2026-041', type: '見積書', status: '下書き', customerName: '佐藤 太郎', phone: '090-1234-5678', vehicle: 'トヨタ プリウス', plate: '品川 500 あ 1234', issuedAt: '2026/07/25', dueDate: '2026/08/08', taxRate: 0.1, note: '納車時に車両取扱説明を実施。',
    items: [
      { id: 'item-001', description: '車両本体価格', quantity: 1, unit: '式', unitPrice: 2680000 },
      { id: 'item-002', description: '付属品・特別仕様', quantity: 1, unit: '式', unitPrice: 120000 },
      { id: 'item-003', description: '登録代行費用', quantity: 1, unit: '式', unitPrice: 88000 },
      { id: 'item-004', description: '値引き', quantity: 1, unit: '式', unitPrice: -50000 },
    ],
  },
  {
    id: 'sales-order-002', number: 'S-2026-038', type: '注文書', status: '発行済み', customerName: '田中 花子', phone: '080-2345-6789', vehicle: 'ホンダ フィット', plate: '横浜 300 い 5678', issuedAt: '2026/07/22', dueDate: '2026/08/05', taxRate: 0.1, note: '納車予定日は別途連絡。',
    items: [{ id: 'item-005', description: '車両本体価格', quantity: 1, unit: '式', unitPrice: 1680000 }, { id: 'item-006', description: '納車費用', quantity: 1, unit: '式', unitPrice: 55000 }],
  },
  {
    id: 'sales-invoice-003', number: 'S-2026-035', type: '請求書', status: '入金待ち', customerName: '高橋 美咲', phone: '080-5678-9012', vehicle: 'マツダ CX-5', plate: '川崎 501 お 7890', issuedAt: '2026/07/18', dueDate: '2026/08/01', taxRate: 0.1, note: '銀行振込での支払い。',
    items: [{ id: 'item-007', description: '車両本体価格', quantity: 1, unit: '式', unitPrice: 3200000 }, { id: 'item-008', description: 'リサイクル料金', quantity: 1, unit: '式', unitPrice: 12800 }],
  },
  {
    id: 'sales-quote-004', number: 'S-2026-029', type: '見積書', status: '発行済み', customerName: '山田 恵子', phone: '090-4567-8901', vehicle: 'スバル インプレッサ', plate: '多摩 500 え 3456', issuedAt: '2026/07/10', dueDate: '2026/07/24', taxRate: 0.1, note: '',
    items: [{ id: 'item-009', description: '車両本体価格', quantity: 1, unit: '式', unitPrice: 2140000 }, { id: 'item-010', description: '付属品・特別仕様', quantity: 1, unit: '式', unitPrice: 78000 }],
  },
]

const initialCreateForm = { type: '見積書' as SalesDocumentType, customerName: '佐藤 太郎', phone: '090-1234-5678', vehicle: 'トヨタ プリウス', plate: '品川 500 あ 1234' }

export function SalesPage() {
  const [documents, setDocuments] = useState<SalesDocument[]>(initialSalesDocuments)
  const [query, setQuery] = useState('')
  const [filterType, setFilterType] = useState<DocumentFilter>('すべて')
  const [selectedDocumentId, setSelectedDocumentId] = useState(initialSalesDocuments[0].id)
  const [createDialogOpen, setCreateDialogOpen] = useState(false)
  const [createForm, setCreateForm] = useState(initialCreateForm)

  const filteredDocuments = useMemo(() => {
    const normalizedQuery = query.trim().toLocaleLowerCase()
    return documents.filter((document) => {
      const matchesType = filterType === 'すべて' || document.type === filterType
      const searchableText = `${document.number} ${document.customerName} ${document.vehicle} ${document.plate}`.toLocaleLowerCase()
      return matchesType && (!normalizedQuery || searchableText.includes(normalizedQuery))
    })
  }, [documents, filterType, query])

  const selectedDocument = filteredDocuments.find((document) => document.id === selectedDocumentId) ?? filteredDocuments[0] ?? null
  const selectedTotals = selectedDocument ? calculateTotals(selectedDocument) : null

  function updateLineItem(itemId: string, field: 'description' | 'quantity' | 'unitPrice', value: string) {
    if (!selectedDocument) return
    const nextValue = field === 'description' ? value : Number(value)
    setDocuments((current) => current.map((document) => document.id !== selectedDocument.id ? document : { ...document, items: document.items.map((item) => item.id === itemId ? { ...item, [field]: nextValue } : item) }))
  }

  function addLineItem() {
    if (!selectedDocument) return
    const newItem: SalesLineItem = { id: `item-${Date.now()}`, description: '', quantity: 1, unit: '式', unitPrice: 0 }
    setDocuments((current) => current.map((document) => document.id === selectedDocument.id ? { ...document, items: [...document.items, newItem] } : document))
  }

  function removeLineItem(itemId: string) {
    if (!selectedDocument) return
    setDocuments((current) => current.map((document) => document.id === selectedDocument.id ? { ...document, items: document.items.filter((item) => item.id !== itemId) } : document))
  }

  function createDocument(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const newDocument: SalesDocument = {
      id: `sales-${Date.now()}`,
      number: `S-2026-${String(documents.length + 42).padStart(3, '0')}`,
      type: createForm.type,
      status: '下書き',
      customerName: createForm.customerName,
      phone: createForm.phone,
      vehicle: createForm.vehicle,
      plate: createForm.plate,
      issuedAt: '2026/07/25',
      dueDate: '2026/08/08',
      taxRate: 0.1,
      note: '',
      items: [{ id: `item-${Date.now()}`, description: '車両本体価格', quantity: 1, unit: '式', unitPrice: 0 }],
    }
    setDocuments((current) => [newDocument, ...current])
    setSelectedDocumentId(newDocument.id)
    setCreateForm(initialCreateForm)
    setCreateDialogOpen(false)
  }

  return (
    <>
      <div className="page-header sales-page-header"><div><span className="page-eyebrow">販売書類</span><h1>販売</h1><p>見積書・注文書・請求書を車両情報と連動して管理します。</p></div><button className="button button-primary" type="button" onClick={() => setCreateDialogOpen(true)}><Plus size={18} />販売書類を作成</button></div>
      <div className="sales-toolbar"><label className="sales-search"><Search size={18} /><span className="sr-only">販売書類を検索</span><input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="書類番号、顧客名、車名で検索" /></label><div className="sales-filter-tabs" aria-label="書類種別"><button className={filterType === 'すべて' ? 'is-active' : ''} type="button" onClick={() => setFilterType('すべて')}>すべて</button>{(['見積書', '注文書', '請求書'] as SalesDocumentType[]).map((type) => <button className={filterType === type ? 'is-active' : ''} key={type} type="button" onClick={() => setFilterType(type)}>{type}</button>)}</div><span className="sales-result-summary"><strong>{filteredDocuments.length}件</strong><span>販売書類</span></span></div>
      <div className="sales-workspace"><SalesDocumentList documents={filteredDocuments} selectedDocumentId={selectedDocument?.id ?? ''} onSelect={setSelectedDocumentId} />{selectedDocument && selectedTotals ? <SalesDocumentDetail document={selectedDocument} totals={selectedTotals} onUpdateItem={updateLineItem} onAddItem={addLineItem} onRemoveItem={removeLineItem} /> : <div className="panel sales-empty"><FileText size={30} /><strong>販売書類が見つかりません</strong><span>検索条件または書類種別を変更してください。</span></div>}</div>
      {createDialogOpen && <SalesDocumentDialog form={createForm} onChange={setCreateForm} onClose={() => { setCreateDialogOpen(false); setCreateForm(initialCreateForm) }} onSubmit={createDocument} />}
    </>
  )
}

function SalesDocumentList({ documents, selectedDocumentId, onSelect }: { documents: SalesDocument[]; selectedDocumentId: string; onSelect: (id: string) => void }) {
  return <section className="panel sales-list-panel"><div className="sales-list-header"><div><h2>販売書類</h2><span>書類を選択すると詳細を表示します</span></div><span className="results-count">{documents.length}件</span></div><div className="sales-document-list">{documents.map((document) => <button className={`sales-document-card${document.id === selectedDocumentId ? ' is-selected' : ''}`} key={document.id} type="button" onClick={() => onSelect(document.id)}><div className="sales-card-top"><span className={`sales-type-badge sales-type-${document.type}`}>{document.type}</span><StatusTag status={document.status} /><ChevronRight size={16} /></div><strong className="sales-card-number">{document.number}</strong><span className="sales-card-customer"><UserRound size={14} />{document.customerName}</span><span className="sales-card-vehicle"><CarFront size={14} />{document.vehicle} ・ {document.plate}</span><div className="sales-card-bottom"><span>{document.issuedAt}</span><strong>{formatYen(calculateTotals(document).total)}</strong></div></button>)}</div></section>
}

function SalesDocumentDetail({ document, totals, onUpdateItem, onAddItem, onRemoveItem }: { document: SalesDocument; totals: SalesTotals; onUpdateItem: (itemId: string, field: 'description' | 'quantity' | 'unitPrice', value: string) => void; onAddItem: () => void; onRemoveItem: (itemId: string) => void }) {
  return <section className="panel sales-detail-panel"><div className="sales-detail-header"><div className="sales-detail-title"><div><span className={`sales-type-badge sales-type-${document.type}`}>{document.type}</span><h2>{document.number}</h2><small>{document.issuedAt} 作成</small></div><StatusTag status={document.status} /></div><div className="sales-detail-actions"><button className="button button-secondary" type="button"><Pencil size={16} />編集</button><button className="button button-secondary" type="button"><FileDown size={16} />PDF出力</button></div></div><div className="sales-detail-content"><div className="sales-context-grid"><div className="sales-context-card"><span className="sales-context-label"><UserRound size={15} />顧客</span><strong>{document.customerName}</strong><small>{document.phone}</small></div><div className="sales-context-card"><span className="sales-context-label"><CarFront size={15} />対象車両</span><strong>{document.vehicle}</strong><small>{document.plate}</small></div></div><section className="sales-items-panel"><div className="sales-items-header"><div><h3>販売明細</h3><span>車両本体・付属品・諸費用・値引きを登録します</span></div><button className="text-button" type="button" onClick={onAddItem}><Plus size={15} />明細を追加</button></div><div className="sales-items-table"><div className="sales-items-head"><span>内容</span><span>数量</span><span>単位</span><span>単価</span><span>金額</span><span /></div>{document.items.map((item) => <div className="sales-item-row" key={item.id}><input aria-label="明細内容" value={item.description} onChange={(event) => onUpdateItem(item.id, 'description', event.target.value)} placeholder="明細内容" /><input className="sales-number-input" aria-label="数量" type="number" min="0" value={item.quantity} onChange={(event) => onUpdateItem(item.id, 'quantity', event.target.value)} /><span>{item.unit}</span><input className="sales-price-input" aria-label="単価" type="number" value={item.unitPrice} onChange={(event) => onUpdateItem(item.id, 'unitPrice', event.target.value)} /><strong>{formatYen(item.quantity * item.unitPrice)}</strong><button className="sales-item-remove" type="button" aria-label="明細を削除" onClick={() => onRemoveItem(item.id)}><Trash2 size={15} /></button></div>)}</div></section><div className="sales-summary-grid"><div className="sales-note"><span>備考</span><p>{document.note || '備考はありません。'}</p></div><div className="sales-totals"><div><span>小計</span><strong>{formatYen(totals.subtotal)}</strong></div><div><span>消費税（{document.taxRate * 100}%）</span><strong>{formatYen(totals.tax)}</strong></div><div className="sales-total-row"><span>合計金額</span><strong>{formatYen(totals.total)}</strong></div></div></div><div className="sales-detail-footer"><span><ShoppingCart size={15} />支払期限：{document.dueDate}</span><span><CircleDollarSign size={15} />入金状況は入金管理で登録</span></div></div></section>
}

function StatusTag({ status }: { status: SalesStatus }) {
  const tone = status === '入金待ち' ? 'warning' : status === '発行済み' ? 'normal' : 'draft'
  return <span className={`sales-status-tag sales-status-${tone}`}><span className="status-dot" />{status}</span>
}

type SalesTotals = { subtotal: number; tax: number; total: number }

function calculateTotals(document: SalesDocument): SalesTotals {
  const subtotal = document.items.reduce((sum, item) => sum + item.quantity * item.unitPrice, 0)
  const tax = Math.floor(Math.max(0, subtotal) * document.taxRate)
  return { subtotal, tax, total: subtotal + tax }
}

function formatYen(amount: number) {
  return `¥${new Intl.NumberFormat('ja-JP').format(Math.round(amount))}`
}

function SalesDocumentDialog({ form, onChange, onClose, onSubmit }: { form: typeof initialCreateForm; onChange: (form: typeof initialCreateForm) => void; onClose: () => void; onSubmit: (event: FormEvent<HTMLFormElement>) => void }) {
  return <div className="modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose() }}><section className="modal" role="dialog" aria-modal="true" aria-labelledby="sales-modal-title"><div className="modal-header"><h2 id="sales-modal-title">販売書類を作成</h2><button className="modal-close" type="button" aria-label="閉じる" onClick={onClose}><X size={19} /></button></div><form className="modal-form" onSubmit={onSubmit}><p className="modal-description"><FileText size={16} />顧客・車両を選択して、下書きの販売書類を作成します。</p><div className="form-grid"><label className="form-field"><span>書類種別<em>必須</em></span><select value={form.type} onChange={(event) => onChange({ ...form, type: event.target.value as SalesDocumentType })}><option>見積書</option><option>注文書</option><option>請求書</option></select></label><FormField label="顧客名" required><input autoFocus required value={form.customerName} onChange={(event) => onChange({ ...form, customerName: event.target.value })} /></FormField><FormField label="電話番号"><input value={form.phone} onChange={(event) => onChange({ ...form, phone: event.target.value })} /></FormField><FormField label="車名"><input value={form.vehicle} onChange={(event) => onChange({ ...form, vehicle: event.target.value })} /></FormField><FormField label="登録番号"><input value={form.plate} onChange={(event) => onChange({ ...form, plate: event.target.value })} /></FormField></div><div className="modal-footer"><button className="button button-secondary" type="button" onClick={onClose}>キャンセル</button><button className="button button-primary" type="submit"><Plus size={16} />作成する</button></div></form></section></div>
}

function FormField({ label, required, children }: { label: string; required?: boolean; children: ReactNode }) {
  return <label className="form-field"><span>{label}{required && <em>必須</em>}</span>{children}</label>
}
