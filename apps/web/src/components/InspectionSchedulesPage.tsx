import { useEffect, useMemo, useState } from 'react'
import { CalendarClock, Search } from 'lucide-react'
import { DashboardCalendar } from './DashboardCalendar'
import type { CustomerVehicleNavigation } from './CustomerVehiclePage'
import { fetchCustomers, type Customer } from '../lib/customerApi'
import type { DashboardCalendarEvent } from '../lib/dashboardApi'

const vehicleSearchFields = ['すべて', '顧客名', '車名', '登録番号', '車台番号'] as const
type VehicleSearchField = (typeof vehicleSearchFields)[number]

const vehicleSearchPlaceholders: Record<VehicleSearchField, string> = {
  すべて: '顧客名、車名、登録番号、車台番号で検索',
  顧客名: '顧客名で検索',
  車名: '車名で検索',
  登録番号: '登録番号で検索',
  車台番号: '車台番号で検索',
}

const vehicleInspectionLegendCategories: Array<{ category: DashboardCalendarEvent['category']; label: string }> = [{ category: 'vehicle-inspection', label: '車検' }]

type InspectionVehicle = {
  id: string
  customerId: string
  customerName: string
  vehicleName: string
  plate: string
  vin: string
  inspectionDate: string
}

export function InspectionSchedulesPage({ onSelectVehicle }: { onSelectVehicle?: (target: CustomerVehicleNavigation) => void } = {}) {
  const [customers, setCustomers] = useState<Customer[]>([])
  const [query, setQuery] = useState('')
  const [searchField, setSearchField] = useState<VehicleSearchField>('すべて')
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')

  useEffect(() => {
    let active = true
    setLoading(true)
    fetchCustomers().then((nextCustomers) => {
      if (!active) return
      setCustomers(nextCustomers)
      setError('')
    }).catch((reason: unknown) => {
      if (active) setError(reason instanceof Error ? reason.message : '車検予定を読み込めませんでした。')
    }).finally(() => {
      if (active) setLoading(false)
    })
    return () => { active = false }
  }, [])

  const inspectionVehicles = useMemo(() => customers.flatMap((customer) => customer.vehicles.flatMap((vehicle) => {
    if (!isValidDate(vehicle.inspectionDate)) return []
    return [{
      id: vehicle.id,
      customerId: customer.id,
      customerName: customer.name || '顧客未登録',
      vehicleName: [vehicle.maker, vehicle.model].filter(Boolean).join(' ') || '車両未登録',
      plate: vehicle.plate,
      vin: vehicle.vin,
      inspectionDate: vehicle.inspectionDate,
    }]
  })).sort((left, right) => normalizeDate(left.inspectionDate).localeCompare(normalizeDate(right.inspectionDate)) || left.customerName.localeCompare(right.customerName, 'ja')), [customers])

  const filteredVehicles = useMemo(() => {
    const normalizedQuery = query.trim().toLocaleLowerCase()
    if (!normalizedQuery) return inspectionVehicles
    return inspectionVehicles.filter((vehicle) => getVehicleSearchText(vehicle, searchField).toLocaleLowerCase().includes(normalizedQuery))
  }, [inspectionVehicles, query, searchField])

  const vehicleInspectionEvents = useMemo<DashboardCalendarEvent[]>(() => filteredVehicles.map((vehicle) => ({
    id: `vehicle-${vehicle.id}-inspection`,
    date: vehicle.inspectionDate,
    category: 'vehicle-inspection',
    categoryLabel: '車検',
    title: `車検：${vehicle.customerName}`,
    detail: `${vehicle.vehicleName}${vehicle.plate ? ` ・ ${vehicle.plate}` : ''}`,
    status: '車検',
    amount: null,
  })), [filteredVehicles])

  return <>
    <div className="page-header inspection-page-header"><div><span className="page-eyebrow">点検予定</span><h1>車検予定</h1><p>顧客・車両に登録されている車検満了日を確認・管理します。</p></div></div>
    {error && <div className="customer-sync-status is-error" role="alert"><span>{error}</span><button className="text-button" type="button" onClick={() => window.location.reload()}>再読み込み</button></div>}
    {loading && <div className="customer-sync-status" role="status"><span>車検予定を読み込んでいます。</span></div>}
    <DashboardCalendar events={vehicleInspectionEvents} loading={loading} eyebrow="車検期限を確認" title="車検満了カレンダー" description="顧客・車両に登録されている車検満了日のみを表示しています。" legendCategories={vehicleInspectionLegendCategories} titleId="inspection-calendar-title" detailTitleId="inspection-calendar-detail-title" />
    <div className="customer-toolbar inspection-vehicle-toolbar">
      <label className="customer-search"><Search size={19} /><span className="sr-only">車検予定を検索</span><input value={query} onChange={(event) => setQuery(event.target.value)} placeholder={vehicleSearchPlaceholders[searchField]} /></label>
      <label className="customer-search-filter"><span className="sr-only">検索項目</span><select value={searchField} onChange={(event) => setSearchField(event.target.value as VehicleSearchField)}>{vehicleSearchFields.map((field) => <option key={field} value={field}>{field}</option>)}</select></label>
      <span className="inspection-result-summary"><strong>{filteredVehicles.length}件</strong><span>車検</span></span>
    </div>
    <section className="inspection-schedule-grid">{filteredVehicles.map((vehicle) => <article className="panel inspection-schedule-card inspection-vehicle-card" key={vehicle.id}><button className="inspection-vehicle-card-button" type="button" onClick={() => onSelectVehicle?.({ section: 'customers', customerId: vehicle.customerId, vehicleId: vehicle.id })} aria-label={`${vehicle.customerName}の${vehicle.vehicleName}の車検詳細を開く`}><div className="inspection-card-header"><span className="inspection-type-badge"><CalendarClock size={15} />車検</span><span className="inspection-state">車検</span></div><h2>{vehicle.customerName}</h2><p>{vehicle.vehicleName}</p><div className="inspection-card-date"><span>車検満了日</span><strong className={dateTone(vehicle.inspectionDate)}>{formatDate(vehicle.inspectionDate)}</strong></div><div className="inspection-card-note">登録番号：{vehicle.plate || '未登録'}<br />車台番号：{vehicle.vin || '未登録'}</div></button></article>)}{!filteredVehicles.length && <div className="panel inspection-empty"><CalendarClock size={30} /><strong>車検満了日が登録された車両がありません</strong><span>{loading ? '読み込み中です。' : query ? '検索条件を変更してください。' : '顧客・車両タブで車検満了日を登録してください。'}</span></div>}</section>
  </>
}

function getVehicleSearchText(vehicle: InspectionVehicle, field: VehicleSearchField) {
  const values = {
    顧客名: vehicle.customerName,
    車名: vehicle.vehicleName,
    登録番号: vehicle.plate,
    車台番号: vehicle.vin,
  }
  return field === 'すべて' ? Object.values(values).join(' ') : values[field]
}

function normalizeDate(value: string) {
  return value.slice(0, 10).replaceAll('/', '-')
}

function isValidDate(value: string) {
  const normalized = normalizeDate(value)
  if (!/^\d{4}-\d{2}-\d{2}$/.test(normalized)) return false
  const date = new Date(`${normalized}T00:00:00`)
  return !Number.isNaN(date.getTime()) && date.getFullYear() === Number(normalized.slice(0, 4)) && date.getMonth() + 1 === Number(normalized.slice(5, 7)) && date.getDate() === Number(normalized.slice(8, 10))
}

function formatDate(value: string) {
  return normalizeDate(value).replaceAll('-', '/')
}

function dateTone(value: string) {
  const diff = Math.ceil((new Date(`${normalizeDate(value)}T00:00:00`).getTime() - new Date(new Date().toISOString().slice(0, 10) + 'T00:00:00').getTime()) / 86_400_000)
  return diff < 0 ? 'is-danger' : diff <= 30 ? 'is-warning' : ''
}
