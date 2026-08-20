import { useEffect, useRef, useState, type ChangeEvent, type CSSProperties } from 'react'
import { ChevronDown, Plus, Trash2 } from 'lucide-react'
import { normalizeDisplacement, normalizeMileage, normalizeModelYear, normalizePhone, normalizePostalCode, type NormalizableField } from '@vehicle-management/shared'
import type {
  MaintenanceDocumentLike,
  MaintenanceDocumentDetails,
  MaintenanceFeeKey,
  MaintenanceLineItem,
} from '../lib/maintenanceApi'
import { maintenanceStatementHeight, maintenanceStatementWidth } from '../lib/maintenanceStatement'
import { DateCalendarButton } from './DateCalendarButton'
import { toNativeDateValue } from './dateInput'
import { sanitizeNormalizedDraft, toEditableNormalizedValue } from './normalizedInput'

export type MaintenanceStatementItemField = 'kind' | 'description' | 'quantity' | 'unit' | 'unitPrice' | 'technicalFee' | 'summary'
export type MaintenanceStatementHeaderField = 'number' | 'type' | 'status' | 'category' | 'customerId' | 'vehicleId' | 'intakeDate' | 'plannedReleaseDate' | 'issuedAt' | 'dueDate' | 'note'

type Props = {
  document: MaintenanceDocumentLike
  itemPresets: string[]
  onUpdateHeader: (field: MaintenanceStatementHeaderField, value: string) => void
  onUpdateDetails: (details: MaintenanceDocumentDetails) => void
  onUpdateItem: (itemId: string, field: MaintenanceStatementItemField, value: string) => void
  onRemoveItem: (itemId: string) => void
  onUpdateFee: (key: MaintenanceFeeKey, value: string) => void
  onAddItem: () => void
}

export function MaintenanceStatementEditor({ document, itemPresets, onUpdateHeader, onUpdateDetails, onUpdateItem, onRemoveItem, onUpdateFee, onAddItem }: Props) {
  const details = document.details
  const customer = {
    ...document.customerDetails,
    ...(details.customerOverride ?? {}),
    birthDate: details.customerBirthDate || details.customerOverride?.birthDate || document.customerDetails.birthDate || '',
    employer: details.customerEmployer || details.customerOverride?.employer || document.customerDetails.employer || '',
  }
  const vehicle = details.vehicleOverride ?? document.vehicleDetails ?? emptyVehicle

  function updateDetails(patch: Partial<MaintenanceDocumentDetails>) {
    onUpdateDetails({ ...details, ...patch })
  }

  function updateCustomer(field: keyof NonNullable<MaintenanceDocumentDetails['customerOverride']>, value: string) {
    updateDetails({
      customerOverride: { ...customer, [field]: value },
      ...(field === 'birthDate' ? { customerBirthDate: value } : {}),
      ...(field === 'employer' ? { customerEmployer: value } : {}),
    })
  }

  function updateVehicle(field: keyof NonNullable<MaintenanceDocumentDetails['vehicleOverride']>, value: string | boolean) {
    updateDetails({ vehicleOverride: { ...vehicle, [field]: value } })
  }

  return <div className="maintenance-statement-editor" aria-label="整備帳票のプレビュー編集">
    <button className="maintenance-statement-add" type="button" aria-label="作業内容・部品明細を追加" style={controlStyle(942, 516, 132, 28)} disabled={document.items.length >= 18} onClick={onAddItem}><Plus size={12} aria-hidden="true" />明細を追加</button>

    <StatementTextControl ariaLabel="書類日付" value={document.issuedAt} className="is-document-number" x={611} y={44} width={118} height={32} centered onChange={(value) => onUpdateHeader('issuedAt', value)} />
    <StatementTextControl ariaLabel="担当" value={details.staffName} className="is-document-number" x={729} y={44} width={118} height={32} centered onChange={(value) => updateDetails({ staffName: value })} />
    <StatementTextControl ariaLabel="請求番号" value={document.number} className="is-document-number" x={847} y={44} width={118} height={32} centered readOnly={!document.id} onChange={(value) => onUpdateHeader('number', value)} />

    <StatementTextControl ariaLabel="顧客名" value={customer.name} x={140} y={101} width={320} height={38} className="is-large" onChange={(value) => updateCustomer('name', value)} />
    <StatementTextControl ariaLabel="顧客ふりがな" value={customer.kana} x={140} y={139} width={320} height={25} onChange={(value) => updateCustomer('kana', value)} />
    <StatementTextControl ariaLabel="顧客敬称" value={details.customerHonorific} x={462} y={112} width={54} height={38} centered className="is-large" onChange={(value) => updateDetails({ customerHonorific: value })} />
    <StatementTextControl normalization="postalCode" ariaLabel="郵便番号" displayPrefix="〒" value={customer.postalCode} x={140} y={181} width={200} height={28} normalizeOnBlur={normalizePostalCode} onChange={(value) => updateCustomer('postalCode', value)} />
    <StatementTextControl ariaLabel="顧客住所" value={customer.address} x={140} y={211} width={370} height={41} onChange={(value) => updateCustomer('address', value)} />
    <StatementTextControl calendar ariaLabel="生年月日" value={customer.birthDate} x={650} y={95} width={155} height={30} className="is-contact-value" normalizeOnBlur={normalizeMaintenanceCustomerBirthDateOnBlur} onChange={(value) => updateCustomer('birthDate', value)} />
    <StatementTextControl normalization="phone" ariaLabel="顧客電話番号" value={customer.phone} x={650} y={137} width={155} height={30} className="is-contact-value" normalizeOnBlur={normalizePhone} onChange={(value) => updateCustomer('phone', value)} />
    <StatementTextControl ariaLabel="勤務先等" value={customer.employer} x={650} y={179} width={155} height={30} className="is-contact-value" onChange={(value) => updateCustomer('employer', value)} />
    <StatementTextControl normalization="phone" ariaLabel="連絡先電話番号" value={details.customerContactPhone} x={650} y={221} width={155} height={30} className="is-contact-value" normalizeOnBlur={normalizePhone} onChange={(value) => updateDetails({ customerContactPhone: value })} />

    <VehicleEditor vehicle={vehicle} onUpdate={updateVehicle} />
    <StatementTextControl calendar ariaLabel="入庫日" value={document.intakeDate} x={916} y={443} width={83} height={35} centered className="is-compact-date" onChange={(value) => onUpdateHeader('intakeDate', value)} />
    <StatementTextControl calendar ariaLabel="出庫予定日" value={document.plannedReleaseDate || document.completionDate} x={999} y={443} width={83} height={35} centered className="is-compact-date" onChange={(value) => onUpdateHeader('plannedReleaseDate', value)} />

    {document.items.slice(0, 18).map((item, index) => <LineEditor key={item.id} item={item} index={index} itemPresets={itemPresets} onUpdateItem={onUpdateItem} onRemoveItem={onRemoveItem} />)}

    <StatementNumberControl className="is-compact-value" ariaLabel="自賠責" value={document.fees.自賠責} x={335} y={1183} width={87} height={35} centered onCommit={(value) => onUpdateFee('自賠責', String(value))} />
    <StatementNumberControl className="is-compact-value" ariaLabel="重量税" value={document.fees.重量税} x={422} y={1183} width={87} height={35} centered onCommit={(value) => onUpdateFee('重量税', String(value))} />
    <StatementNumberControl className="is-compact-value" ariaLabel="印紙代" value={document.fees.印紙代} x={509} y={1183} width={87} height={35} centered onCommit={(value) => onUpdateFee('印紙代', String(value))} />
    <StatementNumberControl className="is-compact-value" ariaLabel="その他費用" value={document.fees.リサイクル料金} x={596} y={1183} width={87} height={35} centered onCommit={(value) => onUpdateFee('リサイクル料金', String(value))} />
    <StatementNumberControl className="is-compact-value" ariaLabel="調整額" value={document.adjustment} x={683} y={1183} width={87} height={35} centered onCommit={(value) => onUpdateFee('調整額', String(value))} />

  </div>
}

function VehicleEditor({ vehicle, onUpdate }: { vehicle: NonNullable<MaintenanceDocumentDetails['vehicleOverride']>; onUpdate: (field: keyof NonNullable<MaintenanceDocumentDetails['vehicleOverride']>, value: string | boolean) => void }) {
  const fields: Array<{ field: keyof NonNullable<MaintenanceDocumentDetails['vehicleOverride']>; x: number; y: number; width: number; height: number; centered?: boolean }> = [
    { field: 'maker', x: 16, y: 367, width: 99, height: 38, centered: true },
    { field: 'name', x: 115, y: 367, width: 236, height: 38, centered: true },
    { field: 'year', x: 351, y: 367, width: 104, height: 38, centered: true },
    { field: 'displacement', x: 455, y: 367, width: 113, height: 38, centered: true },
    { field: 'transmission', x: 568, y: 367, width: 115, height: 38, centered: true },
    { field: 'color', x: 683, y: 367, width: 127, height: 38, centered: true },
    { field: 'modelType', x: 16, y: 447, width: 124, height: 47, centered: true },
    { field: 'vin', x: 140, y: 447, width: 210, height: 47, centered: true },
    { field: 'plate', x: 350, y: 447, width: 180, height: 47, centered: true },
    { field: 'mileage', x: 530, y: 447, width: 140, height: 47, centered: true },
    { field: 'inspectionDate', x: 670, y: 447, width: 140, height: 47, centered: true },
  ]
  return <>
    {fields.map(({ field, ...position }) => <StatementTextControl key={field} calendar={field === 'inspectionDate'} calendarControlClassName={field === 'inspectionDate' ? 'is-vehicle-inspection-date' : undefined} normalization={field === 'year' ? 'modelYear' : field === 'displacement' ? 'displacement' : field === 'mileage' ? 'mileage' : undefined} className="is-compact-value" ariaLabel={`車両${field}`} value={String(vehicle[field] ?? '')} {...position} normalizeOnBlur={field === 'year' ? normalizeModelYear : field === 'displacement' ? normalizeDisplacement : field === 'mileage' ? normalizeMileage : undefined} onChange={(value) => onUpdate(field, value)} />)}
  </>
}

function LineEditor({ item, index, itemPresets, onUpdateItem, onRemoveItem }: { item: MaintenanceLineItem; index: number; itemPresets: string[]; onUpdateItem: Props['onUpdateItem']; onRemoveItem: Props['onRemoveItem'] }) {
  const y = 587 + index * 28
  const imported = item.abacusDetail
  const description = imported ? imported.description ?? '' : item.description
  const quantity = imported ? imported.quantity : item.quantity
  const unit = imported ? imported.unit ?? '' : item.unit
  const unitPrice = imported ? imported.unitPrice : item.unitPrice
  const technicalFee = imported ? imported.technicalFees : item.technicalFee
  const summary = imported ? imported.summary ?? '' : item.summary
  return <>
    <StatementNameCombobox value={description} candidates={itemPresets} ariaLabel={`明細${index + 1}の内容`} x={74} y={y} width={318} height={28} onCommit={(value) => onUpdateItem(item.id, 'description', value)} />
    <StatementNumberControl className="is-compact-value" ariaLabel={`明細${index + 1}の数量`} value={quantity} x={392} y={y} width={80} height={28} centered decimal onCommit={(value) => onUpdateItem(item.id, 'quantity', String(value))} />
    <StatementTextControl className="is-compact-value" ariaLabel={`明細${index + 1}の単位`} value={unit} x={472} y={y} width={84} height={28} centered onChange={(value) => onUpdateItem(item.id, 'unit', value)} />
    <StatementNumberControl className="is-compact-value" ariaLabel={`明細${index + 1}の部品単価`} value={unitPrice} x={556} y={y} width={113} height={28} onCommit={(value) => onUpdateItem(item.id, 'unitPrice', String(value))} />
    <StatementNumberControl className="is-compact-value" ariaLabel={`明細${index + 1}の技術料`} value={technicalFee} x={785} y={y} width={166} height={28} onCommit={(value) => onUpdateItem(item.id, 'technicalFee', String(value))} />
    <StatementTextControl className="is-item-text is-compact-value" ariaLabel={`明細${index + 1}の摘要`} value={summary} x={951} y={y} width={132} height={28} onChange={(value) => onUpdateItem(item.id, 'summary', value)} />
    <button className="maintenance-statement-remove" type="button" aria-label={`明細${index + 1}を削除`} style={controlStyle(1085, y + 3, 31, 22)} onClick={() => onRemoveItem(item.id)}><Trash2 size={13} /></button>
  </>
}

function StatementNameCombobox({ value, candidates, ariaLabel, x, y, width, height, onCommit }: { value: string; candidates: string[]; ariaLabel: string; x: number; y: number; width: number; height: number; onCommit: (value: string) => void }) {
  const [draft, setDraft] = useState(value)
  const [open, setOpen] = useState(false)
  const rootRef = useRef<HTMLDivElement>(null)
  const uniqueCandidates = Array.from(new Set(candidates.filter(Boolean)))

  useEffect(() => setDraft(value), [value])
  useEffect(() => {
    if (!open) return
    function close(event: PointerEvent) {
      if (!rootRef.current?.contains(event.target as Node)) setOpen(false)
    }
    document.addEventListener('pointerdown', close)
    return () => document.removeEventListener('pointerdown', close)
  }, [open])

  function commit(nextValue = draft) {
    setDraft(nextValue)
    setOpen(false)
    if (nextValue !== value) onCommit(nextValue)
  }

  return <div ref={rootRef} className="maintenance-statement-combobox" style={controlStyle(x, y, width, height)}>
    <input aria-label={ariaLabel} value={draft} onChange={(event) => setDraft(event.target.value)} onFocus={() => setOpen(false)} onBlur={() => { if (draft !== value) onCommit(draft) }} />
    <button type="button" aria-label={`${ariaLabel}の候補を表示`} aria-expanded={open} onMouseDown={(event) => event.preventDefault()} onClick={() => setOpen((current) => !current)}><ChevronDown size={12} /></button>
    {open && <div className={`maintenance-statement-candidates${y > 900 ? ' is-up' : ''}`} role="listbox">
      {uniqueCandidates.map((candidate) => <button key={candidate} type="button" role="option" aria-selected={candidate === draft} onMouseDown={(event) => event.preventDefault()} onClick={() => commit(candidate)}>{candidate}</button>)}
      {!uniqueCandidates.length && <span>候補なし</span>}
    </div>}
  </div>
}

function StatementTextControl({ ariaLabel, value, x, y, width, height, onChange, centered = false, className = '', calendarControlClassName = '', normalization, readOnly = false, displayPrefix = '', normalizeOnBlur, calendar = false }: { ariaLabel: string; value: string; x: number; y: number; width: number; height: number; onChange: (value: string) => void; centered?: boolean; className?: string; calendarControlClassName?: string; normalization?: NormalizableField; readOnly?: boolean; displayPrefix?: string; normalizeOnBlur?: (value: string) => string; calendar?: boolean }) {
  const [draft, setDraft] = useState(() => normalization ? toEditableNormalizedValue(normalization, value) : value)
  const [focused, setFocused] = useState(false)
  useEffect(() => {
    if (!focused) setDraft(normalization ? toEditableNormalizedValue(normalization, value) : value)
  }, [focused, normalization, value])

  const editableValue = normalization && focused ? draft : value
  const displayValue = editableValue ? `${displayPrefix}${editableValue}` : editableValue
  function handleChange(nextValue: string) {
    const withoutPrefix = displayPrefix && nextValue.startsWith(displayPrefix) ? nextValue.slice(displayPrefix.length) : nextValue
    if (!normalization) {
      onChange(withoutPrefix)
      return
    }
    const sanitized = sanitizeNormalizedDraft(normalization, withoutPrefix)
    if (sanitized === null) return
    setDraft(sanitized)
  }
  function beginEdit() {
    if (!normalization) return
    setFocused(true)
    setDraft(toEditableNormalizedValue(normalization, value))
  }
  function finish() {
    setFocused(false)
    if (!normalizeOnBlur) return
    if (!normalization) {
      const normalized = normalizeOnBlur(value)
      if (normalized !== value) onChange(normalized)
      return
    }
    const normalized = normalizeOnBlur(draft)
    if (normalized !== value) onChange(normalized)
  }
  const inputClassName = `maintenance-statement-control${centered ? ' is-centered' : ''}${className ? ` ${className}` : ''}`
  const inputProps = { 'aria-label': ariaLabel, className: inputClassName, value: displayValue, readOnly, onFocus: normalization ? beginEdit : undefined, onChange: (event: ChangeEvent<HTMLInputElement>) => handleChange(event.target.value), onBlur: finish }
  if (!calendar) return <input {...inputProps} style={controlStyle(x, y, width, height)} />
  return <div className={`maintenance-statement-calendar-control${calendarControlClassName ? ` ${calendarControlClassName}` : ''}`} style={controlStyle(x, y, width, height)}>
    <input {...inputProps} type="date" value={toNativeDateValue(value)} onChange={(event) => onChange(event.target.value.replaceAll('-', '/'))} style={{ position: 'relative', inset: 'auto', width: '100%', height: '100%' }} />
    <DateCalendarButton ariaLabel={ariaLabel} value={value} onChange={onChange} />
  </div>
}

function normalizeMaintenanceCustomerBirthDate(value: string | null | undefined) {
  const normalized = typeof value === 'string' ? value.trim() : ''
  return normalized === 'birth_date' ? '' : normalized
}

function normalizeMaintenanceCustomerBirthDateOnBlur(value: string) {
  return normalizeMaintenanceCustomerBirthDate(value).replaceAll('-', '/')
}

function StatementNumberControl({ ariaLabel, value, x, y, width, height, onCommit, centered = false, decimal = false, className = '' }: { ariaLabel: string; value: number | null; x: number; y: number; width: number; height: number; onCommit: (value: number) => void; centered?: boolean; decimal?: boolean; className?: string }) {
  const [draft, setDraft] = useState(value === null ? '' : String(value))
  const [focused, setFocused] = useState(false)
  useEffect(() => { if (!focused) setDraft(value === null ? '' : String(value)) }, [focused, value])

  function update(nextValue: string) {
    const pattern = decimal ? /^-?\d*(?:\.\d*)?$/ : /^-?\d*$/
    if (pattern.test(nextValue)) {
      setDraft(nextValue)
      if (nextValue !== '' && nextValue !== '-') onCommit(Number(nextValue))
    }
  }

  function finish() {
    if (draft === '' || draft === '-') {
      setDraft('0')
      onCommit(0)
    }
    setFocused(false)
  }

  const displayValue = focused || draft === '' || draft === '-' ? draft : formatStatementNumber(Number(draft))
  return <input aria-label={ariaLabel} className={`maintenance-statement-control is-number${centered ? ' is-centered' : ''}${className ? ` ${className}` : ''}`} inputMode={decimal ? 'decimal' : 'numeric'} value={displayValue} style={controlStyle(x, y, width, height)} onFocus={() => { setFocused(true); setDraft(value === null ? '' : String(value)) }} onChange={(event) => update(event.target.value.replaceAll(',', ''))} onBlur={finish} />
}

const statementNumberFormatter = new Intl.NumberFormat('ja-JP')

function formatStatementNumber(value: number) {
  return statementNumberFormatter.format(value)
}

function controlStyle(x: number, y: number, width: number, height: number): CSSProperties {
  return { left: `${x / maintenanceStatementWidth * 100}%`, top: `${y / maintenanceStatementHeight * 100}%`, width: `${width / maintenanceStatementWidth * 100}%`, height: `${height / maintenanceStatementHeight * 100}%` }
}

const emptyVehicle: NonNullable<MaintenanceDocumentDetails['vehicleOverride']> = {
  maker: '', name: '', modelType: '', plate: '', vin: '', year: '', inspectionDate: '', mileage: '', color: '', displacement: '', transmission: '', inspectionRecordAvailable: false,
}
