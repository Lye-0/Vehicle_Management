import { describe, expect, it } from 'vitest'
import {
  normalizeDisplacement,
  normalizeDate,
  normalizeMileage,
  normalizeModelYear,
  normalizePhone,
  normalizePostalCode,
  normalizeValueForComparison,
} from '@vehicle-management/shared'
import {
  buildCustomerUpdateValues,
  buildVehicleUpdateValues,
  computeActualCustomerDiffFields,
  computeActualVehicleDiffFields,
  computeCustomerDiffs,
  computeVehicleDiffs,
  normalizeCustomerBirthDateForStorage,
} from '../master-sync-helpers'

describe('入力値の正規化', () => {
  it('年式・排気量・走行距離へ単位と桁区切りを補完する', () => {
    expect(normalizeModelYear('2')).toBe('2年')
    expect(normalizeDisplacement('2000')).toBe('2,000 cc')
    expect(normalizeDisplacement('2000c')).toBe('2,000 cc')
    expect(normalizeMileage('10000')).toBe('10,000 km')
    expect(normalizeMileage('10000k')).toBe('10,000 km')
    expect(normalizeValueForComparison('modelYear', '2')).toBe('2')
    expect(normalizeValueForComparison('displacement', '2,000 cc')).toBe('2000')
    expect(normalizeValueForComparison('mileage', '10,000 km')).toBe('10000')
  })

  it('電話番号と郵便番号へハイフンを補完する', () => {
    expect(normalizePhone('09012345678')).toBe('090-1234-5678')
    expect(normalizePhone('0312345678')).toBe('03-1234-5678')
    expect(normalizePostalCode('〒1000001')).toBe('100-0001')
    expect(normalizePostalCode('100-0001')).toBe('100-0001')
    expect(normalizeValueForComparison('phone', '09012345678')).toBe('09012345678')
    expect(normalizeValueForComparison('postalCode', '100-0001')).toBe('1000001')
  })

  it('生年月日の区切り文字をISO形式へそろえる', () => {
    expect(normalizeDate('1990/01/23')).toBe('1990-01-23')
    expect(normalizeValueForComparison('birthDate', '1990/01/23')).toBe('1990-01-23')
  })

  it('生年月日の保存値は区切り文字を正規化する', () => {
    expect(normalizeCustomerBirthDateForStorage('１９９０／１／２')).toBe('1990/1/2')
    expect(normalizeCustomerBirthDateForStorage('1990-01-02')).toBe('1990/01/02')
    expect(normalizeCustomerBirthDateForStorage('11-2')).toBe('11/2')
  })

  it('空欄と不正な値を勝手に0へ変換しない', () => {
    expect(normalizeModelYear('')).toBe('')
    expect(normalizeDisplacement('未入力')).toBe('未入力')
    expect(normalizePostalCode('123')).toBe('123')
  })
})

describe('マスタ同期の正規化比較', () => {
  it('電話番号・郵便番号の表記ゆれを差分にしない', () => {
    const current = { name: '山田 太郎', nameKana: null, phone: '090-1234-5678', postalCode: '100-0001', address: null }
    expect(computeCustomerDiffs(current, { phone: '09012345678', postalCode: '1000001' })).toEqual([])
    expect(computeActualCustomerDiffFields(current, { phone: '09012345678', postalCode: '1000001' })).toEqual(new Set())
  })

  it('生年月日の区切り文字の表記ゆれを差分にしない', () => {
    const current = { name: '山田 太郎', nameKana: null, phone: null, postalCode: null, address: null, birthDate: '1990-01-23', employer: null }
    expect(computeCustomerDiffs(current, { birthDate: '1990/01/23' })).toEqual([])
    expect(computeActualCustomerDiffFields(current, { birthDate: '1990/01/23' })).toEqual(new Set())
  })

  it('省略形式の生年月日を正規化して同期する', () => {
    const current = { name: '山田 太郎', nameKana: null, phone: null, postalCode: null, address: null, birthDate: null, employer: null }
    expect(computeCustomerDiffs(current, { birthDate: '11-2' }).map((diff) => diff.field)).toEqual(['birthDate'])
    expect(buildCustomerUpdateValues(['birthDate'], { birthDate: '11-2' })).toEqual({ birth_date: '11/2' })
  })

  it('顧客情報の列名プレースホルダーを空値として扱う', () => {
    const current = { name: '山田 太郎', nameKana: null, phone: null, postalCode: null, address: null, birthDate: 'birth_date', employer: 'employer' }
    expect(computeCustomerDiffs(current, { birthDate: 'birth_date', employer: 'employer' })).toEqual([])
    expect(computeActualCustomerDiffFields(current, { birthDate: 'birth_date', employer: 'employer' })).toEqual(new Set())
    expect(computeCustomerDiffs(current, { birthDate: '1990/01/23', employer: '株式会社サンプル' }).map((diff) => diff.field)).toEqual(['birthDate', 'employer'])
    expect(buildCustomerUpdateValues(['birthDate', 'employer'], { birthDate: 'birth_date', employer: 'employer' })).toEqual({})
  })

  it('年式・排気量の単位省略を差分にしない', () => {
    const current = {
      maker: 'トヨタ', name: 'プリウス', model: null, registrationNumber: null, chassisNumber: null,
      modelYear: 2, inspectionDate: null, bodyColor: null, displacement: 2000, transmission: null,
    }
    const override = { year: '2', displacement: '2000' }
    expect(computeVehicleDiffs(current, override)).toEqual([])
    expect(computeActualVehicleDiffFields(current, override)).toEqual(new Set())
    expect(buildVehicleUpdateValues(['modelYear', 'displacement'], override)).toEqual({ model_year: 2, displacement: 2000 })
  })

  it('実値が変わった場合は差分として残す', () => {
    const current = {
      maker: 'トヨタ', name: 'プリウス', model: null, registrationNumber: null, chassisNumber: null,
      modelYear: 2, inspectionDate: null, bodyColor: null, displacement: 2000, transmission: null,
    }
    expect(computeActualVehicleDiffFields(current, { year: '3' })).toEqual(new Set(['modelYear']))
  })
})
