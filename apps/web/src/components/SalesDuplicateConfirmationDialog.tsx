import { useState } from 'react'
import { AlertTriangle, CarFront, UserRound, X } from 'lucide-react'
import type { DuplicateCustomerCandidate, DuplicateVehicleCandidate } from '../lib/masterSyncApi'

export type SalesDuplicateDialogState =
  | { kind: 'customer'; candidates: DuplicateCustomerCandidate[] }
  | { kind: 'vehicle'; matchReason: 'chassis_number' | 'registration_number'; candidates: DuplicateVehicleCandidate[] }

type Props = {
  state: SalesDuplicateDialogState
  canUseExistingVehicle: (vehicleId: string) => boolean
  onUseExistingCustomer: (customerId: string) => void
  onContinueAsNewCustomer: () => void
  onUseExistingVehicle: (vehicleId: string) => void
  onContinueAsNewVehicle: (vehicleId: string) => void
  onCancel: () => void
}

export function SalesDuplicateConfirmationDialog({
  state,
  canUseExistingVehicle,
  onUseExistingCustomer,
  onContinueAsNewCustomer,
  onUseExistingVehicle,
  onContinueAsNewVehicle,
  onCancel,
}: Props) {
  return (
    <div className="modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) onCancel() }}>
      <section className="modal master-sync-modal" role="dialog" aria-modal="true" aria-labelledby="sales-duplicate-dialog-title" style={{ maxWidth: 560 }}>
        <div className="modal-header">
          <h2 id="sales-duplicate-dialog-title">{state.kind === 'customer' ? '顧客の重複確認' : state.matchReason === 'chassis_number' ? '車台番号の重複確認' : '登録番号の重複確認'}</h2>
          <button className="modal-close" type="button" aria-label="閉じる" onClick={onCancel}><X size={19} /></button>
        </div>

        <div className="modal-form">
          {state.kind === 'customer' ? (
            <>
              <p className="modal-description"><UserRound size={16} />入力した顧客情報と一致する既存顧客があります。</p>
              <div className="master-sync-section">
                {state.candidates.map((candidate) => (
                  <div className="master-sync-diff-row" key={candidate.id}>
                    <div className="master-sync-diff-info">
                      <span className="master-sync-diff-label">{candidate.name}</span>
                      <span className="master-sync-diff-note">{candidate.matchReason === 'phone' ? '電話番号' : 'メールアドレス'}が一致{candidate.phone ? ` ・ ${candidate.phone}` : ''}{candidate.email ? ` ・ ${candidate.email}` : ''}</span>
                    </div>
                    <button className="button button-secondary" type="button" onClick={() => onUseExistingCustomer(candidate.id)}>この既存顧客を使用</button>
                  </div>
                ))}
              </div>
              <p style={{ margin: '16px 0 0', color: 'var(--text-secondary)', fontSize: 12, lineHeight: 1.6 }}>
                既存顧客を使用せず、新規顧客として登録する場合は「新規顧客として続ける」を選択してください。
              </p>
            </>
          ) : state.matchReason === 'chassis_number' ? (
            <>
              <p className="modal-description"><AlertTriangle size={16} />車台番号が一致するため、新規車両として保存できません。</p>
              <div className="master-sync-section">
                {state.candidates.map((candidate) => {
                  const canUse = canUseExistingVehicle(candidate.id)
                  return (
                    <div className="master-sync-diff-row" key={candidate.id}>
                      <div className="master-sync-diff-info">
                        <span className="master-sync-diff-label"><CarFront size={14} />{candidate.maker} {candidate.name}</span>
                        <span className="master-sync-diff-note">車台番号: {candidate.chassisNumber || '未登録'}{candidate.registrationNumber ? ` ・ 登録番号: ${candidate.registrationNumber}` : ''}</span>
                      </div>
                      <button className="button button-secondary" type="button" disabled={!canUse} onClick={() => onUseExistingVehicle(candidate.id)}>{canUse ? 'この既存車両を使用' : '別顧客のため選択不可'}</button>
                    </div>
                  )
                })}
              </div>
              <p style={{ margin: '16px 0 0', color: 'var(--danger)', fontSize: 12, lineHeight: 1.6 }}>
                現在の顧客に属する候補を使用できない場合は、顧客と車両の選択を確認してください。
              </p>
            </>
          ) : (
            <RegistrationDuplicateContent
              candidates={state.candidates}
              canUseExistingVehicle={canUseExistingVehicle}
              onUseExistingVehicle={onUseExistingVehicle}
              onContinueAsNewVehicle={onContinueAsNewVehicle}
            />
          )}
        </div>

        <div className="modal-footer">
          <button className="button button-secondary" type="button" onClick={onCancel}>保存を中止</button>
          {state.kind === 'customer' && <button className="button button-primary" type="button" onClick={onContinueAsNewCustomer}>新規顧客として続ける</button>}
        </div>
      </section>
    </div>
  )
}

function RegistrationDuplicateContent({
  candidates,
  canUseExistingVehicle,
  onUseExistingVehicle,
  onContinueAsNewVehicle,
}: {
  candidates: DuplicateVehicleCandidate[]
  canUseExistingVehicle: (vehicleId: string) => boolean
  onUseExistingVehicle: (vehicleId: string) => void
  onContinueAsNewVehicle: (vehicleId: string) => void
}) {
  const [selectedCandidateId, setSelectedCandidateId] = useState<string | null>(null)

  return (
    <>
      <p className="modal-description"><CarFront size={16} />登録番号が一致する候補を確認してください。</p>
      <div className="master-sync-section">
        {candidates.map((candidate) => {
          const canUse = canUseExistingVehicle(candidate.id)
          return (
            <div className="master-sync-diff-row" key={candidate.id}>
              <div className="master-sync-diff-info">
                <span className="master-sync-diff-label">{candidate.maker} {candidate.name}</span>
                <span className="master-sync-diff-note">登録番号: {candidate.registrationNumber || '未登録'}{candidate.chassisNumber ? ` ・ 車台番号: ${candidate.chassisNumber}` : ''}</span>
              </div>
              <div style={{ display: 'grid', justifyItems: 'end', gap: 6 }}>
                <button className="button button-secondary" type="button" disabled={!canUse} onClick={() => onUseExistingVehicle(candidate.id)}>{canUse ? 'この既存車両を使用' : '別顧客のため選択不可'}</button>
                <label style={{ display: 'flex', alignItems: 'center', gap: 6, color: 'var(--text-secondary)', fontSize: 11 }}>
                  <input type="radio" name="sales-registration-duplicate" checked={selectedCandidateId === candidate.id} onChange={() => setSelectedCandidateId(candidate.id)} />
                  この候補との重複を確認
                </label>
              </div>
            </div>
          )
        })}
      </div>
      <p style={{ margin: '16px 0 0', color: 'var(--text-secondary)', fontSize: 12, lineHeight: 1.6 }}>
        既存車両を使用しない場合は、候補を1件選択して重複確認後に新規車両として続けます。
      </p>
      <div style={{ display: 'flex', justifyContent: 'flex-end', marginTop: 14 }}>
        <button className="button button-primary" type="button" disabled={!selectedCandidateId} onClick={() => { if (selectedCandidateId) onContinueAsNewVehicle(selectedCandidateId) }}>
          確認して新規車両として続ける
        </button>
      </div>
    </>
  )
}
