import { useEffect, useState, type InputHTMLAttributes } from 'react'
import { normalizeFieldValue, type NormalizableField } from '@vehicle-management/shared'
import { sanitizeNormalizedDraft, toEditableNormalizedValue } from './normalizedInput'

export function NormalizedInput({ field, value, onChange, ...inputProps }: Omit<InputHTMLAttributes<HTMLInputElement>, 'value' | 'onChange' | 'onFocus' | 'onBlur'> & { field: NormalizableField; value: string; onChange: (value: string) => void }) {
  const [draft, setDraft] = useState(() => toEditableNormalizedValue(field, value))
  const [focused, setFocused] = useState(false)

  useEffect(() => {
    if (!focused) setDraft(toEditableNormalizedValue(field, value))
  }, [field, focused, value])

  function beginEdit() {
    setFocused(true)
    setDraft(toEditableNormalizedValue(field, value))
  }

  function handleChange(nextValue: string) {
    const sanitized = sanitizeNormalizedDraft(field, nextValue)
    if (sanitized === null) return
    setDraft(sanitized)
    onChange(sanitized)
  }

  function finish() {
    const normalized = normalizeFieldValue(field, draft)
    setFocused(false)
    if (normalized !== value) onChange(normalized)
  }

  return <input {...inputProps} value={focused ? draft : value} onFocus={beginEdit} onChange={(event) => handleChange(event.target.value)} onBlur={finish} />
}
