import { describe, expect, it } from 'vitest'
import { isNonNegativeIntegerText } from '../import-routes'

describe('CSV financial input validation', () => {
  it('requires a non-negative integer tax value', () => {
    expect(isNonNegativeIntegerText('0')).toBe(true)
    expect(isNonNegativeIntegerText('1,234')).toBe(true)
    expect(isNonNegativeIntegerText('')).toBe(false)
    expect(isNonNegativeIntegerText('-1')).toBe(false)
    expect(isNonNegativeIntegerText('1.5')).toBe(false)
  })
})
