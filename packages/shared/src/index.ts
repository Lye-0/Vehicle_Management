export type NormalizableField = 'modelYear' | 'displacement' | 'mileage' | 'phone' | 'postalCode'

const numberFormatter = new Intl.NumberFormat('ja-JP')

function normalizedString(value: unknown): string {
  if (value === null || value === undefined) return ''
  return String(value).normalize('NFKC').trim()
}

function parseIntegerWithOptionalUnit(value: unknown): number | null {
  if (typeof value === 'number') {
    return Number.isInteger(value) && Number.isFinite(value) && value >= 0 ? value : null
  }

  const text = normalizedString(value)
  if (!text || !/^\d[\d,\s]*\s*(?:年|cc|km)?$/i.test(text)) return null
  const digits = text.replace(/[,\s]|年|cc|km/gi, '')
  const parsed = Number(digits)
  return Number.isInteger(parsed) && Number.isFinite(parsed) && parsed >= 0 ? parsed : null
}

function normalizeIntegerUnit(value: unknown, unit: '年' | 'cc' | 'km'): string {
  const text = normalizedString(value)
  if (!text) return ''
  const completedText = unit === 'cc'
    ? text.replace(/^(\d[\d,\s]*\s*)c$/i, '$1cc')
    : unit === 'km'
      ? text.replace(/^(\d[\d,\s]*\s*)k$/i, '$1km')
      : text
  const parsed = parseIntegerWithOptionalUnit(completedText)
  if (parsed === null) return text
  return unit === '年' ? `${parsed}年` : `${numberFormatter.format(parsed)} ${unit}`
}

/** 年式を「数字 + 年」の表示形式へそろえる。 */
export function normalizeModelYear(value: unknown): string {
  return normalizeIntegerUnit(value, '年')
}

/** 排気量を「桁区切り + cc」の表示形式へそろえる。 */
export function normalizeDisplacement(value: unknown): string {
  return normalizeIntegerUnit(value, 'cc')
}

/** 走行距離を「桁区切り + km」の表示形式へそろえる。 */
export function normalizeMileage(value: unknown): string {
  return normalizeIntegerUnit(value, 'km')
}

/** マスタの数値カラムへ同期するための整数値を取得する。 */
export function parseNormalizedInteger(value: unknown): number | null {
  return parseIntegerWithOptionalUnit(value)
}

/** 日本国内の一般的な電話番号にハイフンを補完する。 */
export function normalizePhone(value: unknown): string {
  const text = normalizedString(value)
  if (!text) return ''
  if (!/^[0-9\s()\-]+$/.test(text)) return text

  const digits = text.replace(/[\s()\-]/g, '')
  if (/^0\d{10}$/.test(digits)) return `${digits.slice(0, 3)}-${digits.slice(3, 7)}-${digits.slice(7)}`
  if (/^(?:03|06)\d{8}$/.test(digits)) return `${digits.slice(0, 2)}-${digits.slice(2, 6)}-${digits.slice(6)}`
  if (/^(?:0120|0570|0800)\d{6}$/.test(digits)) return `${digits.slice(0, 4)}-${digits.slice(4, 7)}-${digits.slice(7)}`
  if (/^0\d{9}$/.test(digits)) return `${digits.slice(0, 3)}-${digits.slice(3, 6)}-${digits.slice(6)}`
  return text
}

/** 郵便番号の数字7桁へハイフンを補完する。〒プレフィックスも除去する。 */
export function normalizePostalCode(value: unknown): string {
  const text = normalizedString(value).replace(/^〒\s*/, '')
  if (!text) return ''
  if (!/^[0-9\s\-]+$/.test(text)) return text

  const digits = text.replace(/[\s\-]/g, '')
  return /^\d{7}$/.test(digits) ? `${digits.slice(0, 3)}-${digits.slice(3)}` : text
}

/** 生年月日などの日付をISO形式へそろえる。 */
export function normalizeDate(value: unknown): string {
  const text = normalizedString(value)
  return /^\d{4}[-/]\d{2}[-/]\d{2}$/.test(text) ? text.replaceAll('/', '-') : text
}

export function normalizeFieldValue(field: NormalizableField, value: unknown): string {
  switch (field) {
    case 'modelYear': return normalizeModelYear(value)
    case 'displacement': return normalizeDisplacement(value)
    case 'mileage': return normalizeMileage(value)
    case 'phone': return normalizePhone(value)
    case 'postalCode': return normalizePostalCode(value)
  }
}

function digitsOnly(value: string): string {
  return value.replace(/\D/g, '')
}

/** 表記ゆれを除いた比較用の値を返す。 */
export function normalizeValueForComparison(field: string, value: unknown): string {
  const text = field === 'modelYear'
    ? normalizeModelYear(value)
    : field === 'displacement'
      ? normalizeDisplacement(value)
      : field === 'mileage'
        ? normalizeMileage(value)
        : field === 'phone'
          ? normalizePhone(value)
        : field === 'postalCode'
          ? normalizePostalCode(value)
          : field === 'birthDate'
            ? normalizeDate(value)
          : normalizedString(value)

  if (field === 'phone' || field === 'postalCode') return digitsOnly(text)
  if (field === 'modelYear' || field === 'displacement' || field === 'mileage') {
    const parsed = parseNormalizedInteger(value)
    return parsed === null ? text : String(parsed)
  }
  return text
}
