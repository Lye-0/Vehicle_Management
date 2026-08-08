type OptionalDateFieldProps = {
  id: string
  label: string
  value: string
  defaultValue: string
  onChange: (value: string) => void
}

export function OptionalDateField({ id, label, value, defaultValue, onChange }: OptionalDateFieldProps) {
  const isEnabled = Boolean(value.trim())

  function handleModeChange() {
    onChange(isEnabled ? '' : value.trim() || defaultValue)
  }

  return <div className="form-field optional-date-field">
    <span className="optional-date-field-heading">
      <label htmlFor={`${id}-value`}>{label}</label>
      <button id={`${id}-mode`} type="button" role="switch" aria-checked={isEnabled} className={`optional-date-field-toggle${isEnabled ? ' is-enabled' : ''}`} aria-label={`${label}の入力有無`} onClick={handleModeChange}>
        <span className="optional-date-field-toggle-track" aria-hidden="true"><span /></span>
        <span>{isEnabled ? '入力する' : '入力しない'}</span>
      </button>
    </span>
    {isEnabled
      ? <input id={`${id}-value`} type="date" aria-label={label} value={value.replaceAll('/', '-')} onChange={(event) => onChange(event.target.value.replaceAll('-', '/'))} />
      : <input id={`${id}-value`} type="text" aria-label={`${label}（なし）`} value="なし" disabled />}
  </div>
}
