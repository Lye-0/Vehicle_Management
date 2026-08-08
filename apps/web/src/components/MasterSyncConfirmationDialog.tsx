import { useState } from 'react'
import { AlertTriangle, X } from 'lucide-react'
import type { SyncPreviewCustomerDiff, SyncPreviewMileageDiff, SyncPreviewVehicleDiff } from '../lib/masterSyncApi'

export type MasterSyncConfirmationResult = {
  customerFields: string[]
  vehicleFields: string[]
}

type Props = {
  isOlderThanLatestDocument: boolean
  customerDiffs: SyncPreviewCustomerDiff[]
  vehicleDiffs: SyncPreviewVehicleDiff[]
  mileageDiff?: SyncPreviewMileageDiff
  hasCustomerConflict: boolean
  hasVehicleConflict: boolean
  onConfirm: (result: MasterSyncConfirmationResult) => void
  onCancel: () => void
}

export function MasterSyncConfirmationDialog({
  isOlderThanLatestDocument,
  customerDiffs,
  vehicleDiffs,
  mileageDiff,
  hasCustomerConflict,
  hasVehicleConflict,
  onConfirm,
  onCancel,
}: Props) {
  const [selectedCustomerFields, setSelectedCustomerFields] = useState<Set<string>>(new Set())
  const [selectedVehicleFields, setSelectedVehicleFields] = useState<Set<string>>(new Set())

  function toggleCustomerField(field: string) {
    setSelectedCustomerFields((prev) => {
      const next = new Set(prev)
      if (next.has(field)) next.delete(field)
      else next.add(field)
      return next
    })
  }

  function toggleVehicleField(field: string) {
    setSelectedVehicleFields((prev) => {
      const next = new Set(prev)
      if (next.has(field)) next.delete(field)
      else next.add(field)
      return next
    })
  }

  function handleConfirm() {
    onConfirm({
      customerFields: Array.from(selectedCustomerFields),
      vehicleFields: Array.from(selectedVehicleFields),
    })
  }

  const hasAnyDiff = customerDiffs.length > 0 || vehicleDiffs.length > 0 || Boolean(mileageDiff)
  const hasAnySelectable = (customerDiffs.length > 0 && !hasCustomerConflict) || (vehicleDiffs.length > 0 && !hasVehicleConflict)
  const hasMileageOnly = !hasAnySelectable && Boolean(mileageDiff)

  return (
    <div className="modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) onCancel() }}>
      <section className="modal master-sync-modal" role="dialog" aria-modal="true" aria-labelledby="master-sync-dialog-title" style={{ maxWidth: 560, maxHeight: 'calc(100vh - 40px)', display: 'flex', flexDirection: 'column' }}>
        <div className="modal-header">
          <h2 id="master-sync-dialog-title">顧客・車両情報の更新確認</h2>
          <button className="modal-close" type="button" aria-label="閉じる" onClick={onCancel}><X size={19} /></button>
        </div>
        <div className="modal-form" style={{ overflowY: 'auto', flex: '1 1 auto' }}>
          <p style={{ margin: '0 0 16px', fontSize: 13, lineHeight: 1.6, color: 'var(--text-secondary)' }}>
            書類内の情報と現在登録されている情報に違いがあります。現在情報へ反映する項目を選択してください。
          </p>

          {isOlderThanLatestDocument && (
            <div className="master-sync-warning" style={{ marginBottom: 16 }}>
              <div className="master-sync-warning-header"><AlertTriangle size={15} /><strong>過去日付の書類です</strong></div>
              <p style={{ margin: '6px 0 0', fontSize: 12, lineHeight: 1.6 }}>
                この書類は、現在登録されている最新の書類より古い日付です。選択した項目を反映すると、現在の顧客・車両情報が過去の内容へ変更される可能性があります。
              </p>
            </div>
          )}

          {hasCustomerConflict && customerDiffs.length > 0 && (
            <div className="master-sync-conflict" style={{ marginBottom: 16 }}>
              <p style={{ margin: 0, fontSize: 12, lineHeight: 1.6 }}>
                顧客情報が書類を開いた後に更新されました。顧客情報を反映するには、再読み込み後にもう一度保存してください。
              </p>
            </div>
          )}

          {hasVehicleConflict && vehicleDiffs.length > 0 && (
            <div className="master-sync-conflict" style={{ marginBottom: 16 }}>
              <p style={{ margin: 0, fontSize: 12, lineHeight: 1.6 }}>
                車両情報が書類を開いた後に更新されました。車両情報を反映するには、再読み込み後にもう一度保存してください。
              </p>
            </div>
          )}

          {customerDiffs.length > 0 && (
            <fieldset className="master-sync-section" style={{ border: 'none', padding: 0, margin: '0 0 16px' }}>
              <legend className="master-sync-section-title">顧客情報</legend>
              {customerDiffs.map((diff) => (
                <MasterSyncDiffRow
                  key={diff.field}
                  diff={diff}
                  checked={selectedCustomerFields.has(diff.field)}
                  disabled={hasCustomerConflict}
                  onToggle={() => toggleCustomerField(diff.field)}
                />
              ))}
            </fieldset>
          )}

          {vehicleDiffs.length > 0 && (
            <fieldset className="master-sync-section" style={{ border: 'none', padding: 0, margin: '0 0 16px' }}>
              <legend className="master-sync-section-title">車両情報</legend>
              {vehicleDiffs.map((diff) => (
                <MasterSyncDiffRow
                  key={diff.field}
                  diff={diff}
                  checked={selectedVehicleFields.has(diff.field)}
                  disabled={hasVehicleConflict}
                  onToggle={() => toggleVehicleField(diff.field)}
                />
              ))}
            </fieldset>
          )}

          {mileageDiff && mileageDiff.isChanged && (
            <fieldset className="master-sync-section" style={{ border: 'none', padding: 0, margin: '0 0 16px' }}>
              <legend className="master-sync-section-title">走行距離</legend>
              <div className="master-sync-diff-row">
                <div className="master-sync-diff-info">
                  <span className="master-sync-diff-label">走行距離</span>
                  <div className="master-sync-diff-values">
                    <span className="master-sync-diff-current">{formatMileage(mileageDiff.currentValue)}</span>
                    <span className="master-sync-diff-arrow">→</span>
                    <span className="master-sync-diff-document">{formatMileage(mileageDiff.documentValue)}</span>
                  </div>
                  <span className="master-sync-diff-note">走行距離は整備履歴として保存されるため、OFFにできません。</span>
                </div>
                <label className="backup-toggle master-sync-toggle" style={{ cursor: 'default' }}>
                  <input type="checkbox" checked readOnly disabled aria-label="走行距離の同期（必須・変更不可）" />
                  <span className="backup-toggle-track" aria-hidden="true"><span /></span>
                </label>
              </div>
            </fieldset>
          )}

          {!hasAnyDiff && (
            <p style={{ margin: 0, fontSize: 13, color: 'var(--text-muted)' }}>
              差分はありません。
            </p>
          )}
        </div>
        <div className="modal-footer master-sync-modal-footer">
          <button className="button button-secondary" type="button" onClick={onCancel}>保存せず閉じる</button>
          <button className="button button-primary" type="button" disabled={!hasAnyDiff} onClick={handleConfirm}>
            {hasMileageOnly ? '走行距離を記録して保存' : '保存して選択項目を反映'}
          </button>
        </div>
      </section>
    </div>
  )
}

function MasterSyncDiffRow({ diff, checked, disabled, onToggle }: { diff: SyncPreviewCustomerDiff | SyncPreviewVehicleDiff; checked: boolean; disabled: boolean; onToggle: () => void }) {
  return (
    <div className={`master-sync-diff-row${disabled ? ' is-disabled' : ''}`}>
      <div className="master-sync-diff-info">
        <span className="master-sync-diff-label">
          {diff.label}
          {diff.isAttention && <span className="master-sync-attention-badge" title="重要な変更です">!</span>}
        </span>
        <div className="master-sync-diff-values">
          <span className="master-sync-diff-current">{diff.currentValue || '（空）'}</span>
          <span className="master-sync-diff-arrow">→</span>
          <span className="master-sync-diff-document">{diff.documentValue || '（空）'}</span>
        </div>
      </div>
      <label className="backup-toggle master-sync-toggle">
        <input
          type="checkbox"
          checked={checked}
          disabled={disabled}
          onChange={onToggle}
          aria-label={`${diff.label}を反映する`}
        />
        <span className="backup-toggle-track" aria-hidden="true"><span /></span>
      </label>
    </div>
  )
}

function formatMileage(value: number | null): string {
  if (value === null) return '（未設定）'
  return `${value.toLocaleString('ja-JP')} km`
}
