import { describe, expect, it } from 'vitest'
import { formatDocumentNumber, getDocumentNumberPeriod } from '../src/document-number'

describe('document number', () => {
  it('formats the type, year, month, and monthly sequence', () => {
    expect(formatDocumentNumber('S', 2026, 7, 1)).toBe('S-2026-07001')
    expect(formatDocumentNumber('M', 2026, 11, 118)).toBe('M-2026-11118')
  })

  it('uses the Japan creation month at the UTC month boundary', () => {
    expect(getDocumentNumberPeriod(new Date('2026-06-30T15:30:00.000Z'))).toEqual({ year: 2026, month: 7 })
    expect(getDocumentNumberPeriod(new Date('2026-07-31T14:59:59.000Z'))).toEqual({ year: 2026, month: 7 })
    expect(getDocumentNumberPeriod(new Date('2026-07-31T15:00:00.000Z'))).toEqual({ year: 2026, month: 8 })
  })
})
