import { useEffect, useMemo, useRef, useState, type FormEvent } from 'react'
import { CalendarClock, Check, Pencil, Plus, Search, Trash2, X } from 'lucide-react'
import { fetchCustomers, type Customer } from '../lib/customerApi'
import { createInspectionSchedule, deleteInspectionSchedule, fetchInspectionSchedules, updateInspectionSchedule, type InspectionSchedule, type InspectionScheduleInput, type InspectionStatus, type InspectionType } from '../lib/inspectionApi'

const inspectionTypes: InspectionType[] = ['車検', '12か月点検', '24か月点検', '一般点検']
const inspectionStatuses: InspectionStatus[] = ['予定', '完了', 'キャンセル']

type ScheduleForm = InspectionScheduleInput

const emptyForm: ScheduleForm = { customerId: '', vehicleId: '', inspectionType: '車検', dueDate: today(), status: '予定', note: '' }

export function InspectionSchedulesPage({ initialScheduleId }: { initialScheduleId?: string } = {}) {
  const [schedules, setSchedules] = useState<InspectionSchedule[]>([])
  const [customers, setCustomers] = useState<Customer[]>([])
  const [query, setQuery] = useState('')
  const [statusFilter, setStatusFilter] = useState<'すべて' | InspectionStatus>('すべて')
  const [dialogOpen, setDialogOpen] = useState(false)
  const [editingId, setEditingId] = useState<string | null>(null)
  const [form, setForm] = useState<ScheduleForm>(emptyForm)
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState('')
  const initialScheduleIdRef = useRef(initialScheduleId)

  useEffect(() => {
    let active = true
    setLoading(true)
    Promise.all([fetchInspectionSchedules(), fetchCustomers()]).then(([nextSchedules, nextCustomers]) => {
      if (!active) return
      setSchedules(nextSchedules)
      setCustomers(nextCustomers)
      const targetSchedule = initialScheduleIdRef.current ? nextSchedules.find((schedule) => schedule.id === initialScheduleIdRef.current) : undefined
      if (targetSchedule) {
        setEditingId(targetSchedule.id)
        setForm({ customerId: targetSchedule.customerId, vehicleId: targetSchedule.vehicleId, inspectionType: targetSchedule.inspectionType, dueDate: toDisplayDate(targetSchedule.dueDate), status: targetSchedule.status, note: targetSchedule.note })
        setDialogOpen(true)
      }
      setError('')
    }).catch((reason: unknown) => {
      if (active) setError(reason instanceof Error ? reason.message : '点検予定を読み込めませんでした。')
    }).finally(() => {
      if (active) setLoading(false)
    })
    return () => { active = false }
  }, [])

  const filteredSchedules = useMemo(() => {
    const normalizedQuery = query.trim().toLocaleLowerCase()
    return schedules.filter((schedule) => {
      const matchesStatus = statusFilter === 'すべて' || schedule.status === statusFilter
      const searchable = `${schedule.customerName} ${schedule.vehicle} ${schedule.plate} ${schedule.inspectionType} ${schedule.note}`.toLocaleLowerCase()
      return matchesStatus && (!normalizedQuery || searchable.includes(normalizedQuery))
    })
  }, [query, schedules, statusFilter])

  function openCreate() {
    const customer = customers[0]
    setEditingId(null)
    setForm({ ...emptyForm, customerId: customer?.id ?? '', vehicleId: customer?.vehicles[0]?.id ?? '' })
    setDialogOpen(true)
  }

  function openEdit(schedule: InspectionSchedule) {
    setEditingId(schedule.id)
    setForm({ customerId: schedule.customerId, vehicleId: schedule.vehicleId, inspectionType: schedule.inspectionType, dueDate: toDisplayDate(schedule.dueDate), status: schedule.status, note: schedule.note })
    setDialogOpen(true)
  }

  function closeDialog() {
    setDialogOpen(false)
    setEditingId(null)
    setForm(emptyForm)
  }

  async function saveSchedule(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!form.customerId || !form.vehicleId || !form.dueDate || saving) return
    setSaving(true)
    setError('')
    try {
      const saved = editingId ? await updateInspectionSchedule(editingId, form) : await createInspectionSchedule(form)
      setSchedules((current) => editingId ? current.map((schedule) => schedule.id === saved.id ? saved : schedule) : [saved, ...current])
      closeDialog()
    } catch (reason: unknown) {
      setError(reason instanceof Error ? reason.message : '点検予定を保存できませんでした。')
    } finally {
      setSaving(false)
    }
  }

  async function markCompleted(schedule: InspectionSchedule) {
    setSaving(true)
    setError('')
    try {
      const saved = await updateInspectionSchedule(schedule.id, { customerId: schedule.customerId, vehicleId: schedule.vehicleId, inspectionType: schedule.inspectionType, dueDate: schedule.dueDate, status: schedule.status === '完了' ? '予定' : '完了', note: schedule.note })
      setSchedules((current) => current.map((item) => item.id === saved.id ? saved : item))
    } catch (reason: unknown) {
      setError(reason instanceof Error ? reason.message : '点検予定の状態を更新できませんでした。')
    } finally {
      setSaving(false)
    }
  }

  async function removeSchedule(schedule: InspectionSchedule) {
    if (!window.confirm(`${schedule.inspectionType}（${schedule.vehicle}）を削除しますか？`)) return
    setSaving(true)
    setError('')
    try {
      await deleteInspectionSchedule(schedule.id)
      setSchedules((current) => current.filter((item) => item.id !== schedule.id))
    } catch (reason: unknown) {
      setError(reason instanceof Error ? reason.message : '点検予定を削除できませんでした。')
    } finally {
      setSaving(false)
    }
  }

  const selectedCustomer = customers.find((customer) => customer.id === form.customerId)

  return <>
    <div className="page-header inspection-page-header"><div><span className="page-eyebrow">点検予定</span><h1>車検・点検予定</h1><p>車検、12か月点検、24か月点検、一般点検の予定と完了状態を管理します。</p></div><button className="button button-primary" type="button" disabled={!customers.length} onClick={openCreate}><Plus size={18} />点検予定を登録</button></div>
    {error && <div className="customer-sync-status is-error" role="alert"><span>{error}</span><button className="text-button" type="button" onClick={() => window.location.reload()}>再読み込み</button></div>}
    {loading && <div className="customer-sync-status" role="status"><span>点検予定を読み込んでいます。</span></div>}
    <div className="inspection-toolbar"><label className="inspection-search"><Search size={18} /><span className="sr-only">点検予定を検索</span><input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="顧客名、車名、登録番号、点検種別で検索" /></label><div className="inspection-filter-tabs"><button className={statusFilter === 'すべて' ? 'is-active' : ''} type="button" onClick={() => setStatusFilter('すべて')}>すべて</button>{inspectionStatuses.map((status) => <button className={statusFilter === status ? 'is-active' : ''} key={status} type="button" onClick={() => setStatusFilter(status)}>{status}</button>)}</div><span className="inspection-result-summary"><strong>{filteredSchedules.length}件</strong><span>点検予定</span></span></div>
    <section className="inspection-schedule-grid">{filteredSchedules.map((schedule) => <article className={`panel inspection-schedule-card inspection-status-${schedule.status}`} key={schedule.id}><div className="inspection-card-header"><span className="inspection-type-badge"><CalendarClock size={15} />{schedule.inspectionType}</span><span className={`inspection-state inspection-state-${schedule.status}`}>{schedule.status}</span></div><h2>{schedule.customerName}</h2><p>{schedule.vehicle} {schedule.plate && `・ ${schedule.plate}`}</p><div className="inspection-card-date"><span>予定日</span><strong className={dateTone(schedule.dueDate, schedule.status)}>{formatDate(schedule.dueDate)}</strong></div>{schedule.note && <div className="inspection-card-note">{schedule.note}</div>}<div className="inspection-card-actions"><button className="button button-secondary" type="button" disabled={saving} onClick={() => void markCompleted(schedule)}><Check size={15} />{schedule.status === '完了' ? '予定に戻す' : '完了にする'}</button><button className="icon-button" type="button" aria-label="点検予定を編集" title="編集" onClick={() => openEdit(schedule)}><Pencil size={16} /></button><button className="icon-button danger" type="button" aria-label="点検予定を削除" title="削除" onClick={() => void removeSchedule(schedule)}><Trash2 size={16} /></button></div></article>)}{!filteredSchedules.length && <div className="panel inspection-empty"><CalendarClock size={30} /><strong>点検予定がありません</strong><span>{loading ? '読み込み中です。' : '条件を変更するか、点検予定を登録してください。'}</span></div>}</section>
    {dialogOpen && <div className="modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) closeDialog() }}><section className="modal" role="dialog" aria-modal="true" aria-labelledby="inspection-modal-title"><div className="modal-header"><h2 id="inspection-modal-title">{editingId ? '点検予定を編集' : '点検予定を登録'}</h2><button className="modal-close" type="button" aria-label="閉じる" onClick={closeDialog}><X size={19} /></button></div><form className="modal-form" onSubmit={(event) => void saveSchedule(event)}><div className="form-grid"><label className="form-field"><span>点検種別<em>必須</em></span><select required value={form.inspectionType} onChange={(event) => setForm({ ...form, inspectionType: event.target.value as InspectionType })}>{inspectionTypes.map((type) => <option key={type}>{type}</option>)}</select></label><label className="form-field"><span>状態</span><select value={form.status} onChange={(event) => setForm({ ...form, status: event.target.value as InspectionStatus })}>{inspectionStatuses.map((status) => <option key={status}>{status}</option>)}</select></label><label className="form-field"><span>顧客<em>必須</em></span><select required value={form.customerId} onChange={(event) => { const nextCustomer = customers.find((customer) => customer.id === event.target.value); setForm({ ...form, customerId: event.target.value, vehicleId: nextCustomer?.vehicles[0]?.id ?? '' }) }}><option value="">顧客を選択してください</option>{customers.map((customer) => <option key={customer.id} value={customer.id}>{customer.name}</option>)}</select></label><label className="form-field"><span>対象車両<em>必須</em></span><select required disabled={!selectedCustomer?.vehicles.length} value={form.vehicleId} onChange={(event) => setForm({ ...form, vehicleId: event.target.value })}><option value="">車両を選択してください</option>{selectedCustomer?.vehicles.map((vehicle) => <option key={vehicle.id} value={vehicle.id}>{vehicle.maker} {vehicle.model} ・ {vehicle.plate || '登録番号なし'}</option>)}</select></label><label className="form-field"><span>予定日<em>必須</em></span><input required type="date" value={form.dueDate.replaceAll('/', '-')} onChange={(event) => setForm({ ...form, dueDate: event.target.value.replaceAll('-', '/') })} /></label><label className="form-field"><span>備考</span><textarea value={form.note} onChange={(event) => setForm({ ...form, note: event.target.value })} placeholder="案内方法や作業メモ" /></label></div><div className="modal-footer"><button className="button button-secondary" type="button" disabled={saving} onClick={closeDialog}>キャンセル</button><button className="button button-primary" type="submit" disabled={saving}>{saving ? '保存中…' : editingId ? '変更を保存' : '登録する'}</button></div></form></section></div>}
  </>
}

function today() { return new Date().toISOString().slice(0, 10).replaceAll('-', '/') }
function toDisplayDate(value: string) { return value.slice(0, 10).replaceAll('-', '/') }
function formatDate(value: string) { return toDisplayDate(value) }
function dateTone(value: string, status: InspectionStatus) {
  if (status !== '予定') return ''
  const diff = Math.ceil((new Date(`${value.slice(0, 10)}T00:00:00`).getTime() - new Date(new Date().toISOString().slice(0, 10) + 'T00:00:00').getTime()) / 86_400_000)
  return diff < 0 ? 'is-danger' : diff <= 30 ? 'is-warning' : ''
}
