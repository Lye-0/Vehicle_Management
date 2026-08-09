import { describe, expect, it } from 'vitest'
import { HttpError } from '../../http'
import { parseNewVehicle } from '../sales-routes'

describe('sales nested vehicle input', () => {
  it('rejects invalid dates and unsafe numeric values', () => {
    expect(() => parseNewVehicle({ maker: 'メーカー', name: '車名', inspectionDate: '2026-02-30' })).toThrow(HttpError)
    expect(() => parseNewVehicle({ maker: 'メーカー', name: '車名', mileage: -1 })).toThrow(HttpError)
    expect(() => parseNewVehicle({ maker: 'メーカー', name: '車名', displacement: Number.POSITIVE_INFINITY })).toThrow(HttpError)
  })

  it('preserves valid zero values instead of treating them as absent', () => {
    expect(parseNewVehicle({ maker: 'メーカー', name: '車名', modelYear: 0, mileage: 0, displacement: 0 })).toMatchObject({ modelYear: 0, mileage: 0, displacement: 0 })
  })
})
