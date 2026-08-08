import { useRef } from 'react'
import { CalendarDays } from 'lucide-react'
import { toNativeDateValue } from './dateInput'

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
