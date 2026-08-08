import { useRef } from 'react'
import { CalendarDays } from 'lucide-react'

type DateCalendarButtonProps = {
  ariaLabel: string
  value: string
  onChange: (value: string) => void
}

export function DateCalendarButton({ ariaLabel, value, onChange }: DateCalendarButtonProps) {
  const pickerRef = useRef<HTMLInputElement>(null)

  function openPicker() {
    const picker = pickerRef.current
    if (!picker) return
    const pickerWithShowPicker = picker as HTMLInputElement & { showPicker?: () => void }
    if (typeof pickerWithShowPicker.showPicker === 'function') {
      try {
        pickerWithShowPicker.showPicker()
        return
      } catch {
        // showPicker is not available in every browser context; click is the fallback.
      }
    }
    picker.click()
  }

  return <span className="date-calendar-picker">
    <button className="date-calendar-picker-button" type="button" aria-label={`${ariaLabel}をカレンダーから選択`} onClick={openPicker}><CalendarDays size={14} aria-hidden="true" /></button>
    <input ref={pickerRef} className="date-calendar-picker-native" type="date" tabIndex={-1} aria-hidden="true" value={toNativeDateValue(value)} onChange={(event) => onChange(event.target.value.replaceAll('-', '/'))} />
  </span>
}

function toNativeDateValue(value: string) {
  const match = value.trim().match(/^(\d{4})[-/](\d{1,2})[-/](\d{1,2})$/)
  if (!match) return ''
  const month = Number(match[2])
  const day = Number(match[3])
  if (month < 1 || month > 12 || day < 1 || day > 31) return ''
  return `${match[1]}-${match[2].padStart(2, '0')}-${match[3].padStart(2, '0')}`
}
