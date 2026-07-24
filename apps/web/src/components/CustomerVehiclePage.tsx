import { useMemo, useState, type ChangeEvent, type FormEvent, type ReactNode } from 'react'
import type { LucideIcon } from 'lucide-react'
import {
  CarFront,
  ChevronRight,
  FileText,
  Image as ImageIcon,
  Mail,
  MapPin,
  Paperclip,
  Pencil,
  Phone,
  Plus,
  Search,
  Trash2,
  Upload,
  UserRound,
  X,
} from 'lucide-react'

type Attachment = {
  id: string
  name: string
  type: 'image' | 'pdf' | 'other'
  size: number
  createdAt: string
}

type Vehicle = {
  id: string
  maker: string
  model: string
  plate: string
  vin: string
  year: string
  inspectionDate: string
  mileage: string
  color: string
  displacement: string
  transmission: string
  note: string
  attachments: Attachment[]
}

type Customer = {
  id: string
  name: string
  kana: string
  phone: string
  email: string
  postalCode: string
  address: string
  memo: string
  vehicles: Vehicle[]
}

const initialCustomers: Customer[] = [
  {
    id: 'customer-sato',
    name: '佐藤 太郎',
    kana: 'さとう たろう',
    phone: '090-1234-5678',
    email: 'sato.taro@example.com',
    postalCode: '100-0001',
    address: '東京都千代田区千代田1-1',
    memo: '土曜午前の来店が多い。メールより電話を希望。',
    vehicles: [
      {
        id: 'vehicle-sato-prius', maker: 'トヨタ', model: 'プリウス', plate: '品川 500 あ 1234', vin: 'ZVW5000001', year: '2020年', inspectionDate: '2026/10/15', mileage: '68,420 km', color: 'パールホワイト', displacement: '1,800 cc', transmission: 'CVT', note: '左後ドア小傷あり。次回点検時に要確認。',
        attachments: [
          { id: 'attachment-sato-shaken', name: '車検証_プリウス.pdf', type: 'pdf', size: 384000, createdAt: '2026/07/21' },
          { id: 'attachment-sato-photo', name: 'prius_front.png', type: 'image', size: 1240000, createdAt: '2026/07/21' },
        ],
      },
      {
        id: 'vehicle-sato-hilux', maker: 'トヨタ', model: 'ハイラックス', plate: '品川 300 か 5678', vin: 'GUN1250002', year: '2022年', inspectionDate: '2027/04/08', mileage: '31,280 km', color: 'アティチュードブラック', displacement: '2,400 cc', transmission: '6AT', note: '休日利用。', attachments: [],
      },
    ],
  },
  {
    id: 'customer-tanaka', name: '田中 花子', kana: 'たなか はなこ', phone: '080-2345-6789', email: 'tanaka.hanako@example.com', postalCode: '231-0001', address: '神奈川県横浜市中区', memo: '',
    vehicles: [{ id: 'vehicle-tanaka-fit', maker: 'ホンダ', model: 'フィット', plate: '横浜 300 い 5678', vin: 'GK3000003', year: '2019年', inspectionDate: '2026/08/20', mileage: '42,100 km', color: 'ミッドナイトブルー', displacement: '1,300 cc', transmission: 'CVT', note: '', attachments: [] }],
  },
  {
    id: 'customer-suzuki', name: '鈴木 一郎', kana: 'すずき いちろう', phone: '070-3456-7890', email: 'suzuki.ichiro@example.com', postalCode: '330-0001', address: '埼玉県さいたま市大宮区', memo: '',
    vehicles: [{ id: 'vehicle-suzuki-note', maker: 'ニッサン', model: 'ノート', plate: '大宮 400 う 9012', vin: 'E1200004', year: '2018年', inspectionDate: '2025/12/01', mileage: '93,750 km', color: 'ブリリアントシルバー', displacement: '1,200 cc', transmission: 'CVT', note: '車検期限を超過。早急に案内。', attachments: [] }],
  },
  {
    id: 'customer-yamada', name: '山田 恵子', kana: 'やまだ けいこ', phone: '090-4567-8901', email: 'yamada.keiko@example.com', postalCode: '210-0001', address: '神奈川県川崎市川崎区', memo: '',
    vehicles: [{ id: 'vehicle-yamada-cx5', maker: 'マツダ', model: 'CX-5', plate: '川崎 501 お 7890', vin: 'KF2000005', year: '2021年', inspectionDate: '2027/03/31', mileage: '31,200 km', color: 'ソウルレッド', displacement: '2,000 cc', transmission: '6AT', note: '', attachments: [] }],
  },
]

const emptyCustomerForm = { name: '', kana: '', phone: '', email: '', address: '', memo: '' }
const emptyVehicleForm = { maker: '', model: '', plate: '', vin: '', year: '', inspectionDate: '', mileage: '', color: '' }

export function CustomerVehiclePage() {
  const [customers, setCustomers] = useState<Customer[]>(initialCustomers)
  const [query, setQuery] = useState('')
  const [selectedCustomerId, setSelectedCustomerId] = useState(initialCustomers[0].id)
  const [selectedVehicleId, setSelectedVehicleId] = useState(initialCustomers[0].vehicles[0].id)
  const [customerDialogOpen, setCustomerDialogOpen] = useState(false)
  const [vehicleDialogOpen, setVehicleDialogOpen] = useState(false)
  const [customerForm, setCustomerForm] = useState(emptyCustomerForm)
  const [vehicleForm, setVehicleForm] = useState(emptyVehicleForm)

  const filteredCustomers = useMemo(() => {
    const normalizedQuery = query.trim().toLocaleLowerCase()
    if (!normalizedQuery) return customers

    return customers.filter((customer) => {
      const customerText = `${customer.name} ${customer.kana} ${customer.phone} ${customer.address}`.toLocaleLowerCase()
      const vehicleText = customer.vehicles.map((vehicle) => `${vehicle.maker} ${vehicle.model} ${vehicle.plate} ${vehicle.vin}`).join(' ').toLocaleLowerCase()
      return `${customerText} ${vehicleText}`.includes(normalizedQuery)
    })
  }, [customers, query])

  const selectedCustomer = filteredCustomers.find((customer) => customer.id === selectedCustomerId) ?? filteredCustomers[0] ?? null
  const selectedVehicle = selectedCustomer?.vehicles.find((vehicle) => vehicle.id === selectedVehicleId) ?? selectedCustomer?.vehicles[0] ?? null
  const filteredVehicleCount = filteredCustomers.reduce((count, customer) => count + customer.vehicles.length, 0)

  function selectVehicle(customer: Customer, vehicle: Vehicle | null) {
    setSelectedCustomerId(customer.id)
    setSelectedVehicleId(vehicle?.id ?? '')
  }

  function selectCustomer(customer: Customer) {
    setSelectedCustomerId(customer.id)
    setSelectedVehicleId(customer.vehicles[0]?.id ?? '')
  }

  function handleCustomerSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!customerForm.name.trim()) return

    const newCustomer: Customer = {
      id: `customer-${Date.now()}`,
      name: customerForm.name.trim(),
      kana: customerForm.kana.trim(),
      phone: customerForm.phone.trim(),
      email: customerForm.email.trim(),
      postalCode: '',
      address: customerForm.address.trim(),
      memo: customerForm.memo.trim(),
      vehicles: [],
    }
    setCustomers((current) => [...current, newCustomer])
    setSelectedCustomerId(newCustomer.id)
    setSelectedVehicleId('')
    setCustomerForm(emptyCustomerForm)
    setCustomerDialogOpen(false)
  }

  function handleVehicleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!selectedCustomer || !vehicleForm.maker.trim() || !vehicleForm.model.trim()) return

    const newVehicle: Vehicle = {
      id: `vehicle-${Date.now()}`,
      maker: vehicleForm.maker.trim(), model: vehicleForm.model.trim(), plate: vehicleForm.plate.trim(), vin: vehicleForm.vin.trim(), year: vehicleForm.year.trim(), inspectionDate: vehicleForm.inspectionDate.trim(), mileage: vehicleForm.mileage.trim(), color: vehicleForm.color.trim(), displacement: '', transmission: '', note: '', attachments: [],
    }
    setCustomers((current) => current.map((customer) => customer.id === selectedCustomer.id ? { ...customer, vehicles: [...customer.vehicles, newVehicle] } : customer))
    setSelectedVehicleId(newVehicle.id)
    setVehicleForm(emptyVehicleForm)
    setVehicleDialogOpen(false)
  }

  function handleAttachments(event: ChangeEvent<HTMLInputElement>, vehicleId: string) {
    const files = Array.from(event.target.files ?? [])
    if (!files.length) return
    const addedAttachments: Attachment[] = files.map((file, index) => ({
      id: `attachment-${Date.now()}-${index}`,
      name: file.name,
      type: file.type === 'application/pdf' ? 'pdf' : file.type.startsWith('image/') ? 'image' : 'other',
      size: file.size,
      createdAt: new Date().toLocaleDateString('ja-JP'),
    }))
    setCustomers((current) => current.map((customer) => ({ ...customer, vehicles: customer.vehicles.map((vehicle) => vehicle.id === vehicleId ? { ...vehicle, attachments: [...vehicle.attachments, ...addedAttachments] } : vehicle) })))
    event.target.value = ''
  }

  function removeAttachment(vehicleId: string, attachmentId: string) {
    setCustomers((current) => current.map((customer) => ({ ...customer, vehicles: customer.vehicles.map((vehicle) => vehicle.id === vehicleId ? { ...vehicle, attachments: vehicle.attachments.filter((attachment) => attachment.id !== attachmentId) } : vehicle) })))
  }

  return (
    <>
      <div className="page-header customer-page-header">
        <div><span className="page-eyebrow">顧客・車両</span><h1>顧客・車両</h1><p>顧客情報と、顧客に紐づく複数の車両を管理します。</p></div>
        <button className="button button-primary" type="button" onClick={() => setCustomerDialogOpen(true)}><Plus size={18} />顧客を登録</button>
      </div>

      <div className="customer-toolbar">
        <label className="customer-search"><Search size={19} /><span className="sr-only">顧客・車両を検索</span><input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="顧客名、電話番号、車名、登録番号で検索" /></label>
        <span className="customer-result-summary"><strong>{filteredVehicleCount}台</strong><span>{filteredCustomers.length}名の顧客</span></span>
      </div>

      <div className="customer-directory">
        <CustomerList customers={filteredCustomers} selectedCustomerId={selectedCustomer?.id ?? ''} onSelect={selectCustomer} />
        <CustomerProfile customer={selectedCustomer} vehicle={selectedVehicle} onSelectVehicle={(vehicle) => selectedCustomer && selectVehicle(selectedCustomer, vehicle)} onAddVehicle={() => setVehicleDialogOpen(true)} onAttachments={handleAttachments} onRemoveAttachment={removeAttachment} />
      </div>

      {customerDialogOpen && <CustomerDialog form={customerForm} onChange={setCustomerForm} onClose={() => { setCustomerDialogOpen(false); setCustomerForm(emptyCustomerForm) }} onSubmit={handleCustomerSubmit} />}
      {vehicleDialogOpen && selectedCustomer && <VehicleDialog form={vehicleForm} customerName={selectedCustomer.name} onChange={setVehicleForm} onClose={() => { setVehicleDialogOpen(false); setVehicleForm(emptyVehicleForm) }} onSubmit={handleVehicleSubmit} />}
    </>
  )
}

function CustomerList({ customers, selectedCustomerId, onSelect }: { customers: Customer[]; selectedCustomerId: string; onSelect: (customer: Customer) => void }) {
  return <section className="panel customer-list-panel"><div className="customer-list-header"><div><h2>顧客一覧</h2><span>顧客を選択すると詳細を表示します</span></div><span className="results-count">{customers.length}名</span></div><div className="customer-list">{customers.map((customer) => <button className={`customer-list-item${customer.id === selectedCustomerId ? ' is-selected' : ''}`} key={customer.id} type="button" onClick={() => onSelect(customer)}><span className="customer-list-avatar"><UserRound size={19} /></span><span className="customer-list-copy"><strong>{customer.name}</strong><small>{customer.phone}</small><em>{customer.vehicles.length}台所有</em></span><ChevronRight size={17} className="customer-list-chevron" /></button>)}{!customers.length && <div className="empty-state"><Search size={24} /><strong>顧客が見つかりません</strong><span>検索条件を変更してください。</span></div>}</div></section>
}

function CustomerProfile({ customer, vehicle, onSelectVehicle, onAddVehicle, onAttachments, onRemoveAttachment }: { customer: Customer | null; vehicle: Vehicle | null; onSelectVehicle: (vehicle: Vehicle) => void; onAddVehicle: () => void; onAttachments: (event: ChangeEvent<HTMLInputElement>, vehicleId: string) => void; onRemoveAttachment: (vehicleId: string, attachmentId: string) => void }) {
  if (!customer) return <section className="panel customer-profile-empty"><UserRound size={30} /><strong>顧客を登録してください</strong><span>登録した顧客の情報がここに表示されます。</span></section>

  return <section className="customer-profile"><section className="panel customer-info-panel"><div className="customer-profile-header"><div className="customer-identity"><span className="customer-profile-avatar"><UserRound size={28} /></span><span><h2>{customer.name}</h2><small>{customer.kana || 'ふりがな未登録'}</small></span></div><button className="button button-secondary" type="button"><Pencil size={17} />顧客情報を編集</button></div><div className="customer-info-grid"><InfoItem icon={Phone} label="電話番号" value={customer.phone || '未登録'} /><InfoItem icon={Mail} label="メールアドレス" value={customer.email || '未登録'} /><InfoItem icon={MapPin} label="住所" value={customer.address || '未登録'} /></div>{customer.memo && <div className="customer-memo"><span>メモ</span><p>{customer.memo}</p></div>}</section><section className="owned-vehicles-section"><div className="owned-vehicles-header"><div><h2>所有車両 <small>{customer.vehicles.length}台</small></h2><span>車両を選択すると詳細と添付ファイルが切り替わります</span></div><button className="button button-primary" type="button" onClick={onAddVehicle}><Plus size={17} />車両を追加</button></div>{customer.vehicles.length ? <div className="vehicle-choice-grid">{customer.vehicles.map((item) => <button className={`vehicle-choice-card${item.id === vehicle?.id ? ' is-selected' : ''}`} key={item.id} type="button" onClick={() => onSelectVehicle(item)}><span className="vehicle-choice-name"><span className={`vehicle-status-dot ${item.inspectionDate.startsWith('2025') ? 'is-danger' : item.inspectionDate.startsWith('2026/08') ? 'is-warning' : ''}`} /><strong>{item.maker} {item.model}</strong></span><span className="vehicle-choice-plate">{item.plate || '登録番号未登録'}</span><span className="vehicle-choice-footer"><span>{item.year || '年式未登録'}</span><span>{item.attachments.length}件の添付</span></span></button>)}</div> : <div className="owned-vehicles-empty"><CarFront size={23} /><strong>所有車両が登録されていません</strong><span>この顧客に最初の車両を追加してください。</span><button className="button button-primary" type="button" onClick={onAddVehicle}><Plus size={17} />車両を追加</button></div>}</section>{vehicle && <div className="selected-vehicle-grid"><VehicleSummary vehicle={vehicle} /><section className="panel attachments-panel"><AttachmentSection vehicle={vehicle} onAttachments={onAttachments} onRemoveAttachment={onRemoveAttachment} /></section></div>}</section>
}

function InfoItem({ icon: Icon, label, value }: { icon: LucideIcon; label: string; value: string }) {
  return <div className="customer-info-item"><span className="customer-info-label"><Icon size={16} />{label}</span><strong>{value}</strong></div>
}

function VehicleSummary({ vehicle }: { vehicle: Vehicle }) {
  return <section className="panel vehicle-summary-panel"><div className="vehicle-summary-header"><div><span>選択中の車両</span><h2>{vehicle.maker} {vehicle.model}</h2><small>{vehicle.plate || '登録番号未登録'}</small></div><button className="detail-action-button" type="button"><Pencil size={15} />編集</button></div><div className="vehicle-summary-body"><div className="detail-fields"><DetailField label="車検満了日" value={vehicle.inspectionDate || '未登録'} /><DetailField label="車台番号" value={vehicle.vin || '未登録'} /><DetailField label="型式・年式" value={`${vehicle.maker} ・ ${vehicle.year || '未登録'}`} /><DetailField label="車体色" value={vehicle.color || '未登録'} /><DetailField label="走行距離" value={vehicle.mileage || '未登録'} /><DetailField label="ミッション" value={vehicle.transmission || '未登録'} /></div>{vehicle.note && <div className="vehicle-note"><span>備考</span><p>{vehicle.note}</p></div>}</div></section>
}

function DetailField({ label, value }: { label: string; value: string }) {
  return <div className="detail-field"><span>{label}</span><strong>{value}</strong></div>
}

function AttachmentSection({ vehicle, onAttachments, onRemoveAttachment }: { vehicle: Vehicle; onAttachments: (event: ChangeEvent<HTMLInputElement>, vehicleId: string) => void; onRemoveAttachment: (vehicleId: string, attachmentId: string) => void }) {
  return <section className="attachments-section"><div className="attachments-header"><div><h3>添付ファイル</h3><span>写真・車検証PDFなどを車両ごとに保存</span></div><label className="attachment-add-button"><Upload size={16} />追加<input className="hidden-input" type="file" accept=".jpg,.jpeg,.png,.pdf,image/jpeg,image/png,application/pdf" multiple onChange={(event) => onAttachments(event, vehicle.id)} /></label></div>{vehicle.attachments.length ? <div className="attachments-grid">{vehicle.attachments.map((attachment) => <div className="attachment-card" key={attachment.id}><span className={`attachment-icon attachment-icon-${attachment.type}`}>{attachment.type === 'image' ? <ImageIcon size={19} /> : <FileText size={19} />}</span><span className="attachment-card-copy"><strong title={attachment.name}>{attachment.name}</strong><small>{formatFileSize(attachment.size)} ・ {attachment.createdAt}</small></span><button className="attachment-remove" type="button" aria-label={`${attachment.name}を削除`} onClick={() => onRemoveAttachment(vehicle.id, attachment.id)}><Trash2 size={15} /></button></div>)}</div> : <label className="attachment-dropzone"><Paperclip size={21} /><strong>ファイルを追加</strong><span>JPEG・PNG・PDFに対応</span><input className="hidden-input" type="file" accept=".jpg,.jpeg,.png,.pdf,image/jpeg,image/png,application/pdf" multiple onChange={(event) => onAttachments(event, vehicle.id)} /></label>}</section>
}

function CustomerDialog({ form, onChange, onClose, onSubmit }: { form: typeof emptyCustomerForm; onChange: (form: typeof emptyCustomerForm) => void; onClose: () => void; onSubmit: (event: FormEvent<HTMLFormElement>) => void }) {
  return <Modal title="顧客を登録" onClose={onClose}><form className="modal-form" onSubmit={onSubmit}><div className="form-grid"><FormField label="顧客名" required><input autoFocus required value={form.name} onChange={(event) => onChange({ ...form, name: event.target.value })} placeholder="例：佐藤 太郎" /></FormField><FormField label="ふりがな"><input value={form.kana} onChange={(event) => onChange({ ...form, kana: event.target.value })} placeholder="例：さとう たろう" /></FormField><FormField label="電話番号"><input type="tel" value={form.phone} onChange={(event) => onChange({ ...form, phone: event.target.value })} placeholder="例：090-1234-5678" /></FormField><FormField label="メールアドレス"><input type="email" value={form.email} onChange={(event) => onChange({ ...form, email: event.target.value })} placeholder="例：sato@example.com" /></FormField><FormField label="住所"><input value={form.address} onChange={(event) => onChange({ ...form, address: event.target.value })} placeholder="例：東京都千代田区" /></FormField><FormField label="メモ"><textarea value={form.memo} onChange={(event) => onChange({ ...form, memo: event.target.value })} placeholder="連絡方法など" /></FormField></div><ModalFooter onClose={onClose} submitLabel="顧客を登録" /></form></Modal>
}

function VehicleDialog({ form, customerName, onChange, onClose, onSubmit }: { form: typeof emptyVehicleForm; customerName: string; onChange: (form: typeof emptyVehicleForm) => void; onClose: () => void; onSubmit: (event: FormEvent<HTMLFormElement>) => void }) {
  return <Modal title="車両を追加" onClose={onClose}><form className="modal-form" onSubmit={onSubmit}><p className="modal-description"><UserRound size={16} />{customerName} に車両を追加します。</p><div className="form-grid"><FormField label="メーカー" required><input autoFocus required value={form.maker} onChange={(event) => onChange({ ...form, maker: event.target.value })} placeholder="例：トヨタ" /></FormField><FormField label="車名" required><input required value={form.model} onChange={(event) => onChange({ ...form, model: event.target.value })} placeholder="例：プリウス" /></FormField><FormField label="登録番号"><input value={form.plate} onChange={(event) => onChange({ ...form, plate: event.target.value })} placeholder="例：品川 500 あ 1234" /></FormField><FormField label="車台番号"><input value={form.vin} onChange={(event) => onChange({ ...form, vin: event.target.value })} placeholder="例：ZVW5000001" /></FormField><FormField label="年式"><input value={form.year} onChange={(event) => onChange({ ...form, year: event.target.value })} placeholder="例：2024年" /></FormField><FormField label="車検満了日"><input type="date" value={form.inspectionDate.replaceAll('/', '-')} onChange={(event) => onChange({ ...form, inspectionDate: event.target.value.replaceAll('-', '/') })} /></FormField><FormField label="走行距離"><input value={form.mileage} onChange={(event) => onChange({ ...form, mileage: event.target.value })} placeholder="例：12,500 km" /></FormField><FormField label="車体色"><input value={form.color} onChange={(event) => onChange({ ...form, color: event.target.value })} placeholder="例：パールホワイト" /></FormField></div><ModalFooter onClose={onClose} submitLabel="車両を追加" /></form></Modal>
}

function FormField({ label, required, children }: { label: string; required?: boolean; children: ReactNode }) {
  return <label className="form-field"><span>{label}{required && <em>必須</em>}</span>{children}</label>
}

function Modal({ title, onClose, children }: { title: string; onClose: () => void; children: ReactNode }) {
  return <div className="modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose() }}><section className="modal" role="dialog" aria-modal="true" aria-labelledby="modal-title"><div className="modal-header"><h2 id="modal-title">{title}</h2><button className="modal-close" type="button" aria-label="閉じる" onClick={onClose}><X size={19} /></button></div>{children}</section></div>
}

function ModalFooter({ onClose, submitLabel }: { onClose: () => void; submitLabel: string }) {
  return <div className="modal-footer"><button className="button button-secondary" type="button" onClick={onClose}>キャンセル</button><button className="button button-primary" type="submit">{submitLabel}</button></div>
}

function formatFileSize(bytes: number) {
  if (bytes < 1024 * 1024) return `${Math.max(1, Math.round(bytes / 1024))} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}
