export const ABACUS_LINK_METHODS = ['automatic', 'manual-vehicle', 'manual-customer-only', 'recommended'] as const
export type AbacusLinkMethod = typeof ABACUS_LINK_METHODS[number]

export type AbacusDocumentImportMetadata = {
  documentKey: string
  sourceCandidateId: string
  sourceLocation: string
  vehicleless: boolean
  linkedCustomerId: string | null
  linkedVehicleId: string | null
  linkMethod: AbacusLinkMethod
  linkReason: string
  legacyFormat: boolean
}

/**
 * ABACUSグラフ登録が保存した詳細JSONから、表示用の最小メタデータだけを取り出します。
 * 書類の通常詳細は各ルートで正規化するため、ここではABACUS由来の印だけを読み取ります。
 */
export function parseAbacusDocumentImportMetadata(detailsJson: string | null): AbacusDocumentImportMetadata | null {
  if (!detailsJson) return null

  let parsed: unknown
  try {
    parsed = JSON.parse(detailsJson)
  } catch {
    return null
  }
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return null

  const metadata = (parsed as Record<string, unknown>).abacusImport
  if (!metadata || typeof metadata !== 'object' || Array.isArray(metadata)) return null
  const record = metadata as Record<string, unknown>
  if (typeof record.documentKey !== 'string' || record.documentKey.trim() === '') return null
  if (typeof record.sourceLocation !== 'string' || record.sourceLocation.trim() === '') return null
  if (typeof record.vehicleless !== 'boolean') return null

  const legacyFormat = typeof record.linkMethod !== 'string'
  const linkMethod = ABACUS_LINK_METHODS.includes(record.linkMethod as AbacusLinkMethod)
    ? record.linkMethod as AbacusLinkMethod
    : legacyFormat
      ? 'automatic'
      : null
  if (!linkMethod) return null
  const sourceCandidateId = typeof record.sourceCandidateId === 'string' && record.sourceCandidateId.trim() !== ''
    ? record.sourceCandidateId.trim()
    : record.documentKey.trim()
  const linkedCustomerId = typeof record.linkedCustomerId === 'string' && record.linkedCustomerId.trim() !== ''
    ? record.linkedCustomerId.trim()
    : null
  const linkedVehicleId = typeof record.linkedVehicleId === 'string' && record.linkedVehicleId.trim() !== ''
    ? record.linkedVehicleId.trim()
    : null
  const linkReason = typeof record.linkReason === 'string' && record.linkReason.trim() !== ''
    ? record.linkReason.trim()
    : legacyFormat
      ? '旧形式パッケージ（Gate17〜19）から互換読み込み'
      : 'ABACUS移行元の紐づけ判断'

  return {
    documentKey: record.documentKey.trim(),
    sourceCandidateId,
    sourceLocation: record.sourceLocation.trim(),
    vehicleless: record.vehicleless,
    linkedCustomerId,
    linkedVehicleId,
    linkMethod,
    linkReason,
    legacyFormat,
  }
}
