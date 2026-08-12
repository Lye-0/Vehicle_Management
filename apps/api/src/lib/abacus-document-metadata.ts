export type AbacusDocumentImportMetadata = {
  documentKey: string
  sourceLocation: string
  vehicleless: boolean
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

  return {
    documentKey: record.documentKey,
    sourceLocation: record.sourceLocation,
    vehicleless: record.vehicleless,
  }
}
