import { useEffect, useMemo, useState, type ChangeEvent, type DragEvent, type FormEvent, type ReactNode } from 'react'
import type { LucideIcon } from 'lucide-react'
import {
  CarFront,
  ChevronRight,
  Download,
  Eye,
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
  UserRound,
  X,
} from 'lucide-react'
import {
  createCustomer,
  createVehicle,
  deleteVehicleFile,
  fetchVehicleFile,
  fetchCustomers,
  fetchVehicleHistory,
  type Customer,
  type CustomerInput,
  type Attachment,
  type Vehicle,
  type VehicleInput,
  type VehicleHistory,
  updateCustomer,
  updateVehicle,
  uploadVehicleFile,
} from '../lib/customerApi'

const emptyCustomerForm: CustomerInput = { name: '', kana: '', phone: '', email: '', postalCode: '', address: '', memo: '' }
const emptyVehicleForm: VehicleInput = { maker: '', model: '', modelType: '', plate: '', vin: '', year: '', inspectionDate: '', mileage: '', color: '', displacement: '', transmission: '', note: '', freeItem1: '', freeItem2: '', freeItem3: '' }
const customerSearchFields = ['すべて', '顧客名', 'ふりがな', 'メールアドレス', '電話番号', '住所', '車名', '登録番号', '車台番号'] as const
type CustomerSearchField = (typeof customerSearchFields)[number]
const customerSearchPlaceholders: Record<CustomerSearchField, string> = {
  すべて: '顧客名、ふりがな、メールアドレス、電話番号、住所、車名、登録番号、車台番号で検索',
  顧客名: '顧客名で検索',
  ふりがな: 'ふりがなで検索',
  メールアドレス: 'メールアドレスで検索',
  電話番号: '電話番号で検索',
  住所: '住所で検索',
  車名: '車名で検索',
  登録番号: '登録番号で検索',
  車台番号: '車台番号で検索',
}

function getCustomerSearchText(customer: Customer, field: CustomerSearchField) {
  const values = {
    顧客名: customer.name,
    ふりがな: customer.kana,
    メールアドレス: customer.email,
    電話番号: customer.phone,
    住所: `${customer.postalCode} ${customer.address}`,
    車名: customer.vehicles.map((vehicle) => `${vehicle.maker} ${vehicle.model}`).join(' '),
    登録番号: customer.vehicles.map((vehicle) => vehicle.plate).join(' '),
    車台番号: customer.vehicles.map((vehicle) => vehicle.vin).join(' '),
  }
  return field === 'すべて' ? Object.values(values).join(' ') : values[field]
}

export function CustomerVehiclePage() {
  const [customers, setCustomers] = useState<Customer[]>([])
  const [query, setQuery] = useState('')
  const [searchField, setSearchField] = useState<CustomerSearchField>('すべて')
  const [selectedCustomerId, setSelectedCustomerId] = useState('')
  const [selectedVehicleId, setSelectedVehicleId] = useState('')
  const [customerDialogOpen, setCustomerDialogOpen] = useState(false)
  const [vehicleDialogOpen, setVehicleDialogOpen] = useState(false)
  const [editingCustomerId, setEditingCustomerId] = useState<string | null>(null)
  const [editingVehicleId, setEditingVehicleId] = useState<string | null>(null)
  const [customerForm, setCustomerForm] = useState<CustomerInput>(emptyCustomerForm)
  const [vehicleForm, setVehicleForm] = useState<VehicleInput>(emptyVehicleForm)
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState('')

  useEffect(() => {
    let active = true
    setLoading(true)
    void fetchCustomers().then((nextCustomers) => {
      if (!active) return
      setCustomers(nextCustomers)
      setError('')
    }).catch((reason: unknown) => {
      if (active) setError(getErrorMessage(reason))
    }).finally(() => {
      if (active) setLoading(false)
    })
    return () => { active = false }
  }, [])

  const filteredCustomers = useMemo(() => {
    const normalizedQuery = query.trim().toLocaleLowerCase()
    if (!normalizedQuery) return customers
    return customers.filter((customer) => getCustomerSearchText(customer, searchField).toLocaleLowerCase().includes(normalizedQuery))
  }, [customers, query, searchField])

  const selectedCustomer = filteredCustomers.find((customer) => customer.id === selectedCustomerId) ?? filteredCustomers[0] ?? null
  const selectedVehicle = selectedCustomer?.vehicles.find((vehicle) => vehicle.id === selectedVehicleId) ?? selectedCustomer?.vehicles[0] ?? null

  function selectCustomer(customer: Customer) {
    setSelectedCustomerId(customer.id)
    setSelectedVehicleId(customer.vehicles[0]?.id ?? '')
  }

  function selectVehicle(customer: Customer, vehicle: Vehicle) {
    setSelectedCustomerId(customer.id)
    setSelectedVehicleId(vehicle.id)
  }

  function openNewCustomerDialog() {
    setEditingCustomerId(null)
    setCustomerForm(emptyCustomerForm)
    setCustomerDialogOpen(true)
  }

  function openEditCustomerDialog(customer: Customer) {
    setEditingCustomerId(customer.id)
    setCustomerForm({ name: customer.name, kana: customer.kana, phone: customer.phone, email: customer.email, postalCode: customer.postalCode, address: customer.address, memo: customer.memo })
    setCustomerDialogOpen(true)
  }

  function closeCustomerDialog() {
    setCustomerDialogOpen(false)
    setEditingCustomerId(null)
    setCustomerForm(emptyCustomerForm)
  }

  async function handleCustomerSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!customerForm.name.trim()) return
    setSaving(true)
    setError('')
    try {
      const savedCustomer = editingCustomerId ? await updateCustomer(editingCustomerId, customerForm) : await createCustomer(customerForm)
      setCustomers((current) => editingCustomerId ? current.map((customer) => customer.id === savedCustomer.id ? savedCustomer : customer) : [...current, savedCustomer])
      setSelectedCustomerId(savedCustomer.id)
      setSelectedVehicleId(savedCustomer.vehicles[0]?.id ?? '')
      closeCustomerDialog()
    } catch (reason: unknown) {
      setError(getErrorMessage(reason))
    } finally {
      setSaving(false)
    }
  }

  function openNewVehicleDialog() {
    setEditingVehicleId(null)
    setVehicleForm(emptyVehicleForm)
    setVehicleDialogOpen(true)
  }

  function openEditVehicleDialog(vehicle: Vehicle) {
    setEditingVehicleId(vehicle.id)
    setVehicleForm({ maker: vehicle.maker, model: vehicle.model, modelType: vehicle.modelType, plate: vehicle.plate, vin: vehicle.vin, year: vehicle.year, inspectionDate: vehicle.inspectionDate, mileage: vehicle.mileage, color: vehicle.color, displacement: vehicle.displacement, transmission: vehicle.transmission, note: vehicle.note, freeItem1: vehicle.freeItem1, freeItem2: vehicle.freeItem2, freeItem3: vehicle.freeItem3 })
    setVehicleDialogOpen(true)
  }

  function closeVehicleDialog() {
    setVehicleDialogOpen(false)
    setEditingVehicleId(null)
    setVehicleForm(emptyVehicleForm)
  }

  async function handleVehicleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!selectedCustomer || !vehicleForm.maker.trim() || !vehicleForm.model.trim()) return
    setSaving(true)
    setError('')
    try {
      if (editingVehicleId) {
        await updateVehicle(editingVehicleId, vehicleForm)
        setCustomers((current) => current.map((customer) => customer.id !== selectedCustomer.id ? customer : { ...customer, vehicles: customer.vehicles.map((vehicle) => vehicle.id === editingVehicleId ? { ...vehicle, ...vehicleForm, attachments: vehicle.attachments } : vehicle) }))
        setSelectedVehicleId(editingVehicleId)
      } else {
        const result = await createVehicle(selectedCustomer.id, vehicleForm)
        setCustomers((current) => current.map((customer) => customer.id === result.customer.id ? result.customer : customer))
        setSelectedCustomerId(result.customer.id)
        setSelectedVehicleId(result.vehicleId)
      }
      closeVehicleDialog()
    } catch (reason: unknown) {
      setError(getErrorMessage(reason))
    } finally {
      setSaving(false)
    }
  }

  async function handleAttachmentFiles(files: File[], vehicleId: string) {
    if (!files.length) return
    setSaving(true)
    setError('')
    try {
      for (const file of files) {
        const attachment = await uploadVehicleFile(vehicleId, file)
        setCustomers((current) => current.map((customer) => ({ ...customer, vehicles: customer.vehicles.map((vehicle) => vehicle.id === vehicleId ? { ...vehicle, attachments: [...vehicle.attachments, attachment] } : vehicle) })))
      }
    } catch (reason: unknown) {
      setError(getErrorMessage(reason))
    } finally {
      setSaving(false)
    }
  }

  function handleAttachments(event: ChangeEvent<HTMLInputElement>, vehicleId: string) {
    const files = Array.from(event.target.files ?? [])
    event.target.value = ''
    void handleAttachmentFiles(files, vehicleId)
  }

  function handleAttachmentDrop(event: DragEvent<HTMLLabelElement>, vehicleId: string) {
    event.preventDefault()
    event.currentTarget.classList.remove('is-dragging')
    void handleAttachmentFiles(Array.from(event.dataTransfer.files), vehicleId)
  }

  async function removeAttachment(vehicleId: string, attachmentId: string) {
    setSaving(true)
    setError('')
    try {
      await deleteVehicleFile(vehicleId, attachmentId)
      setCustomers((current) => current.map((customer) => ({ ...customer, vehicles: customer.vehicles.map((vehicle) => vehicle.id === vehicleId ? { ...vehicle, attachments: vehicle.attachments.filter((attachment) => attachment.id !== attachmentId) } : vehicle) })))
    } catch (reason: unknown) {
      setError(getErrorMessage(reason))
    } finally {
      setSaving(false)
    }
  }

  async function openAttachment(vehicleId: string, attachment: Attachment, mode: 'preview' | 'download') {
    const previewWindow = mode === 'preview' ? window.open('', '_blank') : null
    if (previewWindow) previewWindow.opener = null
    setSaving(true)
    setError('')
    try {
      const blob = await fetchVehicleFile(vehicleId, attachment.id)
      const url = URL.createObjectURL(blob)
      if (mode === 'preview') {
        if (!previewWindow) throw new Error('プレビュー画面を開けませんでした。ポップアップを許可してください。')
        previewWindow.location.href = url
      } else {
        const link = document.createElement('a')
        link.href = url
        link.download = attachment.name
        document.body.appendChild(link)
        link.click()
        link.remove()
      }
      window.setTimeout(() => URL.revokeObjectURL(url), 60_000)
    } catch (reason: unknown) {
      previewWindow?.close()
      setError(getErrorMessage(reason))
    } finally {
      setSaving(false)
    }
  }

  return (
    <>
      <div className="page-header customer-page-header">
        <div><span className="page-eyebrow">顧客・車両</span><h1>顧客・車両</h1><p>顧客情報と、顧客に紐づく複数の車両を管理します。</p></div>
        <button className="button button-primary" type="button" onClick={openNewCustomerDialog}><Plus size={18} />顧客を登録</button>
      </div>

      {(loading || error || saving) && <div className={`customer-sync-status${error ? ' is-error' : ''}`} role={error ? 'alert' : 'status'}><span>{loading ? '顧客・車両データを読み込んでいます…' : saving ? '変更を保存しています…' : error}</span>{error && <button className="text-button" type="button" onClick={() => window.location.reload()}>再読み込み</button>}</div>}

      <div className="customer-toolbar">
        <label className="customer-search"><Search size={19} /><span className="sr-only">顧客・車両を検索</span><input value={query} onChange={(event) => setQuery(event.target.value)} placeholder={customerSearchPlaceholders[searchField]} /></label>
        <label className="customer-search-filter"><span className="sr-only">検索項目</span><select value={searchField} onChange={(event) => setSearchField(event.target.value as CustomerSearchField)}>{customerSearchFields.map((field) => <option key={field} value={field}>{field}</option>)}</select></label>
      </div>

      <div className="customer-directory">
        <CustomerList customers={filteredCustomers} selectedCustomerId={selectedCustomer?.id ?? ''} onSelect={selectCustomer} />
        <CustomerProfile customer={selectedCustomer} vehicle={selectedVehicle} onSelectVehicle={(vehicle) => selectedCustomer && selectVehicle(selectedCustomer, vehicle)} onAddVehicle={openNewVehicleDialog} onEditCustomer={openEditCustomerDialog} onEditVehicle={openEditVehicleDialog} onAttachments={handleAttachments} onAttachmentDrop={handleAttachmentDrop} onPreviewAttachment={openAttachment} onRemoveAttachment={removeAttachment} />
      </div>

      {customerDialogOpen && <CustomerDialog form={customerForm} title={editingCustomerId ? '顧客情報を編集' : '顧客を登録'} submitLabel={editingCustomerId ? '変更を保存' : '顧客を登録'} onChange={setCustomerForm} onClose={closeCustomerDialog} onSubmit={handleCustomerSubmit} />}
      {vehicleDialogOpen && selectedCustomer && <VehicleDialog form={vehicleForm} title={editingVehicleId ? '車両情報を編集' : '車両を追加'} submitLabel={editingVehicleId ? '変更を保存' : '車両を追加'} customerName={selectedCustomer.name} onChange={setVehicleForm} onClose={closeVehicleDialog} onSubmit={handleVehicleSubmit} />}
    </>
  )
}

function CustomerList({ customers, selectedCustomerId, onSelect }: { customers: Customer[]; selectedCustomerId: string; onSelect: (customer: Customer) => void }) {
  return <section className="panel customer-list-panel"><div className="customer-list-header"><div><h2>顧客一覧</h2><span>顧客を選択すると詳細を表示します</span></div></div><div className="customer-list">{customers.map((customer) => <button className={`customer-list-item${customer.id === selectedCustomerId ? ' is-selected' : ''}`} key={customer.id} type="button" onClick={() => onSelect(customer)}><span className="customer-list-avatar"><UserRound size={19} /></span><span className="customer-list-copy"><strong>{customer.name}</strong><small>{customer.phone || '電話番号未登録'}</small></span><ChevronRight size={17} className="customer-list-chevron" /></button>)}{!customers.length && <div className="empty-state"><Search size={24} /><strong>顧客が見つかりません</strong><span>顧客を登録するか、検索条件を変更してください。</span></div>}</div></section>
}

function CustomerProfile({ customer, vehicle, onSelectVehicle, onAddVehicle, onEditCustomer, onEditVehicle, onAttachments, onAttachmentDrop, onPreviewAttachment, onRemoveAttachment }: { customer: Customer | null; vehicle: Vehicle | null; onSelectVehicle: (vehicle: Vehicle) => void; onAddVehicle: () => void; onEditCustomer: (customer: Customer) => void; onEditVehicle: (vehicle: Vehicle) => void; onAttachments: (event: ChangeEvent<HTMLInputElement>, vehicleId: string) => void; onAttachmentDrop: (event: DragEvent<HTMLLabelElement>, vehicleId: string) => void; onPreviewAttachment: (vehicleId: string, attachment: Attachment, mode: 'preview' | 'download') => void; onRemoveAttachment: (vehicleId: string, attachmentId: string) => void }) {
  if (!customer) return <section className="panel customer-profile-empty"><UserRound size={30} /><strong>顧客を登録してください</strong><span>登録した顧客の情報がここに表示されます。</span></section>

  return <section className="customer-profile"><section className="panel customer-info-panel"><div className="customer-profile-header"><div className="customer-identity"><span className="customer-profile-avatar"><UserRound size={28} /></span><span><h2>{customer.name}</h2><small>{customer.kana || 'ふりがな未登録'}</small></span></div><button className="button button-secondary" type="button" onClick={() => onEditCustomer(customer)}><Pencil size={17} />顧客情報を編集</button></div><div className="customer-info-grid"><InfoItem icon={Phone} label="電話番号" value={customer.phone || '未登録'} /><InfoItem icon={Mail} label="メールアドレス" value={customer.email || '未登録'} /><InfoItem icon={MapPin} label="住所" value={customer.address || '未登録'} /></div>{customer.memo && <div className="customer-memo"><span>メモ</span><p>{customer.memo}</p></div>}</section><section className="owned-vehicles-section"><div className="owned-vehicles-header"><div><h2>所有車両</h2><span>車両を選択すると詳細と添付ファイルが切り替わります</span></div><button className="button button-primary" type="button" onClick={onAddVehicle}><Plus size={17} />車両を追加</button></div>{customer.vehicles.length ? <div className="vehicle-choice-grid">{customer.vehicles.map((item) => <button className={`vehicle-choice-card${item.id === vehicle?.id ? ' is-selected' : ''}`} key={item.id} type="button" onClick={() => onSelectVehicle(item)}><span className="vehicle-choice-name"><span className={`vehicle-status-dot ${item.inspectionDate.startsWith('2025') ? 'is-danger' : item.inspectionDate.startsWith('2026/08') ? 'is-warning' : ''}`} /><strong>{item.maker} {item.model}</strong></span><span className="vehicle-choice-plate">{item.plate || '登録番号未登録'}</span><span className="vehicle-choice-footer"><span>{item.year || '年式未登録'}</span><span>{item.attachments.length}件の添付</span></span></button>)}</div> : <div className="owned-vehicles-empty"><CarFront size={23} /><strong>所有車両が登録されていません</strong><span>この顧客に最初の車両を追加してください。</span><button className="button button-primary" type="button" onClick={onAddVehicle}><Plus size={17} />車両を追加</button></div>}</section>{vehicle && <><div className="selected-vehicle-grid"><VehicleSummary vehicle={vehicle} onEdit={onEditVehicle} /><section className="panel attachments-panel"><AttachmentSection vehicle={vehicle} onAttachments={onAttachments} onAttachmentDrop={onAttachmentDrop} onPreviewAttachment={onPreviewAttachment} onRemoveAttachment={onRemoveAttachment} /></section></div><VehicleHistoryPanel vehicleId={vehicle.id} /></>}</section>
}

function InfoItem({ icon: Icon, label, value }: { icon: LucideIcon; label: string; value: string }) {
  return <div className="customer-info-item"><span className="customer-info-label"><Icon size={16} />{label}</span><strong>{value}</strong></div>
}

function VehicleSummary({ vehicle, onEdit }: { vehicle: Vehicle; onEdit: (vehicle: Vehicle) => void }) {
  return <section className="panel vehicle-summary-panel"><div className="vehicle-summary-header"><div><span>選択中の車両</span><h2>{vehicle.maker} {vehicle.model}</h2><small>{vehicle.plate || '登録番号未登録'}</small></div><button className="detail-action-button" type="button" onClick={() => onEdit(vehicle)}><Pencil size={15} />編集</button></div><div className="vehicle-summary-body"><div className="detail-fields"><DetailField label="車検満了日" value={vehicle.inspectionDate || '未登録'} /><DetailField label="車台番号" value={vehicle.vin || '未登録'} /><DetailField label="型式・年式" value={[vehicle.modelType, vehicle.year].filter(Boolean).join(' ・ ') || '未登録'} /><DetailField label="車体色" value={vehicle.color || '未登録'} /><DetailField label="走行距離" value={vehicle.mileage || '未登録'} /><DetailField label="排気量" value={vehicle.displacement || '未登録'} /><DetailField label="ミッション" value={vehicle.transmission || '未登録'} /></div>{[vehicle.freeItem1, vehicle.freeItem2, vehicle.freeItem3].some(Boolean) && <div className="vehicle-free-items">{[vehicle.freeItem1, vehicle.freeItem2, vehicle.freeItem3].filter(Boolean).map((item, index) => <DetailField key={`${item}-${index}`} label={`自由項目${index + 1}`} value={item} />)}</div>}{vehicle.note && <div className="vehicle-note"><span>備考</span><p>{vehicle.note}</p></div>}</div></section>
}

function VehicleHistoryPanel({ vehicleId }: { vehicleId: string }) {
  const [history, setHistory] = useState<VehicleHistory | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')

  useEffect(() => {
    let active = true
    setLoading(true)
    setError('')
    void fetchVehicleHistory(vehicleId).then((nextHistory) => {
      if (active) setHistory(nextHistory)
    }).catch((reason: unknown) => {
      if (active) setError(reason instanceof Error ? reason.message : '車両履歴を読み込めませんでした。')
    }).finally(() => {
      if (active) setLoading(false)
    })
    return () => { active = false }
  }, [vehicleId])

  return <section className="panel vehicle-history-panel"><div className="vehicle-history-header"><div><span className="page-eyebrow">VEHICLE HISTORY</span><h3>車両履歴</h3><p>販売・整備・点検・入金・添付ファイルを車両単位で確認できます。</p></div><FileText size={20} /></div>{loading && <div className="vehicle-history-empty">履歴を読み込んでいます…</div>}{error && <div className="vehicle-history-empty is-error" role="alert">{error}</div>}{!loading && !error && history && <div className="vehicle-history-grid"><HistoryGroup title="販売履歴" count={history.sales.length}>{history.sales.map((row) => <HistoryRow key={row.id} primary={`${row.type} ${row.number}`} secondary={`${formatHistoryDate(row.issuedAt)} ・ ${row.status}`} amount={row.total} />)}</HistoryGroup><HistoryGroup title="整備履歴" count={history.maintenance.length}>{history.maintenance.map((row) => <HistoryRow key={row.id} primary={`${row.category} ${row.number}`} secondary={`${formatHistoryDate(row.issuedAt)} ・ ${row.status}`} amount={row.total} />)}</HistoryGroup><HistoryGroup title="車検・点検履歴" count={history.inspections.length}>{history.inspections.map((row) => <HistoryRow key={row.id} primary={row.inspectionType} secondary={`${formatHistoryDate(row.dueDate)} ・ ${row.status}`} />)}</HistoryGroup><HistoryGroup title="入金履歴" count={history.payments.length}>{history.payments.map((row) => <HistoryRow key={row.id} primary={`${row.documentType} ${row.documentNumber}`} secondary={`${formatHistoryDate(row.paymentDate)} ・ ${row.method || '方法未登録'}`} amount={row.paidAmount} />)}</HistoryGroup><HistoryGroup title="添付ファイル履歴" count={history.attachments.length}>{history.attachments.map((row) => <HistoryRow key={row.id} primary={row.name} secondary={`${row.contentType} ・ ${formatFileSize(row.size)}`} />)}</HistoryGroup></div>}</section>
}

function HistoryGroup({ title, count, children }: { title: string; count: number; children: ReactNode }) {
  return <div className="vehicle-history-group"><div className="vehicle-history-group-header"><strong>{title}</strong><span>{count}件</span></div>{count ? children : <small className="vehicle-history-none">履歴はありません。</small>}</div>
}

function HistoryRow({ primary, secondary, amount }: { primary: string; secondary: string; amount?: number }) {
  return <div className="vehicle-history-row"><span><strong>{primary}</strong><small>{secondary}</small></span>{amount === undefined ? null : <b>{formatYen(amount)}</b>}</div>
}

function formatYen(amount: number) { return `¥${new Intl.NumberFormat('ja-JP').format(Math.round(amount))}` }
function formatHistoryDate(value: string | null) { return value ? value.slice(0, 10).replaceAll('-', '/') : '日付未登録' }

function DetailField({ label, value }: { label: string; value: string }) {
  return <div className="detail-field"><span>{label}</span><strong>{value}</strong></div>
}

function AttachmentSection({ vehicle, onAttachments, onAttachmentDrop, onPreviewAttachment, onRemoveAttachment }: { vehicle: Vehicle; onAttachments: (event: ChangeEvent<HTMLInputElement>, vehicleId: string) => void; onAttachmentDrop: (event: DragEvent<HTMLLabelElement>, vehicleId: string) => void; onPreviewAttachment: (vehicleId: string, attachment: Attachment, mode: 'preview' | 'download') => void; onRemoveAttachment: (vehicleId: string, attachmentId: string) => void }) {
  return (
    <section className="attachments-section">
      <div className="attachments-header">
        <div><h3>添付ファイル</h3><span>写真・車検証PDFなどを車両ごとに保存</span></div>
        <label className="attachment-add-button"><Plus size={16} />追加<input className="hidden-input" type="file" accept=".jpg,.jpeg,.png,.pdf,image/jpeg,image/png,application/pdf" multiple onChange={(event) => onAttachments(event, vehicle.id)} /></label>
      </div>
      {vehicle.attachments.length > 0 && <div className="attachments-grid">{vehicle.attachments.map((attachment) => <div className="attachment-card" key={attachment.id}><span className={`attachment-icon attachment-icon-${attachment.type}`}>{attachment.type === 'image' ? <ImageIcon size={19} /> : <FileText size={19} />}</span><span className="attachment-card-copy"><strong title={attachment.name}>{attachment.name}</strong><small>{formatFileSize(attachment.size)} ・ {attachment.createdAt}</small></span><span className="attachment-actions"><button className="attachment-action" type="button" aria-label={`${attachment.name}をプレビュー`} title="プレビュー" onClick={() => onPreviewAttachment(vehicle.id, attachment, 'preview')}><Eye size={15} /></button><button className="attachment-action" type="button" aria-label={`${attachment.name}をダウンロード`} title="ダウンロード" onClick={() => onPreviewAttachment(vehicle.id, attachment, 'download')}><Download size={15} /></button><button className="attachment-remove" type="button" aria-label={`${attachment.name}を削除`} onClick={() => onRemoveAttachment(vehicle.id, attachment.id)}><Trash2 size={15} /></button></span></div>)}</div>}
      <label className="attachment-dropzone" onDragEnter={(event) => { event.preventDefault(); event.currentTarget.classList.add('is-dragging') }} onDragOver={(event) => { event.preventDefault(); event.currentTarget.classList.add('is-dragging') }} onDragLeave={(event) => event.currentTarget.classList.remove('is-dragging')} onDrop={(event) => onAttachmentDrop(event, vehicle.id)}>
        <Paperclip size={21} />
        <strong>ファイルをドロップ</strong>
        <span>ここにドラッグ＆ドロップ、またはクリックして選択（JPEG・PNG・PDF）</span>
        <input className="hidden-input" type="file" accept=".jpg,.jpeg,.png,.pdf,image/jpeg,image/png,application/pdf" multiple onChange={(event) => onAttachments(event, vehicle.id)} />
      </label>
    </section>
  )
}

function CustomerDialog({ form, title, submitLabel, onChange, onClose, onSubmit }: { form: CustomerInput; title: string; submitLabel: string; onChange: (form: CustomerInput) => void; onClose: () => void; onSubmit: (event: FormEvent<HTMLFormElement>) => void }) {
  return <Modal title={title} onClose={onClose}><form className="modal-form" onSubmit={onSubmit}><div className="form-grid"><FormField label="顧客名" required><input autoFocus required value={form.name} onChange={(event) => onChange({ ...form, name: event.target.value })} placeholder="例：佐藤 太郎" /></FormField><FormField label="ふりがな"><input value={form.kana} onChange={(event) => onChange({ ...form, kana: event.target.value })} placeholder="例：さとう たろう" /></FormField><FormField label="電話番号"><input type="tel" value={form.phone} onChange={(event) => onChange({ ...form, phone: event.target.value })} placeholder="例：090-1234-5678" /></FormField><FormField label="メールアドレス"><input type="email" value={form.email} onChange={(event) => onChange({ ...form, email: event.target.value })} placeholder="例：sato@example.com" /></FormField><FormField label="郵便番号"><input value={form.postalCode ?? ''} onChange={(event) => onChange({ ...form, postalCode: event.target.value })} placeholder="例：100-0001" /></FormField><FormField label="住所"><input value={form.address} onChange={(event) => onChange({ ...form, address: event.target.value })} placeholder="例：東京都千代田区" /></FormField><FormField label="メモ"><textarea value={form.memo} onChange={(event) => onChange({ ...form, memo: event.target.value })} placeholder="連絡方法など" /></FormField></div><ModalFooter onClose={onClose} submitLabel={submitLabel} disabled={false} /></form></Modal>
}

function VehicleDialog({ form, title, submitLabel, customerName, onChange, onClose, onSubmit }: { form: VehicleInput; title: string; submitLabel: string; customerName: string; onChange: (form: VehicleInput) => void; onClose: () => void; onSubmit: (event: FormEvent<HTMLFormElement>) => void }) {
  return <Modal title={title} onClose={onClose}><form className="modal-form" onSubmit={onSubmit}><p className="modal-description"><UserRound size={16} />{customerName} の車両情報を登録します。</p><div className="form-grid"><FormField label="メーカー" required><input autoFocus required value={form.maker} onChange={(event) => onChange({ ...form, maker: event.target.value })} placeholder="例：トヨタ" /></FormField><FormField label="車名" required><input required value={form.model} onChange={(event) => onChange({ ...form, model: event.target.value })} placeholder="例：プリウス" /></FormField><FormField label="型式"><input value={form.modelType} onChange={(event) => onChange({ ...form, modelType: event.target.value })} placeholder="例：6AA-ZVW60" /></FormField><FormField label="登録番号"><input value={form.plate} onChange={(event) => onChange({ ...form, plate: event.target.value })} placeholder="例：品川 500 あ 1234" /></FormField><FormField label="車台番号"><input value={form.vin} onChange={(event) => onChange({ ...form, vin: event.target.value })} placeholder="例：ZVW5000001" /></FormField><FormField label="年式"><input value={form.year} onChange={(event) => onChange({ ...form, year: event.target.value })} placeholder="例：2024年" /></FormField><FormField label="車検満了日"><input type="date" value={form.inspectionDate.replace(/\//g, '-')} onChange={(event) => onChange({ ...form, inspectionDate: event.target.value.replace(/-/g, '/') })} /></FormField><FormField label="走行距離"><input value={form.mileage} onChange={(event) => onChange({ ...form, mileage: event.target.value })} placeholder="例：12,500 km" /></FormField><FormField label="車体色"><input value={form.color} onChange={(event) => onChange({ ...form, color: event.target.value })} placeholder="例：パールホワイト" /></FormField><FormField label="排気量"><input inputMode="numeric" value={form.displacement} onChange={(event) => onChange({ ...form, displacement: event.target.value })} placeholder="例：1800 cc" /></FormField><FormField label="ミッション"><input value={form.transmission} onChange={(event) => onChange({ ...form, transmission: event.target.value })} placeholder="例：CVT" /></FormField><FormField label="自由項目1"><input value={form.freeItem1} onChange={(event) => onChange({ ...form, freeItem1: event.target.value })} placeholder="例：駆動方式" /></FormField><FormField label="自由項目2"><input value={form.freeItem2} onChange={(event) => onChange({ ...form, freeItem2: event.target.value })} placeholder="自由項目" /></FormField><FormField label="自由項目3"><input value={form.freeItem3} onChange={(event) => onChange({ ...form, freeItem3: event.target.value })} placeholder="自由項目" /></FormField><FormField label="備考"><textarea value={form.note} onChange={(event) => onChange({ ...form, note: event.target.value })} placeholder="車両に関するメモ" /></FormField></div><ModalFooter onClose={onClose} submitLabel={submitLabel} disabled={false} /></form></Modal>
}

function FormField({ label, required, children }: { label: string; required?: boolean; children: ReactNode }) {
  return <label className="form-field"><span>{label}{required && <em>必須</em>}</span>{children}</label>
}

function Modal({ title, onClose, children }: { title: string; onClose: () => void; children: ReactNode }) {
  return <div className="modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose() }}><section className="modal" role="dialog" aria-modal="true" aria-labelledby="modal-title"><div className="modal-header"><h2 id="modal-title">{title}</h2><button className="modal-close" type="button" aria-label="閉じる" onClick={onClose}><X size={19} /></button></div>{children}</section></div>
}

function ModalFooter({ onClose, submitLabel, disabled }: { onClose: () => void; submitLabel: string; disabled: boolean }) {
  return <div className="modal-footer"><button className="button button-secondary" type="button" onClick={onClose} disabled={disabled}>キャンセル</button><button className="button button-primary" type="submit" disabled={disabled}>{submitLabel}</button></div>
}

function formatFileSize(bytes: number) {
  if (bytes < 1024 * 1024) return `${Math.max(1, Math.round(bytes / 1024))} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

function getErrorMessage(reason: unknown) {
  return reason instanceof Error ? reason.message : '顧客・車両データの処理に失敗しました。'
}
