export type AbacusDetailLine = {
  description: string | null
  quantity: number | null
  unit: string | null
  unitPrice: number | null
  partAmount: number | null
  technicalFees: number | null
  summary: string | null
  sourceRowIndex: number
}

export type AbacusDetailReport = {
  isAbacusMigration: true
  amountOnlyRowCount: number
  excludedDetailCount: number
  detailAmount: number
  detailSubtotalDifference: number | null
  detailTotalDifference: number | null
  warning: string | null
}

export type AbacusDocumentAmounts = { subtotal: number; tax: number; total: number }

export function abacusNumber(value: number | null | undefined) {
  return value === null || value === undefined ? '' : String(value)
}
