export type AbacusDetailLineMetadata = {
  description: string | null
  quantity: number | null
  unit: string | null
  unitPrice: number | null
  partAmount: number | null
  technicalFees: number | null
  summary: string | null
  sourceRowIndex: number
}

export type AbacusDetailReportMetadata = {
  isAbacusMigration: true
  amountOnlyRowCount: number
  excludedDetailCount: number
  detailAmount: number
  detailSubtotalDifference: number | null
  detailTotalDifference: number | null
  warning: string | null
}

export type AbacusDetailEnvelopeMetadata = {
  isAbacusMigration: true
  matchStatus: 'matched' | 'review' | 'unmatched'
  lines: AbacusDetailLineMetadata[]
  report: AbacusDetailReportMetadata | null
  amounts: { subtotal: number; tax: number; total: number } | null
}

export function parseAbacusDetailEnvelope(detailsJson: string | null): AbacusDetailEnvelopeMetadata | null {
  if (!detailsJson) return null
  let parsed: unknown
  try { parsed = JSON.parse(detailsJson) } catch { return null }
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return null
  const record = parsed as Record<string, unknown>
  const detail = record.abacusDetails
  if (!detail || typeof detail !== 'object' || Array.isArray(detail)) return null
  const detailRecord = detail as Record<string, unknown>
  if (detailRecord.kind !== 'abacus-detail-lines' || detailRecord.version !== 1 || !['matched', 'review', 'unmatched'].includes(String(detailRecord.matchStatus)) || !Array.isArray(detailRecord.lines)) return null
  const lines = detailRecord.lines.flatMap((value) => {
    if (!value || typeof value !== 'object' || Array.isArray(value)) return []
    const line = value as Record<string, unknown>
    const sourceRowIndex = typeof line.sourceRowIndex === 'number' && Number.isSafeInteger(line.sourceRowIndex) ? line.sourceRowIndex : null
    if (sourceRowIndex === null || sourceRowIndex < 1) return []
    return [{
      description: nullableText(line.description),
      quantity: nullableNumber(line.quantity),
      unit: nullableText(line.unit),
      unitPrice: nullableInteger(line.unitPrice),
      partAmount: nullableInteger(line.partAmount),
      technicalFees: nullableInteger(line.technicalFees),
      summary: nullableText(line.summary),
      sourceRowIndex,
    }]
  })
  const reportRecord = record.abacusDetailReport
  const report = reportRecord && typeof reportRecord === 'object' && !Array.isArray(reportRecord)
    ? normalizeReport(reportRecord as Record<string, unknown>)
    : normalizeReport(detailRecord, true)
  const amountsRecord = record.abacusAmounts
  const amounts = amountsRecord && typeof amountsRecord === 'object' && !Array.isArray(amountsRecord)
    ? normalizeAmounts(amountsRecord as Record<string, unknown>)
    : null
  return { isAbacusMigration: true, matchStatus: detailRecord.matchStatus as AbacusDetailEnvelopeMetadata['matchStatus'], lines, report, amounts }
}

function normalizeAmounts(record: Record<string, unknown>) {
  const subtotal = nullableInteger(record.subtotal)
  const tax = nullableInteger(record.tax)
  const total = nullableInteger(record.total)
  return subtotal === null || tax === null || total === null ? null : { subtotal, tax, total }
}

function normalizeReport(record: Record<string, unknown>, allowMissingMarker = false): AbacusDetailReportMetadata | null {
  if (!allowMissingMarker && record.isAbacusMigration !== true) return null
  return {
    isAbacusMigration: true,
    amountOnlyRowCount: nonNegativeInteger(record.amountOnlyRowCount),
    excludedDetailCount: nonNegativeInteger(record.excludedDetailCount),
    detailAmount: integerValue(record.detailAmount),
    detailSubtotalDifference: nullableInteger(record.detailSubtotalDifference),
    detailTotalDifference: nullableInteger(record.detailTotalDifference),
    warning: nullableText(record.warning),
  }
}

function nullableText(value: unknown) {
  return typeof value === 'string' ? value : null
}

function nullableNumber(value: unknown) {
  if (value === null || value === undefined || value === '') return null
  const number = typeof value === 'number' ? value : Number(value)
  return Number.isFinite(number) ? number : null
}

function nullableInteger(value: unknown) {
  const number = nullableNumber(value)
  return number === null ? null : Math.round(number)
}

function integerValue(value: unknown) {
  return nullableInteger(value) ?? 0
}

function nonNegativeInteger(value: unknown) {
  return Math.max(0, integerValue(value))
}
