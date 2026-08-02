import { describe, expect, it } from 'vitest'
import { calculateMaintenanceTotals, type MaintenanceItemInput } from '../src/routes/maintenance-routes'

const item = (
  description: string,
  quantity: number,
  unit: string,
  unitPrice: number,
  technicalFee: number,
): MaintenanceItemInput => ({
  kind: unitPrice === 0 ? '作業' : '部品',
  description,
  quantity,
  unit,
  unitPrice,
  technicalFee,
  summary: '',
  amount: Math.round(quantity * unitPrice) + technicalFee,
})

describe('maintenance document calculation', () => {
  it('matches the reference invoice totals', () => {
    const totals = calculateMaintenanceTotals([
      item('基本点検（38項目）', 1, '式', 0, 15_000),
      item('距離項目（A）', 1, '式', 0, 1_500),
      item('距離項目（B）', 1, '式', 0, 1_000),
      item('下回りスチーム洗浄', 1, '式', 0, 3_000),
      item('ファンベルト交換', 1, '本', 2_000, 1_200),
      item('クーラーベルト交換', 1, '本', 2_500, 1_500),
      item('エンジンオイル交換', 4, 'L', 800, 1_500),
      item('オイルフィルター交換', 1, '個', 1_500, 1_000),
      item('ブレーキフルード交換', 1, '個', 2_500, 1_800),
      item('代車', 1, '日', 0, 6_000),
      item('割引', 1, '式', 0, -3_000),
    ], {
      自賠責: 20_010,
      重量税: 24_600,
      印紙代: 1_100,
      リサイクル料金: 0,
    }, 0, 10, '切り捨て')

    expect(totals).toEqual({ subtotal: 42_200, tax: 4_220, total: 92_130 })
  })

  it('rounds each part amount before adding the technical fee', () => {
    const totals = calculateMaintenanceTotals([
      item('数量計算', 1.5, 'L', 333, 1_000),
    ], { 自賠責: 0, 重量税: 0, 印紙代: 0, リサイクル料金: 0 }, 0, 10, '切り捨て')

    expect(totals).toEqual({ subtotal: 1_500, tax: 150, total: 1_650 })
  })

  it('treats the adjustment as a non-taxable fee', () => {
    const totals = calculateMaintenanceTotals([
      item('整備作業', 1, '式', 10_000, 0),
    ], { 自賠責: 0, 重量税: 0, 印紙代: 0, リサイクル料金: 0 }, -3_000, 10, '切り捨て')

    expect(totals).toEqual({ subtotal: 10_000, tax: 1_000, total: 8_000 })
  })

  it('applies the selected rounding mode to document tax', () => {
    const items = [item('端数確認', 1, '式', 100_005, 0)]
    const fees = { 自賠責: 0, 重量税: 0, 印紙代: 0, リサイクル料金: 0 }

    expect(calculateMaintenanceTotals(items, fees, 0, 10, '切り捨て')).toEqual({ subtotal: 100_005, tax: 10_000, total: 110_005 })
    expect(calculateMaintenanceTotals(items, fees, 0, 10, '四捨五入')).toEqual({ subtotal: 100_005, tax: 10_001, total: 110_006 })
  })
})
