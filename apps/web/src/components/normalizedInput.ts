import type { NormalizableField } from '@vehicle-management/shared'

function normalizeEditableCharacters(value: string) {
  return value.normalize('NFKC').replace(/[‐‑‒–—―−ー]/gu, '-')
}

export function toEditableNormalizedValue(field: NormalizableField, value: string) {
  const normalized = normalizeEditableCharacters(value).trim()
  if (field === 'modelYear') return normalized.replace(/\s*年$/u, '')
  if (field === 'displacement') return normalized.replace(/\s*c{1,2}$/i, '').replace(/[\s,]/g, '')
  if (field === 'mileage') return normalized.replace(/\s*k(?:m)?$/i, '').replace(/[\s,]/g, '')
  if (field === 'phone') return normalized
  return normalized.replace(/[\s-]/g, '')
}

export function sanitizeNormalizedDraft(field: NormalizableField, value: string): string | null {
  const normalized = normalizeEditableCharacters(value)
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
