import { describe, expect, it } from 'vitest'
import { parseAbacusDocumentImportMetadata } from '../abacus-document-metadata'

describe('ABACUS書類の紐づけメタデータ', () => {
  it('Gate21形式のlinkMethod・候補・根拠を読み取る', () => {
    const result = parseAbacusDocumentImportMetadata(JSON.stringify({
      abacusImport: {
        documentKey: '販売書類|hanbai.csv|12|280',
        sourceCandidateId: '販売書類|hanbai.csv|12|280',
        sourceLocation: 'hanbai.csv #12',
        vehicleless: false,
        linkedCustomerId: 'abacus-customer-1',
        linkedVehicleId: 'abacus-vehicle-1',
        linkMethod: 'manual-vehicle',
        linkReason: 'ユーザーが紐づけ先車両を選択',
      },
    }))

    expect(result).toMatchObject({
      sourceCandidateId: '販売書類|hanbai.csv|12|280',
      linkMethod: 'manual-vehicle',
      linkedCustomerId: 'abacus-customer-1',
      linkedVehicleId: 'abacus-vehicle-1',
      linkReason: 'ユーザーが紐づけ先車両を選択',
      legacyFormat: false,
    })
  })

  it('Gate17〜19の旧形式は自動紐づけとして互換読み込みする', () => {
    const result = parseAbacusDocumentImportMetadata(JSON.stringify({
      abacusImport: {
        documentKey: '整備書類|seibi.csv|4|635',
        sourceLocation: 'seibi.csv #4',
        vehicleless: true,
      },
    }))

    expect(result).toMatchObject({
      linkMethod: 'automatic',
      sourceCandidateId: '整備書類|seibi.csv|4|635',
      legacyFormat: true,
    })
    expect(result?.linkReason).toContain('旧形式')
  })
})
