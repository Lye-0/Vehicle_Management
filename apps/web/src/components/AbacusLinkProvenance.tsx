import type { AbacusDocumentImportMetadata } from '../lib/abacusDocumentMetadata'

const linkMethodLabels: Record<AbacusDocumentImportMetadata['linkMethod'], string> = {
  automatic: '自動紐づけ',
  'manual-vehicle': '手動紐づけ（車両）',
  'manual-customer-only': '手動紐づけ（顧客のみ）',
  recommended: 'おすすめ承認',
}

export function AbacusLinkProvenance({ metadata }: { metadata: AbacusDocumentImportMetadata | null | undefined }) {
  if (!metadata) return null

  const isManual = metadata.linkMethod === 'manual-vehicle' || metadata.linkMethod === 'manual-customer-only'
  return (
    <div className="document-abacus-provenance" aria-label="ABACUS移行の紐づけ情報">
      <div className="document-abacus-provenance-heading">
        <span className="document-abacus-provenance-label">ABACUS移行</span>
        <span className={`document-abacus-provenance-method${isManual ? ' is-manual' : ''}`}>{linkMethodLabels[metadata.linkMethod]}</span>
        <span className="document-abacus-provenance-state">{isManual ? '手動判断あり' : '手動判断なし'}</span>
      </div>
      <small>出典: {metadata.sourceLocation} / 元候補ID: {metadata.sourceCandidateId}</small>
      <small>判断根拠: {metadata.linkReason}{metadata.legacyFormat ? '（旧形式互換）' : ''}</small>
    </div>
  )
}
