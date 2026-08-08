type OptionalDateFieldProps = {
  id: string
  label: string
  value: string
  defaultValue: string
  onChange: (value: string) => void
}

export function OptionalDateField({ id, label, value, defaultValue, onChange }: OptionalDateFieldProps) {
  const isEnabled = Boolean(value.trim())

  function handleModeChange(nextMode: string) {
    onChange(nextMode === 'enabled' ? value.trim() || defaultValue : '')
  }

  return <div className="form-field optional-date-field">
    <span className="optional-date-field-heading">
      <label htmlFor={`${id}-value`}>{label}</label>
      <select id={`${id}-mode`} className="optional-date-field-mode" aria-label={`${label}の入力有無`} value={isEnabled ? 'enabled' : 'disabled'} onChange={(event) => handleModeChange(event.target.value)}>
        <option value="disabled">入力しない</option>
        <option value="enabled">入力する</option>
      </select>
    </span>
    {isEnabled
      ? <input id={`${id}-value`} type="date" aria-label={label} value={value.replaceAll('/', '-')} onChange={(event) => onChange(event.target.value.replaceAll('-', '/'))} />
      : <input id={`${id}-value`} type="text" aria-label={`${label}（なし）`} value="なし" disabled />}
  </div>
}
