export type DocumentFilterTone = 'all' | 'estimate' | 'invoice' | 'draft' | 'pending' | 'completed' | 'inspection' | 'bodywork' | 'general'

export type DocumentFilterOption<Value extends string> = {
  value: Value
  label: string
  tone: DocumentFilterTone
}

export function DocumentFilterGroup<Value extends string>({ label, value, options, onChange }: { label: string; value: Value; options: DocumentFilterOption<Value>[]; onChange: (value: Value) => void }) {
  return <div className="document-filter-group"><span className="document-filter-label">{label}</span><div className="document-filter-options" role="group" aria-label={label}>{options.map((option) => <button className={`document-filter-option document-filter-option-${option.tone}${value === option.value ? ' is-active' : ''}`} key={option.value} type="button" aria-pressed={value === option.value} onClick={() => onChange(option.value)}>{option.label}</button>)}</div></div>
}
