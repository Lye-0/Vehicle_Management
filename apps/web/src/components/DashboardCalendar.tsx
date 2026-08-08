import { useMemo, useState, type FormEvent } from 'react'
import { CalendarClock, CalendarDays, CarFront, ChevronLeft, ChevronRight, CircleDollarSign, ClipboardCheck, FileText, Plus, Trash2, X } from 'lucide-react'
import type { DashboardCalendarEvent } from '../lib/dashboardApi'
import type { SharedScheduleInput } from '../lib/sharedSchedulesApi'

const weekdayLabels = ['日', '月', '火', '水', '木', '金', '土']
type CalendarCategory = DashboardCalendarEvent['category']
type CalendarLegendCategory = { category: CalendarCategory; label: string }

const defaultLegendCategories: CalendarLegendCategory[] = [
  { category: 'inspection', label: '整備' },
  { category: 'vehicle-inspection', label: '車検満了' },
  { category: 'payment-due', label: '支払期限' },
  { category: 'sales', label: '販売書類作成日' },
  { category: 'maintenance', label: '整備書類作成日' },
  { category: 'shared', label: '組織内共有スケジュール' },
]

type DashboardCalendarProps = {
  events: DashboardCalendarEvent[]
  loading: boolean
  onSelectEvent?: (event: DashboardCalendarEvent) => void
  onCreateSharedSchedule?: (input: SharedScheduleInput) => Promise<void>
  onUpdateSharedSchedule?: (id: string, input: SharedScheduleInput) => Promise<void>
  onDeleteSharedSchedule?: (id: string) => Promise<void>
  eyebrow?: string
  title?: string
  description?: string
  legendCategories?: CalendarLegendCategory[]
  defaultEnabledCategories?: CalendarCategory[]
  titleId?: string
  detailTitleId?: string
}

type CalendarRangeSegment = {
  event: DashboardCalendarEvent
  startColumn: number
  endColumn: number
  lane: number
  startsAtEvent: boolean
  endsAtEvent: boolean
}

type CalendarWeekRangeData = {
  segments: CalendarRangeSegment[]
  laneCount: number
}

export function DashboardCalendar({
  events,
  loading,
  onSelectEvent,
  onCreateSharedSchedule,
  onUpdateSharedSchedule,
  onDeleteSharedSchedule,
  eyebrow = '予定をまとめて確認',
  title = '業務カレンダー',
  description = '車検、車検満了、販売・整備書類作成日、支払期限、組織内共有スケジュールを表示しています。',
  legendCategories = defaultLegendCategories,
  defaultEnabledCategories,
  titleId = 'dashboard-calendar-title',
  detailTitleId = 'calendar-detail-title',
}: DashboardCalendarProps) {
  const today = todayDateKey()
  const [viewDate, setViewDate] = useState(() => startOfMonth(parseDateKey(today) ?? new Date()))
  const [selectedDate, setSelectedDate] = useState(today)
  const [enabledCategories, setEnabledCategories] = useState<Set<CalendarCategory>>(() => new Set(defaultEnabledCategories ?? legendCategories.map(({ category }) => category)))
  const [sharedScheduleModalOpen, setSharedScheduleModalOpen] = useState(false)
  const [sharedScheduleModalMode, setSharedScheduleModalMode] = useState<'create' | 'edit'>('create')
  const [sharedScheduleTargetId, setSharedScheduleTargetId] = useState<string | null>(null)
  const [sharedScheduleForm, setSharedScheduleForm] = useState<SharedScheduleInput>(() => emptySharedScheduleForm(today))
  const [sharedScheduleSaving, setSharedScheduleSaving] = useState(false)
  const [sharedScheduleError, setSharedScheduleError] = useState('')
  const calendarWeeks = useMemo(() => buildCalendarWeeks(viewDate), [viewDate])
  const visibleEvents = useMemo(() => events.filter((event) => enabledCategories.has(event.category)), [enabledCategories, events])
  const eventsByDate = useMemo(() => groupEventsByDate(visibleEvents), [visibleEvents])
  const rangeDataByWeek = useMemo(() => buildRangeDataByWeek(visibleEvents, calendarWeeks), [calendarWeeks, visibleEvents])
  const selectedEvents = eventsByDate.get(selectedDate) ?? []
  const monthEventCount = useMemo(() => visibleEvents.filter((event) => eventIntersectsMonth(event, viewDate)).length, [viewDate, visibleEvents])

  function selectDate(date: Date) {
    const nextDate = toDateKey(date)
    setSelectedDate(nextDate)
    if (!isSameMonth(date, viewDate)) setViewDate(startOfMonth(date))
  }

  function changeMonth(offset: number) {
    const nextMonth = new Date(viewDate.getFullYear(), viewDate.getMonth() + offset, 1)
    setViewDate(nextMonth)
    setSelectedDate(toDateKey(nextMonth))
  }

  function selectToday() {
    const nextToday = parseDateKey(today) ?? new Date()
    setViewDate(startOfMonth(nextToday))
    setSelectedDate(today)
  }

  function toggleCategory(category: CalendarCategory) {
    setEnabledCategories((current) => {
      const next = new Set(current)
      if (next.has(category)) next.delete(category)
      else next.add(category)
      return next
    })
  }

  function openSharedScheduleModal() {
    setSharedScheduleModalMode('create')
    setSharedScheduleTargetId(null)
    setSharedScheduleForm(emptySharedScheduleForm(selectedDate))
    setSharedScheduleError('')
    setSharedScheduleModalOpen(true)
  }

  function openSharedScheduleEdit(event: DashboardCalendarEvent) {
    if (!event.sharedScheduleId || !onUpdateSharedSchedule) return
    setSharedScheduleModalMode('edit')
    setSharedScheduleTargetId(event.sharedScheduleId)
    setSharedScheduleForm({ title: event.title, startDate: event.date, endDate: event.endDate, detail: event.detail })
    setSharedScheduleError('')
    setSharedScheduleModalOpen(true)
  }

  async function submitSharedSchedule(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (sharedScheduleModalMode === 'create' && !onCreateSharedSchedule) return
    if (sharedScheduleModalMode === 'edit' && (!sharedScheduleTargetId || !onUpdateSharedSchedule)) return
    setSharedScheduleSaving(true)
    setSharedScheduleError('')
    try {
      if (sharedScheduleModalMode === 'edit' && sharedScheduleTargetId && onUpdateSharedSchedule) await onUpdateSharedSchedule(sharedScheduleTargetId, sharedScheduleForm)
      else if (onCreateSharedSchedule) await onCreateSharedSchedule(sharedScheduleForm)
      setSharedScheduleModalOpen(false)
    } catch (reason) {
      setSharedScheduleError(reason instanceof Error ? reason.message : sharedScheduleModalMode === 'edit' ? '共有スケジュールを更新できませんでした。' : '共有スケジュールを登録できませんでした。')
    } finally {
      setSharedScheduleSaving(false)
    }
  }

  async function deleteSharedSchedule() {
    if (sharedScheduleModalMode !== 'edit' || !sharedScheduleTargetId || !onDeleteSharedSchedule) return
    if (!window.confirm(`「${sharedScheduleForm.title || 'この共有スケジュール'}」を削除しますか？`)) return
    setSharedScheduleSaving(true)
    setSharedScheduleError('')
    try {
      await onDeleteSharedSchedule(sharedScheduleTargetId)
      setSharedScheduleModalOpen(false)
    } catch (reason) {
      setSharedScheduleError(reason instanceof Error ? reason.message : '共有スケジュールを削除できませんでした。')
    } finally {
      setSharedScheduleSaving(false)
    }
  }

  return (
    <section className="panel dashboard-calendar-panel" aria-labelledby={titleId}>
      <div className="dashboard-calendar-top">
        <div className="dashboard-calendar-heading">
          <div>
            <span className="calendar-heading-kicker"><CalendarDays size={15} />{eyebrow}</span>
            <h2 id={titleId}>{title}</h2>
            <p>{description}</p>
          </div>
          {onCreateSharedSchedule && <button className="calendar-add-button" type="button" aria-label="共有スケジュールを追加" title="共有スケジュールを追加" onClick={openSharedScheduleModal}><Plus size={18} /><span>共有予定を追加</span></button>}
        </div>
        <div className="calendar-legend" aria-label="カレンダーに表示する予定の種類">
          {legendCategories.map(({ category, label }) => {
            const enabled = enabledCategories.has(category)
            return <button className={`calendar-legend-toggle${enabled ? ' is-enabled' : ' is-disabled'}`} key={category} type="button" aria-pressed={enabled} onClick={() => toggleCategory(category)}><i className={`calendar-legend-dot calendar-event-${category}`} /><span>{label}</span></button>
          })}
        </div>
      </div>

      <div className="dashboard-calendar-layout">
        <div className="dashboard-calendar-board">
          <div className="dashboard-calendar-monthbar">
            <div><h3>{formatMonth(viewDate)}</h3><span>{monthEventCount}件の予定</span></div>
            <div className="calendar-controls" aria-label="カレンダー操作">
              <button className="button button-secondary calendar-today-button" type="button" onClick={selectToday}>今日</button>
              <button className="calendar-icon-button" type="button" aria-label="前月" title="前月" onClick={() => changeMonth(-1)}><ChevronLeft size={18} /></button>
              <button className="calendar-icon-button" type="button" aria-label="次月" title="次月" onClick={() => changeMonth(1)}><ChevronRight size={18} /></button>
            </div>
          </div>
          <div className="dashboard-calendar-scroll">
            <div className="dashboard-calendar-grid" role="grid" aria-label={`${formatMonth(viewDate)}のカレンダー`}>
              <div className="dashboard-calendar-weekdays">
                {weekdayLabels.map((label, index) => <div className={`dashboard-calendar-weekday${index === 0 ? ' is-sunday' : ''}${index === 6 ? ' is-saturday' : ''}`} role="columnheader" key={label}>{label}</div>)}
              </div>
              <div className="dashboard-calendar-weeks">
                {calendarWeeks.map((week, weekIndex) => {
                  const rangeData = rangeDataByWeek[weekIndex]
                  return <div className="dashboard-calendar-week" key={toDateKey(week[0])}>
                    <div className="dashboard-calendar-week-days">
                      {week.map((day, dayIndex) => {
                        const date = toDateKey(day)
                        const dayEvents = eventsByDate.get(date) ?? []
                        const pointEvents = dayEvents.filter((event) => !isRangeEvent(event))
                        const visibleEventsForDay = pointEvents.slice(0, 3)
                        const hiddenEventCount = pointEvents.length - visibleEventsForDay.length
                        const isCurrentMonth = isSameMonth(day, viewDate)
                        const isSelected = date === selectedDate
                        const isToday = date === today
                        const rangeLaneCount = getRangeLaneCountForDay(rangeData, dayIndex)
                        const eventListStyle = rangeLaneCount > 0 ? { paddingTop: rangeLaneCount * 36 - 2 } : undefined
                        return <button className={`dashboard-calendar-day${isCurrentMonth ? '' : ' is-outside'}${isSelected ? ' is-selected' : ''}${isToday ? ' is-today' : ''}`} type="button" role="gridcell" aria-label={`${formatFullDate(day)}、予定${dayEvents.length}件`} aria-pressed={isSelected} key={date} onClick={() => selectDate(day)}>
                          <span className="calendar-day-number"><span>{day.getDate()}</span>{isToday && <em>今日</em>}</span>
                          <span className="calendar-day-event-list" style={eventListStyle}>
                            {visibleEventsForDay.map((event) => <span className={`calendar-event-chip calendar-event-${event.category}`} key={event.id} title={`${calendarEventLabel(event)}：${event.title}`}><span>{calendarEventLabel(event)}</span><strong>{event.title}</strong></span>)}
                            {hiddenEventCount > 0 && <span className="calendar-more-events">+{hiddenEventCount}件</span>}
                          </span>
                        </button>
                      })}
                    </div>
                    {rangeData.segments.length > 0 && <div className="dashboard-calendar-range-layer" aria-hidden="true">
                      {rangeData.segments.map((segment) => <span className={`calendar-range-event calendar-event-${segment.event.category}${segment.startsAtEvent ? ' is-start' : ''}${segment.endsAtEvent ? ' is-end' : ''}`} key={`${segment.event.id}-${segment.startColumn}`} style={{ gridColumn: `${segment.startColumn} / ${segment.endColumn + 1}`, gridRow: segment.lane + 1 }} title={`${calendarEventLabel(segment.event)}：${segment.event.title}`}>
                        {segment.startsAtEvent && <><span>{calendarEventLabel(segment.event)}</span><strong>{segment.event.title}</strong></>}
                      </span>)}
                    </div>}
                  </div>
                })}
              </div>
            </div>
          </div>
        </div>

        <aside className="dashboard-calendar-detail" aria-live="polite" aria-labelledby={detailTitleId}>
          <div className="calendar-detail-heading">
            <span className="calendar-heading-kicker">SELECTED DATE</span>
            <h3 id={detailTitleId}>{formatFullDate(parseDateKey(selectedDate) ?? new Date())}</h3>
            <span className="calendar-detail-count">{selectedEvents.length}件の予定</span>
          </div>
          {loading ? <div className="calendar-detail-empty"><CalendarDays size={24} /><strong>予定を読み込んでいます</strong><span>店舗データを集計しています。</span></div> : selectedEvents.length ? <div className="calendar-detail-list">{selectedEvents.map((event) => <CalendarEventDetail event={event} key={event.id} onSelectEvent={onSelectEvent} onEditSharedSchedule={onUpdateSharedSchedule ? openSharedScheduleEdit : undefined} />)}</div> : <div className="calendar-detail-empty"><CalendarDays size={24} /><strong>この日の予定はありません</strong><span>表示する予定の種類をオンにするか、別の日付を選択してください。</span></div>}
        </aside>
      </div>
      {sharedScheduleModalOpen && (onCreateSharedSchedule || onUpdateSharedSchedule) && <SharedScheduleModal mode={sharedScheduleModalMode} form={sharedScheduleForm} saving={sharedScheduleSaving} error={sharedScheduleError} onChange={setSharedScheduleForm} onClose={() => { if (!sharedScheduleSaving) setSharedScheduleModalOpen(false) }} onDelete={sharedScheduleModalMode === 'edit' && onDeleteSharedSchedule ? () => void deleteSharedSchedule() : undefined} onSubmit={(event) => void submitSharedSchedule(event)} />}
    </section>
  )
}

function CalendarEventDetail({ event, onSelectEvent, onEditSharedSchedule }: { event: DashboardCalendarEvent; onSelectEvent?: (event: DashboardCalendarEvent) => void; onEditSharedSchedule?: (event: DashboardCalendarEvent) => void }) {
  const Icon = event.category === 'vehicle-inspection' ? CarFront : event.category === 'inspection' ? CalendarClock : event.category === 'maintenance' ? ClipboardCheck : event.category === 'sales' ? FileText : event.category === 'shared' ? CalendarDays : CircleDollarSign
  const content = <><div className="calendar-detail-item-header"><span className="calendar-detail-type"><Icon size={14} />{event.categoryLabel}</span>{event.status && <span className="calendar-detail-status">{event.status}</span>}</div><h4>{event.title}</h4>{event.authorName && <p className="calendar-detail-author">作成者：{event.authorName}</p>}<p className="calendar-detail-description">{event.detail || '詳細はありません。'}</p>{event.amount !== null && <strong className="calendar-detail-amount">{formatYen(event.amount)}</strong>}</>
  const isSharedScheduleSelectable = Boolean(event.category === 'shared' && event.sharedScheduleId && onEditSharedSchedule)
  const isSelectable = Boolean(onSelectEvent && event.navigation)
  if (isSharedScheduleSelectable) return <button className={`calendar-detail-item calendar-detail-item-action calendar-event-${event.category}`} type="button" onClick={() => onEditSharedSchedule?.(event)} aria-label={`${event.title}の編集・削除画面を開く`}>{content}</button>
  return isSelectable ? <button className={`calendar-detail-item calendar-detail-item-action calendar-event-${event.category}`} type="button" onClick={() => onSelectEvent?.(event)} aria-label={`${event.title}の詳細を開く`}>{content}</button> : <article className={`calendar-detail-item calendar-event-${event.category}`}>{content}</article>
}

function SharedScheduleModal({ mode, form, saving, error, onChange, onClose, onDelete, onSubmit }: { mode: 'create' | 'edit'; form: SharedScheduleInput; saving: boolean; error: string; onChange: (form: SharedScheduleInput) => void; onClose: () => void; onDelete?: () => void; onSubmit: (event: FormEvent<HTMLFormElement>) => void }) {
  const dateError = Boolean(form.startDate && form.endDate && form.endDate < form.startDate)
  return <div className="modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget && !saving) onClose() }}><section className="modal shared-schedule-modal" role="dialog" aria-modal="true" aria-labelledby="shared-schedule-modal-title" onMouseDown={(event) => event.stopPropagation()}>
    <div className="modal-header"><div><span className="page-eyebrow">ORGANIZATION SCHEDULE</span><h2 id="shared-schedule-modal-title">{mode === 'edit' ? '共有スケジュールを編集' : '共有スケジュールを追加'}</h2></div><button className="modal-close" type="button" aria-label="共有スケジュール入力を閉じる" disabled={saving} onClick={onClose}><X size={19} /></button></div>
    <form className="modal-form" onSubmit={onSubmit}>
      <p className="modal-description"><CalendarDays size={16} />登録した予定は組織内の管理者・従業員に共有されます。</p>
      <div className="form-grid shared-schedule-form-grid">
        <label className="form-field shared-schedule-title-field"><span>予定名<em>必須</em></span><input autoFocus required maxLength={100} value={form.title} onChange={(event) => onChange({ ...form, title: event.target.value })} placeholder="例：月次ミーティング" /></label>
        <label className="form-field"><span>開始日<em>必須</em></span><input required type="date" value={form.startDate} onChange={(event) => onChange({ ...form, startDate: event.target.value })} /></label>
        <label className="form-field"><span>終了日<em>必須</em></span><input required type="date" value={form.endDate} min={form.startDate || undefined} onChange={(event) => onChange({ ...form, endDate: event.target.value })} /></label>
        <label className="form-field shared-schedule-detail-field"><span>詳細</span><textarea maxLength={2000} value={form.detail} onChange={(event) => onChange({ ...form, detail: event.target.value })} placeholder="参加者、場所、目的など" /></label>
      </div>
      {dateError && <p className="shared-schedule-error" role="alert">終了日は開始日以降の日付を選択してください。</p>}
      {error && <p className="shared-schedule-error" role="alert">{error}</p>}
      <div className="modal-footer">{mode === 'edit' && onDelete && <button className="button button-danger shared-schedule-delete-button" type="button" disabled={saving} onClick={onDelete}><Trash2 size={15} />削除</button>}<button className="button button-secondary" type="button" disabled={saving} onClick={onClose}>キャンセル</button><button className="button button-primary" type="submit" disabled={saving || dateError}>{saving ? mode === 'edit' ? '更新中…' : '登録中…' : mode === 'edit' ? '変更を保存' : '共有予定を登録'}</button></div>
    </form>
  </section></div>
}

function groupEventsByDate(events: DashboardCalendarEvent[]) {
  const grouped = new Map<string, DashboardCalendarEvent[]>()
  for (const event of events) {
    const start = parseDateKey(event.date)
    const end = parseDateKey(event.endDate) ?? start
    if (!start || !end) continue
    const safeEnd = end.getTime() >= start.getTime() ? end : start
    for (let date = start; date.getTime() <= safeEnd.getTime(); date = addDays(date, 1)) {
      const dateKey = toDateKey(date)
      const dateEvents = grouped.get(dateKey) ?? []
      dateEvents.push(event)
      grouped.set(dateKey, dateEvents)
    }
  }
  return grouped
}

function buildCalendarWeeks(viewDate: Date) {
  const firstDay = new Date(viewDate.getFullYear(), viewDate.getMonth(), 1)
  const firstCell = new Date(viewDate.getFullYear(), viewDate.getMonth(), 1 - firstDay.getDay())
  const days = Array.from({ length: 42 }, (_, index) => new Date(firstCell.getFullYear(), firstCell.getMonth(), firstCell.getDate() + index))
  return Array.from({ length: 6 }, (_, index) => days.slice(index * 7, index * 7 + 7))
}

function buildRangeDataByWeek(events: DashboardCalendarEvent[], weeks: Date[][]): CalendarWeekRangeData[] {
  return weeks.map((week) => {
    const weekStart = week[0]
    const weekEnd = week[week.length - 1]
    const candidates = events.flatMap((event) => {
      if (!isRangeEvent(event)) return []
      const eventStart = parseDateKey(event.date)
      const eventEnd = parseDateKey(event.endDate)
      if (!eventStart || !eventEnd || eventEnd.getTime() < weekStart.getTime() || eventStart.getTime() > weekEnd.getTime()) return []
      const segmentStart = eventStart.getTime() > weekStart.getTime() ? eventStart : weekStart
      const segmentEnd = eventEnd.getTime() < weekEnd.getTime() ? eventEnd : weekEnd
      return [{ event, startColumn: differenceInDays(weekStart, segmentStart) + 1, endColumn: differenceInDays(weekStart, segmentEnd) + 1, lane: 0, startsAtEvent: segmentStart.getTime() === eventStart.getTime(), endsAtEvent: segmentEnd.getTime() === eventEnd.getTime() }]
    }).sort((left, right) => left.startColumn - right.startColumn || right.endColumn - left.endColumn)
    const laneEnds: number[] = []
    for (const segment of candidates) {
      const availableLane = laneEnds.findIndex((endColumn) => endColumn < segment.startColumn)
      segment.lane = availableLane >= 0 ? availableLane : laneEnds.length
      laneEnds[segment.lane] = segment.endColumn
    }
    return { segments: candidates, laneCount: laneEnds.length }
  })
}

function getRangeLaneCountForDay(rangeData: CalendarWeekRangeData, dayIndex: number): number {
  const dayColumn = dayIndex + 1
  const lanes = new Set<number>()
  for (const segment of rangeData.segments) {
    if (segment.startColumn <= dayColumn && segment.endColumn >= dayColumn) {
      lanes.add(segment.lane)
    }
  }
  return lanes.size
}

function eventIntersectsMonth(event: DashboardCalendarEvent, viewDate: Date) {
  const start = parseDateKey(event.date)
  const end = parseDateKey(event.endDate) ?? start
  if (!start || !end) return false
  const monthStart = startOfMonth(viewDate)
  const monthEnd = new Date(viewDate.getFullYear(), viewDate.getMonth() + 1, 0)
  return start.getTime() <= monthEnd.getTime() && end.getTime() >= monthStart.getTime()
}

function isRangeEvent(event: DashboardCalendarEvent) {
  const start = normalizeDateKey(event.date)
  const end = normalizeDateKey(event.endDate)
  return Boolean(start && end && start !== end)
}

function addDays(date: Date, days: number) {
  return new Date(date.getFullYear(), date.getMonth(), date.getDate() + days)
}

function differenceInDays(from: Date, to: Date) {
  return Math.round((to.getTime() - from.getTime()) / 86_400_000)
}

function startOfMonth(date: Date) {
  return new Date(date.getFullYear(), date.getMonth(), 1)
}

function isSameMonth(left: Date, right: Date) {
  return left.getFullYear() === right.getFullYear() && left.getMonth() === right.getMonth()
}

function todayDateKey() {
  return toDateKey(new Date())
}

function toDateKey(date: Date) {
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`
}

function parseDateKey(value: string | null | undefined) {
  const normalized = normalizeDateKey(value)
  if (!normalized) return null
  const [year, month, day] = normalized.split('-').map(Number)
  const date = new Date(year, month - 1, day)
  return date.getFullYear() === year && date.getMonth() === month - 1 && date.getDate() === day ? date : null
}

function normalizeDateKey(value: string | null | undefined) {
  if (!value) return null
  const normalized = value.slice(0, 10).replaceAll('/', '-')
  return /^\d{4}-\d{2}-\d{2}$/.test(normalized) ? normalized : null
}

function emptySharedScheduleForm(date: string): SharedScheduleInput {
  return { title: '', startDate: date, endDate: date, detail: '' }
}

function calendarEventLabel(event: DashboardCalendarEvent) {
  return event.category === 'shared' ? '共有' : event.categoryLabel
}

function formatMonth(date: Date) {
  return `${date.getFullYear()}年${date.getMonth() + 1}月`
}

function formatFullDate(date: Date) {
  return `${date.getFullYear()}年${date.getMonth() + 1}月${date.getDate()}日（${weekdayLabels[date.getDay()]}）`
}

function formatYen(amount: number) {
  return `¥${new Intl.NumberFormat('ja-JP').format(Math.round(amount))}`
}
