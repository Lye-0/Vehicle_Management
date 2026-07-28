import { useMemo, useState } from 'react'
import { CalendarClock, CalendarDays, CarFront, ChevronLeft, ChevronRight, CircleDollarSign, ClipboardCheck, FileText } from 'lucide-react'
import type { DashboardCalendarEvent } from '../lib/dashboardApi'

const weekdayLabels = ['日', '月', '火', '水', '木', '金', '土']
const legendCategories: Array<{ category: DashboardCalendarEvent['category']; label: string }> = [
  { category: 'vehicle-inspection', label: '車検満了' },
  { category: 'inspection', label: '点検予定' },
  { category: 'maintenance', label: '整備' },
  { category: 'sales', label: '販売書類' },
  { category: 'payment-due', label: '支払期限' },
  { category: 'payment', label: '入金' },
]

export function DashboardCalendar({ events, loading }: { events: DashboardCalendarEvent[]; loading: boolean }) {
  const today = todayDateKey()
  const [viewDate, setViewDate] = useState(() => startOfMonth(parseDateKey(today) ?? new Date()))
  const [selectedDate, setSelectedDate] = useState(today)
  const eventsByDate = useMemo(() => groupEventsByDate(events), [events])
  const calendarDays = useMemo(() => buildCalendarDays(viewDate), [viewDate])
  const selectedEvents = eventsByDate.get(selectedDate) ?? []
  const monthEventCount = calendarDays.reduce((count, day) => count + (isSameMonth(day, viewDate) ? (eventsByDate.get(toDateKey(day))?.length ?? 0) : 0), 0)

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

  return (
    <section className="panel dashboard-calendar-panel" aria-labelledby="dashboard-calendar-title">
      <div className="dashboard-calendar-top">
        <div className="dashboard-calendar-heading">
          <div>
            <span className="calendar-heading-kicker"><CalendarDays size={15} />予定をまとめて確認</span>
            <h2 id="dashboard-calendar-title">業務カレンダー</h2>
            <p>車検・点検、販売・整備書類、支払期限と入金日を表示しています。</p>
          </div>
        </div>
        <div className="calendar-legend" aria-label="予定の種類">
          {legendCategories.map(({ category, label }) => <span key={category}><i className={`calendar-legend-dot calendar-event-${category}`} />{label}</span>)}
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
              {weekdayLabels.map((label, index) => <div className={`dashboard-calendar-weekday${index === 0 ? ' is-sunday' : ''}${index === 6 ? ' is-saturday' : ''}`} role="columnheader" key={label}>{label}</div>)}
              {calendarDays.map((day) => {
                const date = toDateKey(day)
                const dayEvents = eventsByDate.get(date) ?? []
                const isCurrentMonth = isSameMonth(day, viewDate)
                const isSelected = date === selectedDate
                const isToday = date === today
                const visibleEvents = dayEvents.slice(0, 3)
                const hiddenEventCount = dayEvents.length - visibleEvents.length
                return <button className={`dashboard-calendar-day${isCurrentMonth ? '' : ' is-outside'}${isSelected ? ' is-selected' : ''}${isToday ? ' is-today' : ''}`} type="button" role="gridcell" aria-label={`${formatFullDate(day)}、予定${dayEvents.length}件`} aria-pressed={isSelected} key={date} onClick={() => selectDate(day)}>
                  <span className="calendar-day-number"><span>{day.getDate()}</span>{isToday && <em>今日</em>}</span>
                  <span className="calendar-day-event-list">
                    {visibleEvents.map((event) => <span className={`calendar-event-chip calendar-event-${event.category}`} key={event.id} title={`${event.categoryLabel}：${event.title}`}><span>{event.categoryLabel}</span><strong>{event.title}</strong></span>)}
                    {hiddenEventCount > 0 && <span className="calendar-more-events">+{hiddenEventCount}件</span>}
                  </span>
                </button>
              })}
            </div>
          </div>
        </div>

        <aside className="dashboard-calendar-detail" aria-live="polite" aria-labelledby="calendar-detail-title">
          <div className="calendar-detail-heading">
            <span className="calendar-heading-kicker">SELECTED DATE</span>
            <h3 id="calendar-detail-title">{formatFullDate(parseDateKey(selectedDate) ?? new Date())}</h3>
            <span className="calendar-detail-count">{selectedEvents.length}件の予定</span>
          </div>
          {loading ? <div className="calendar-detail-empty"><CalendarDays size={24} /><strong>予定を読み込んでいます</strong><span>店舗データを集計しています。</span></div> : selectedEvents.length ? <div className="calendar-detail-list">{selectedEvents.map((event) => <CalendarEventDetail event={event} key={event.id} />)}</div> : <div className="calendar-detail-empty"><CalendarDays size={24} /><strong>この日の予定はありません</strong><span>別の日付を選択すると予定を確認できます。</span></div>}
        </aside>
      </div>
    </section>
  )
}

function CalendarEventDetail({ event }: { event: DashboardCalendarEvent }) {
  const Icon = event.category === 'vehicle-inspection' ? CarFront : event.category === 'inspection' ? CalendarClock : event.category === 'maintenance' ? ClipboardCheck : event.category === 'sales' ? FileText : CircleDollarSign
  return <article className={`calendar-detail-item calendar-event-${event.category}`}><div className="calendar-detail-item-header"><span className="calendar-detail-type"><Icon size={14} />{event.categoryLabel}</span>{event.status && <span className="calendar-detail-status">{event.status}</span>}</div><h4>{event.title}</h4><p>{event.detail}</p>{event.amount !== null && <strong className="calendar-detail-amount">{formatYen(event.amount)}</strong>}</article>
}

function groupEventsByDate(events: DashboardCalendarEvent[]) {
  const grouped = new Map<string, DashboardCalendarEvent[]>()
  for (const event of events) {
    const date = normalizeDateKey(event.date)
    if (!date) continue
    const dateEvents = grouped.get(date) ?? []
    dateEvents.push(event)
    grouped.set(date, dateEvents)
  }
  return grouped
}

function buildCalendarDays(viewDate: Date) {
  const firstDay = new Date(viewDate.getFullYear(), viewDate.getMonth(), 1)
  const firstCell = new Date(viewDate.getFullYear(), viewDate.getMonth(), 1 - firstDay.getDay())
  return Array.from({ length: 42 }, (_, index) => new Date(firstCell.getFullYear(), firstCell.getMonth(), firstCell.getDate() + index))
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

function parseDateKey(value: string) {
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

function formatMonth(date: Date) {
  return `${date.getFullYear()}年${date.getMonth() + 1}月`
}

function formatFullDate(date: Date) {
  return `${date.getFullYear()}年${date.getMonth() + 1}月${date.getDate()}日（${weekdayLabels[date.getDay()]}）`
}

function formatYen(amount: number) {
  return `¥${new Intl.NumberFormat('ja-JP').format(Math.round(amount))}`
}
