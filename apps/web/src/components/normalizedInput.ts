import type { NormalizableField } from '@vehicle-management/shared'

export function toEditableNormalizedValue(field: NormalizableField, value: string) {
  const normalized = value.normalize('NFKC').trim()
  if (field === 'modelYear') return normalized.replace(/\s*年$/u, '')
  if (field === 'displacement') return normalized.replace(/\s*c{1,2}$/i, '').replace(/[\s,]/g, '')
  if (field === 'mileage') return normalized.replace(/\s*k(?:m)?$/i, '').replace(/[\s,]/g, '')
  if (field === 'phone') return normalized.replace(/[\s()-]/g, '')
  return normalized.replace(/[\s-]/g, '')
}

export function sanitizeNormalizedDraft(field: NormalizableField, value: string): string | null {
  const normalized = value.normalize('NFKC')
  const pattern = field === 'modelYear'
    ? /^[\d,\s]*年?$/u
    : field === 'displacement'
      ? /^[\d,\s]*c{0,2}$/iu
      : field === 'mileage'
        ? /^[\d,\s]*(?:k(?:m)?)?$/iu
        : field === 'phone'
          ? /^[\d\s()-]*$/u
          : /^[\d\s-]*$/u
  return pattern.test(normalized) ? normalized : null
}
