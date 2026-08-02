import { useRef, useState, type KeyboardEvent } from 'react'
import { Lock, ShieldAlert, X } from 'lucide-react'

type DocumentTaxSettingsProps = {
  taxRate: number
  onTaxRateChange: (value: number) => void
}

export function DocumentTaxSettings({ taxRate, onTaxRateChange }: DocumentTaxSettingsProps) {
  const [unlocked, setUnlocked] = useState(false)
  const [confirmOpen, setConfirmOpen] = useState(false)
  const taxRateRef = useRef<HTMLInputElement>(null)

  function requestUnlock() {
    if (!unlocked) setConfirmOpen(true)
  }

  function handleLockedKeyDown(event: KeyboardEvent<HTMLInputElement>) {
    if (unlocked || (event.key !== 'Enter' && event.key !== ' ')) return
    event.preventDefault()
    requestUnlock()
  }

  function unlock() {
    setUnlocked(true)
    setConfirmOpen(false)
    window.setTimeout(() => taxRateRef.current?.focus(), 0)
  }

  function updateTaxRate(value: string) {
    const nextValue = Number(value)
    if (!Number.isFinite(nextValue)) return
    onTaxRateChange(Math.min(100, Math.max(0, nextValue)))
  }

  return <>
    <div className="document-tax-settings">
      <label className="form-field">
        <span>消費税率</span>
        <div className={`document-tax-rate-control${unlocked ? ' is-unlocked' : ''}`}>
          <input
            ref={taxRateRef}
            aria-label="この書類の消費税率"
            className="document-tax-rate-input"
            type="number"
            min="0"
            max="100"
            step="1"
            value={taxRate}
            readOnly={!unlocked}
            onClick={requestUnlock}
            onKeyDown={handleLockedKeyDown}
            onChange={(event) => updateTaxRate(event.target.value)}
          />
          <span>%</span>
          {!unlocked && <Lock size={14} aria-hidden="true" />}
        </div>
      </label>
    </div>
    {confirmOpen && <div className="modal-backdrop document-tax-confirm-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) setConfirmOpen(false) }}>
      <section className="modal document-tax-confirm-modal" role="dialog" aria-modal="true" aria-labelledby="document-tax-confirm-title">
        <div className="modal-header"><div><span className="document-tax-confirm-eyebrow"><ShieldAlert size={14} />金額計算設定の変更</span><h2 id="document-tax-confirm-title">この書類の税率を変更しますか？</h2></div><button className="modal-close" type="button" aria-label="税率変更の確認を閉じる" onClick={() => setConfirmOpen(false)}><X size={18} /></button></div>
        <div className="document-tax-confirm-body"><p>税率を変更すると、消費税額・合計金額・プレビュー・出力内容が再計算されます。</p><p>設定画面の標準値ではなく、この書類に保存されている税率を変更します。</p><div className="modal-footer"><button className="button button-secondary" type="button" onClick={() => setConfirmOpen(false)}>いいえ</button><button className="button button-primary" type="button" onClick={unlock}>はい</button></div></div>
      </section>
    </div>}
  </>
}
