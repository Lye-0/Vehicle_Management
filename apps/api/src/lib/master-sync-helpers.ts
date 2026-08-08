// 顧客・車両マスタ同期の共有ヘルパー
// sync-preview API と 販売・整備PATCH API の両方で使用

import {
  normalizeDisplacement,
  normalizeMileage,
  normalizeModelYear,
  normalizePhone,
  normalizePostalCode,
  normalizeValueForComparison,
  parseNormalizedInteger,
} from '@vehicle-management/shared'

// ----- 型定義 -----

export type CustomerSyncField = 'name' | 'nameKana' | 'phone' | 'postalCode' | 'address' | 'birthDate' | 'employer'

export type VehicleSyncField = 'maker' | 'name' | 'model' | 'registrationNumber'
  | 'chassisNumber' | 'modelYear' | 'inspectionDate' | 'bodyColor'
  | 'displacement' | 'transmission'

// ----- Allowlist -----

export const CUSTOMER_SYNC_ALLOWLIST = new Set<CustomerSyncField>([
  'name', 'nameKana', 'phone', 'postalCode', 'address', 'birthDate', 'employer',
])

export const VEHICLE_SYNC_ALLOWLIST = new Set<VehicleSyncField>([
  'maker', 'name', 'model', 'registrationNumber',
  'chassisNumber', 'modelYear', 'inspectionDate', 'bodyColor',
  'displacement', 'transmission',
])

// ----- 表示ラベル -----

export const CUSTOMER_FIELD_LABELS: Record<CustomerSyncField, string> = {
  name: '顧客名',
  nameKana: 'ふりがな',
  phone: '電話番号',
  postalCode: '郵便番号',
  address: '住所',
  birthDate: '生年月日',
  employer: '勤務先等',
}

export const VEHICLE_FIELD_LABELS: Record<VehicleSyncField, string> = {
  maker: 'メーカー',
  name: '車名',
  model: '型式',
  registrationNumber: '登録番号',
  chassisNumber: '車台番号',
  modelYear: '年式',
  inspectionDate: '車検日',
  bodyColor: '車体色',
  displacement: '排気量',
  transmission: 'ミッション',
}

// ----- 注意項目 -----

export const CUSTOMER_ATTENTION_FIELDS = new Set<CustomerSyncField>(['name'])
export const VEHICLE_ATTENTION_FIELDS = new Set<VehicleSyncField>(['chassisNumber', 'registrationNumber'])

// ----- overrideキー → syncフィールド名 の対応 -----
// 書類の customerOverride / vehicleOverride のキーから allowlist フィールド名への変換

export const CUSTOMER_OVERRIDE_TO_SYNC: Record<string, CustomerSyncField> = {
  name: 'name',
  kana: 'nameKana',
  phone: 'phone',
  postalCode: 'postalCode',
  address: 'address',
  birthDate: 'birthDate',
  employer: 'employer',
}

export const VEHICLE_OVERRIDE_TO_SYNC: Record<string, VehicleSyncField> = {
  maker: 'maker',
  name: 'name',
  modelType: 'model',
  plate: 'registrationNumber',
  vin: 'chassisNumber',
  year: 'modelYear',
  inspectionDate: 'inspectionDate',
  color: 'bodyColor',
  displacement: 'displacement',
  transmission: 'transmission',
}

// ----- syncフィールド名 → DBカラム名（生SQL用） -----

export const CUSTOMER_FIELD_TO_DB_COLUMN: Record<CustomerSyncField, string> = {
  name: 'name',
  nameKana: 'name_kana',
  phone: 'phone',
  postalCode: 'postal_code',
  address: 'address',
  birthDate: 'birth_date',
  employer: 'employer',
}

export const VEHICLE_FIELD_TO_DB_COLUMN: Record<VehicleSyncField, string> = {
  maker: 'maker',
  name: 'name',
  model: 'model',
  registrationNumber: 'registration_number',
  chassisNumber: 'chassis_number',
  modelYear: 'model_year',
  inspectionDate: 'inspection_date',
  bodyColor: 'body_color',
  displacement: 'displacement',
  transmission: 'transmission',
}

// ----- syncフィールド名 → Drizzleプロパティ名 -----

export const CUSTOMER_FIELD_TO_DRIZZLE: Record<CustomerSyncField, keyof {
  name: string; nameKana: string | null; phone: string | null; postalCode: string | null; address: string | null;
  birthDate: string | null; employer: string | null
}> = {
  name: 'name',
  nameKana: 'nameKana',
  phone: 'phone',
  postalCode: 'postalCode',
  address: 'address',
  birthDate: 'birthDate',
  employer: 'employer',
}

export const VEHICLE_FIELD_TO_DRIZZLE: Record<VehicleSyncField, keyof {
  maker: string | null; name: string; model: string | null; registrationNumber: string | null;
  chassisNumber: string | null; modelYear: number | null; inspectionDate: string | null;
  bodyColor: string | null; displacement: number | null; transmission: string | null
}> = {
  maker: 'maker',
  name: 'name',
  model: 'model',
  registrationNumber: 'registrationNumber',
  chassisNumber: 'chassisNumber',
  modelYear: 'modelYear',
  inspectionDate: 'inspectionDate',
  bodyColor: 'bodyColor',
  displacement: 'displacement',
  transmission: 'transmission',
}

// ----- 正規化ヘルパー -----

export function isBlankValue(value: unknown): boolean {
  if (value === null || value === undefined) return true
  if (typeof value === 'string') return value.trim().length === 0
  return false
}

export function normalizePhoneForDuplicate(value: string | null | undefined): string {
  if (!value) return ''
  return value.normalize('NFKC').replace(/[^0-9]/g, '')
}

export function normalizeRegistrationNumberForDuplicate(value: string | null | undefined): string {
  if (!value) return ''
  return value.normalize('NFKC').replace(/[\s\u3000\-－—─]/g, '').toUpperCase()
}

export function normalizeChassisNumberForDuplicate(value: string | null | undefined): string {
  if (!value) return ''
  return value.normalize('NFKC').replace(/[\s\u3000\-－—─]/g, '').toUpperCase()
}

export function normalizeEmailForDuplicate(value: string | null | undefined): string {
  if (!value) return ''
  return value.trim().toLowerCase()
}

// ----- override値の抽出 -----

export type CustomerOverrideValues = Partial<Record<CustomerSyncField, string>>
export type VehicleOverrideValues = Partial<Record<VehicleSyncField, string | number>>

export function extractCustomerFieldsFromOverride(
  override: Record<string, unknown> | null | undefined,
): CustomerOverrideValues {
  if (!override) return {}
  const result: CustomerOverrideValues = {}
  for (const [overrideKey, syncField] of Object.entries(CUSTOMER_OVERRIDE_TO_SYNC)) {
    const value = override[overrideKey]
    if (typeof value === 'string' && value.trim()) {
      const normalized = normalizeCustomerSyncValue(syncField, value)
      if (normalized) result[syncField] = normalized
    }
  }
  return result
}

export function extractVehicleFieldsFromOverride(
  override: Record<string, unknown> | null | undefined,
): VehicleOverrideValues {
  if (!override) return {}
  const result: VehicleOverrideValues = {}
  for (const [overrideKey, syncField] of Object.entries(VEHICLE_OVERRIDE_TO_SYNC)) {
    const value = override[overrideKey]
    if (typeof value === 'string' && value.trim()) {
      result[syncField] = normalizeVehicleSyncValue(syncField, value)
    } else if (typeof value === 'number' && Number.isFinite(value)) {
      result[syncField] = normalizeVehicleSyncValue(syncField, value)
    }
  }
  return result
}

function normalizeCustomerSyncValue(field: CustomerSyncField, value: string): string {
  if (field === 'phone') return normalizePhone(value)
  if (field === 'postalCode') return normalizePostalCode(value)
  if (field === 'birthDate') return normalizeCustomerBirthDateForStorage(value)
  if (field === 'employer') return normalizeCustomerEmployerValue(value)
  return value.normalize('NFKC').trim()
}

export function normalizeCustomerBirthDateForStorage(value: unknown): string {
  const normalized = typeof value === 'string' ? value.normalize('NFKC').trim().replaceAll('/', '-').slice(0, 50) : ''
  return normalized === 'birth_date' ? '' : normalized
}

function normalizeCustomerEmployerValue(value: string): string {
  const normalized = value.normalize('NFKC').trim()
  return normalized === 'employer' ? '' : normalized
}

function normalizeVehicleSyncValue(field: VehicleSyncField, value: string | number): string {
  if (field === 'modelYear') return normalizeModelYear(value)
  if (field === 'displacement') return normalizeDisplacement(value)
  return String(value).normalize('NFKC').trim()
}

// ----- allowlist検証 -----

export function validateSyncFields(
  fields: string[] | undefined,
  allowlist: Set<string>,
): string[] | null {
  if (!fields) return null
  const invalid = fields.filter((f) => !allowlist.has(f))
  return invalid.length > 0 ? invalid : null
}

// ----- 差分計算 -----

export type DiffItem<T extends string> = {
  field: T
  label: string
  currentValue: string
  documentValue: string
  isAttention: boolean
}

function formatMasterValue(value: string | number | null | undefined, field: string): string {
  if (value === null || value === undefined) return ''
  if (field === 'modelYear') return normalizeModelYear(value)
  if (field === 'displacement') return normalizeDisplacement(value)
  if (field === 'mileage') return normalizeMileage(value)
  if (field === 'phone') return normalizePhone(value)
  if (field === 'postalCode') return normalizePostalCode(value)
  if (field === 'birthDate') return normalizeCustomerBirthDateForStorage(value)
  if (field === 'employer') return normalizeCustomerEmployerValue(String(value))
  return String(value).normalize('NFKC').trim()
}

export function computeCustomerDiffs(
  currentMaster: {
    name: string
    nameKana: string | null
    phone: string | null
    postalCode: string | null
    address: string | null
    birthDate?: string | null
    employer?: string | null
  } | null | undefined,
  documentValues: CustomerOverrideValues,
): DiffItem<CustomerSyncField>[] {
  if (!currentMaster) return []
  const diffs: DiffItem<CustomerSyncField>[] = []
  for (const field of CUSTOMER_SYNC_ALLOWLIST) {
    const current = currentMaster[CUSTOMER_FIELD_TO_DRIZZLE[field]] ?? null
    const doc = documentValues[field] ?? ''
    if (!doc) continue
    const currentStr = formatMasterValue(current, field)
    const documentStr = formatMasterValue(doc, field)
    if (normalizeValueForComparison(field, currentStr) === normalizeValueForComparison(field, documentStr)) continue
    diffs.push({
      field,
      label: CUSTOMER_FIELD_LABELS[field],
      currentValue: currentStr,
      documentValue: documentStr,
      isAttention: CUSTOMER_ATTENTION_FIELDS.has(field),
    })
  }
  return diffs
}

export function computeVehicleDiffs(
  currentMaster: {
    maker: string | null
    name: string
    model: string | null
    registrationNumber: string | null
    chassisNumber: string | null
    modelYear: number | null
    inspectionDate: string | null
    bodyColor: string | null
    displacement: number | null
    transmission: string | null
  } | null | undefined,
  documentValues: VehicleOverrideValues,
): DiffItem<VehicleSyncField>[] {
  if (!currentMaster) return []
  const diffs: DiffItem<VehicleSyncField>[] = []
  for (const field of VEHICLE_SYNC_ALLOWLIST) {
    const current = currentMaster[VEHICLE_FIELD_TO_DRIZZLE[field]] ?? null
    const doc = documentValues[field]
    const docStr = doc === undefined ? '' : formatMasterValue(doc, field)
    if (!docStr) continue
    const currentStr = formatMasterValue(current, field)
    if (normalizeValueForComparison(field, currentStr) === normalizeValueForComparison(field, docStr)) continue
    diffs.push({
      field,
      label: VEHICLE_FIELD_LABELS[field],
      currentValue: currentStr,
      documentValue: docStr,
      isAttention: VEHICLE_ATTENTION_FIELDS.has(field),
    })
  }
  return diffs
}

// ----- 最終保存時のUPDATE値生成 -----

export function buildCustomerUpdateValues(
  selectedFields: CustomerSyncField[],
  override: Record<string, unknown> | null | undefined,
): Record<string, string> {
  const extracted = extractCustomerFieldsFromOverride(override)
  const values: Record<string, string> = {}
  for (const field of selectedFields) {
    if (!CUSTOMER_SYNC_ALLOWLIST.has(field)) continue
    const value = extracted[field]
    if (value !== undefined && !isBlankValue(value)) {
      values[CUSTOMER_FIELD_TO_DB_COLUMN[field]] = value
    }
  }
  return values
}

export function buildVehicleUpdateValues(
  selectedFields: VehicleSyncField[],
  override: Record<string, unknown> | null | undefined,
): Record<string, string | number> {
  const extracted = extractVehicleFieldsFromOverride(override)
  const values: Record<string, string | number> = {}
  for (const field of selectedFields) {
    if (!VEHICLE_SYNC_ALLOWLIST.has(field)) continue
    const value = extracted[field]
    if (value !== undefined && !isBlankValue(value)) {
      if (field === 'modelYear' || field === 'displacement') {
        const numericValue = parseNormalizedInteger(value)
        if (numericValue !== null) values[VEHICLE_FIELD_TO_DB_COLUMN[field]] = numericValue
      } else {
        values[VEHICLE_FIELD_TO_DB_COLUMN[field]] = String(value)
      }
    }
  }
  return values
}

// ----- UPDATE文のSET句生成 -----

export function buildUpdateSetClause(
  fields: readonly string[],
  columnMap: Record<string, string>,
): string {
  return fields.map((f) => `${columnMap[f]} = ?`).join(', ')
}

// ----- 重複検出 -----

export type DuplicateCustomer = {
  id: string
  name: string
  phone: string | null
  email: string | null
  matchReason: 'phone' | 'email'
  strength: 'strong'
}

export type DuplicateVehicle = {
  id: string
  maker: string | null
  name: string
  registrationNumber: string | null
  chassisNumber: string | null
  matchReason: 'chassis_number' | 'registration_number'
  strength: 'strong'
}

export type NewCustomerInput = {
  name: string
  nameKana?: string
  phone?: string
  email?: string
  postalCode?: string
  address?: string
  birthDate?: string
  employer?: string
}

export type NewVehicleInput = {
  maker: string
  name: string
  model?: string
  registrationNumber?: string
  chassisNumber?: string
  modelYear?: number
  inspectionDate?: string
  mileage?: number
  bodyColor?: string
  displacement?: number
  transmission?: string
}

export function findDuplicateCustomers(
  allCustomers: Array<{
    id: string
    name: string
    phone: string | null
    email: string | null
  }>,
  newCustomer: NewCustomerInput,
  excludeCustomerId?: string,
): DuplicateCustomer[] {
  const results: DuplicateCustomer[] = []
  const seen = new Set<string>()

  for (const c of allCustomers) {
    if (excludeCustomerId && c.id === excludeCustomerId) continue

    const phoneMatch = newCustomer.phone
      && normalizePhoneForDuplicate(c.phone) === normalizePhoneForDuplicate(newCustomer.phone)
      && normalizePhoneForDuplicate(newCustomer.phone) !== ''
    const emailMatch = newCustomer.email
      && normalizeEmailForDuplicate(c.email) === normalizeEmailForDuplicate(newCustomer.email)
      && normalizeEmailForDuplicate(newCustomer.email) !== ''

    if (phoneMatch && !seen.has(c.id)) {
      results.push({ id: c.id, name: c.name, phone: c.phone, email: c.email, matchReason: 'phone', strength: 'strong' })
      seen.add(c.id)
    } else if (emailMatch && !seen.has(c.id)) {
      results.push({ id: c.id, name: c.name, phone: c.phone, email: c.email, matchReason: 'email', strength: 'strong' })
      seen.add(c.id)
    }
  }
  return results
}

export function findDuplicateVehicles(
  allVehicles: Array<{
    id: string
    maker: string | null
    name: string
    registrationNumber: string | null
    chassisNumber: string | null
  }>,
  newVehicle: NewVehicleInput,
  excludeVehicleId?: string,
): DuplicateVehicle[] {
  const results: DuplicateVehicle[] = []
  const seen = new Set<string>()

  for (const v of allVehicles) {
    if (excludeVehicleId && v.id === excludeVehicleId) continue

    const vinMatch = newVehicle.chassisNumber
      && normalizeChassisNumberForDuplicate(v.chassisNumber) === normalizeChassisNumberForDuplicate(newVehicle.chassisNumber)
      && normalizeChassisNumberForDuplicate(newVehicle.chassisNumber) !== ''
    const plateMatch = newVehicle.registrationNumber
      && normalizeRegistrationNumberForDuplicate(v.registrationNumber) === normalizeRegistrationNumberForDuplicate(newVehicle.registrationNumber)
      && normalizeRegistrationNumberForDuplicate(newVehicle.registrationNumber) !== ''

    if (vinMatch && !seen.has(v.id)) {
      results.push({ id: v.id, maker: v.maker, name: v.name, registrationNumber: v.registrationNumber, chassisNumber: v.chassisNumber, matchReason: 'chassis_number', strength: 'strong' })
      seen.add(v.id)
    } else if (plateMatch && !seen.has(v.id)) {
      results.push({ id: v.id, maker: v.maker, name: v.name, registrationNumber: v.registrationNumber, chassisNumber: v.chassisNumber, matchReason: 'registration_number', strength: 'strong' })
      seen.add(v.id)
    }
  }
  return results
}

// ----- 排他的入力検証 -----

export type CombinationValidation = {
  customerId?: string
  newCustomer?: NewCustomerInput
  vehicleId?: string
  newVehicle?: NewVehicleInput
  documentType: 'sales' | 'maintenance'
  /** 既存の車両なし販売書類を編集するための後方互換用。新規保存では指定しない。 */
  allowVehicleless?: boolean
}

export type CombinationError = {
  status: number
  message: string
}

export function validateCombination(input: CombinationValidation): CombinationError | null {
  const hasNewCustomer = Boolean(input.newCustomer)
  const hasExistingCustomer = Boolean(input.customerId)
  const hasNewVehicle = Boolean(input.newVehicle)
  const hasExistingVehicle = Boolean(input.vehicleId)

  if (hasNewCustomer && hasExistingCustomer) {
    return { status: 400, message: 'customerIdとnewCustomerは同時に指定できません。' }
  }
  if (!hasNewCustomer && !hasExistingCustomer) {
    return { status: 400, message: 'customerIdまたはnewCustomerのどちらかを指定してください。' }
  }

  if (hasNewVehicle && hasExistingVehicle) {
    return { status: 400, message: 'vehicleIdとnewVehicleは同時に指定できません。' }
  }

  if (hasNewCustomer && hasExistingVehicle) {
    return { status: 400, message: '新規顧客には新しい車両を登録してください。既存車両は選択できません。' }
  }

  if (!input.allowVehicleless && !hasNewVehicle && !hasExistingVehicle) {
    return {
      status: 400,
      message: input.documentType === 'maintenance'
        ? '整備書類ではvehicleIdまたはnewVehicleのどちらかを指定してください。'
        : '販売書類ではvehicleIdまたはnewVehicleのどちらかを指定してください。',
    }
  }

  return null
}

// ----- 最終保存時の実差分集合計算 -----
// sync-previewの結果を信用せず、保存API側で再計算するためのヘルパー

export function computeActualCustomerDiffFields(
  currentMaster: {
    name: string
    nameKana: string | null
    phone: string | null
    postalCode: string | null
    address: string | null
    birthDate?: string | null
    employer?: string | null
  } | null | undefined,
  override: Record<string, unknown> | null | undefined,
): Set<CustomerSyncField> {
  const docValues = extractCustomerFieldsFromOverride(override)
  const fields = new Set<CustomerSyncField>()
  if (!currentMaster) return fields
  for (const field of CUSTOMER_SYNC_ALLOWLIST) {
    const current = currentMaster[CUSTOMER_FIELD_TO_DRIZZLE[field]] ?? null
    const doc = docValues[field] ?? ''
    if (!doc) continue
    const currentStr = formatMasterValue(current, field)
    const documentStr = formatMasterValue(doc, field)
    if (normalizeValueForComparison(field, currentStr) === normalizeValueForComparison(field, documentStr)) continue
    fields.add(field)
  }
  return fields
}

export function computeActualVehicleDiffFields(
  currentMaster: {
    maker: string | null
    name: string
    model: string | null
    registrationNumber: string | null
    chassisNumber: string | null
    modelYear: number | null
    inspectionDate: string | null
    bodyColor: string | null
    displacement: number | null
    transmission: string | null
  } | null | undefined,
  override: Record<string, unknown> | null | undefined,
): Set<VehicleSyncField> {
  const docValues = extractVehicleFieldsFromOverride(override)
  const fields = new Set<VehicleSyncField>()
  if (!currentMaster) return fields
  for (const field of VEHICLE_SYNC_ALLOWLIST) {
    const current = currentMaster[VEHICLE_FIELD_TO_DRIZZLE[field]] ?? null
    const doc = docValues[field]
    const docStr = doc === undefined ? '' : formatMasterValue(doc, field)
    if (!docStr) continue
    const currentStr = formatMasterValue(current, field)
    if (normalizeValueForComparison(field, currentStr) === normalizeValueForComparison(field, docStr)) continue
    fields.add(field)
  }
  return fields
}

// ----- masterSync入力検証 -----

export type MasterSyncInput = {
  confirmed: boolean
  customerFields?: unknown
  vehicleFields?: unknown
  expectedCustomerUpdatedAt?: unknown
  expectedVehicleUpdatedAt?: unknown
}

export type MasterSyncValidationResult = {
  customerFields: CustomerSyncField[]
  vehicleFields: VehicleSyncField[]
  expectedCustomerUpdatedAt: string | undefined
  expectedVehicleUpdatedAt: string | undefined
}

export function validateMasterSyncInput(raw: unknown): MasterSyncValidationResult | { error: string } {
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) {
    return { error: 'masterSyncはオブジェクトである必要があります。' }
  }
  const obj = raw as Record<string, unknown>
  if (obj.confirmed !== true) {
    return { error: 'masterSync.confirmedはtrueである必要があります。' }
  }

  const customerFieldsRaw = obj.customerFields
  const vehicleFieldsRaw = obj.vehicleFields

  if (customerFieldsRaw !== undefined && !Array.isArray(customerFieldsRaw)) {
    return { error: 'customerFieldsは配列である必要があります。' }
  }
  if (vehicleFieldsRaw !== undefined && !Array.isArray(vehicleFieldsRaw)) {
    return { error: 'vehicleFieldsは配列である必要があります。' }
  }

  const customerFields = (customerFieldsRaw as string[] | undefined) ?? []
  const vehicleFields = (vehicleFieldsRaw as string[] | undefined) ?? []

  // 重複チェック
  if (new Set(customerFields).size !== customerFields.length) {
    return { error: 'customerFieldsに重複があります。' }
  }
  if (new Set(vehicleFields).size !== vehicleFields.length) {
    return { error: 'vehicleFieldsに重複があります。' }
  }

  // allowlist外チェック
  const invalidCustomer = customerFields.filter((f) => !CUSTOMER_SYNC_ALLOWLIST.has(f as CustomerSyncField))
  if (invalidCustomer.length > 0) {
    return { error: `allowlist外の顧客フィールド: ${invalidCustomer.join(', ')}` }
  }
  const invalidVehicle = vehicleFields.filter((f) => !VEHICLE_SYNC_ALLOWLIST.has(f as VehicleSyncField))
  if (invalidVehicle.length > 0) {
    return { error: `allowlist外の車両フィールド: ${invalidVehicle.join(', ')}` }
  }

  // mileageチェック
  if (vehicleFields.includes('mileage')) {
    return { error: 'mileageはvehicleFieldsに含めません。mileageSyncを使用してください。' }
  }

  const expectedCustomerUpdatedAt = typeof obj.expectedCustomerUpdatedAt === 'string' ? obj.expectedCustomerUpdatedAt : undefined
  const expectedVehicleUpdatedAt = typeof obj.expectedVehicleUpdatedAt === 'string' ? obj.expectedVehicleUpdatedAt : undefined

  if (customerFields.length > 0 && !expectedCustomerUpdatedAt) {
    return { error: 'customerFields指定時はexpectedCustomerUpdatedAtが必須です。' }
  }
  if (vehicleFields.length > 0 && !expectedVehicleUpdatedAt) {
    return { error: 'vehicleFields指定時はexpectedVehicleUpdatedAtが必須です。' }
  }

  return {
    customerFields: customerFields as CustomerSyncField[],
    vehicleFields: vehicleFields as VehicleSyncField[],
    expectedCustomerUpdatedAt,
    expectedVehicleUpdatedAt,
  }
}
