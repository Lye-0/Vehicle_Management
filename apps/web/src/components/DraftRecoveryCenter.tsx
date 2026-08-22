import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { Bell, Clock3, FileText, RotateCcw, Settings2, Trash2, UserRound, X } from 'lucide-react'
import { deleteDraft, listDrafts, setDraftScope, subscribeDraftStorageChange, type DraftKind, type DraftRecord } from '../lib/draftStorage'
import { DraftRecoveryContext, type DraftRecoveryContextValue, type DraftRecoveryDestination } from '../hooks/draftRecoveryContext'

export function DraftRecoveryProvider({ userId, organizationId, runId, onNavigate, children }: { userId: string; organizationId: string; runId: string; onNavigate: (destination: DraftRecoveryDestination) => void; children: ReactNode }) {
  return <DraftRecoveryProviderContent userId={userId} organizationId={organizationId} runId={runId} onNavigate={onNavigate}>{children}</DraftRecoveryProviderContent>
}

function DraftRecoveryProviderContent({ userId, organizationId, runId, onNavigate, children }: { userId: string; organizationId: string; runId: string; onNavigate: (destination: DraftRecoveryDestination) => void; children: ReactNode }) {
  const [drafts, setDrafts] = useState<DraftRecord[]>([])
  const [notificationOpen, setNotificationOpen] = useState(false)
  const [recoveryOpen, setRecoveryOpen] = useState(false)
  const [focusedRecoveryKey, setFocusedRecoveryKey] = useState<string | null>(null)
  const [pendingRestore, setPendingRestore] = useState<DraftRecord | null>(null)
  const [hiddenKeys, setHiddenKeys] = useState<Set<string>>(new Set())
  const [heldKeys, setHeldKeys] = useState<Set<string>>(new Set())
  const [activeDraftKeys, setActiveDraftKeys] = useState<Partial<Record<DraftKind, string>>>({})
  const [restoreConflict, setRestoreConflict] = useState<{ target: DraftRecord; active: DraftRecord } | null>(null)
  const recoveryPromptedKeysRef = useRef<Set<string>>(new Set())

  const refreshDrafts = useCallback(async () => {
    try {
      setDrafts(await listDrafts())
    } catch {
      setDrafts([])
    }
  }, [])

  useEffect(() => {
    setDraftScope({ userId, organizationId, runId })
    setHiddenKeys(new Set())
    setHeldKeys(new Set())
    setActiveDraftKeys({})
    setRestoreConflict(null)
    recoveryPromptedKeysRef.current = new Set()
    setNotificationOpen(false)
    setRecoveryOpen(false)
    setFocusedRecoveryKey(null)
    setPendingRestore(null)
    void refreshDrafts()
    return () => setDraftScope(null)
  }, [organizationId, refreshDrafts, runId, userId])

  useEffect(() => subscribeDraftStorageChange(() => { void refreshDrafts() }), [refreshDrafts])

  const actionableDrafts = useMemo(() => drafts.filter((draft) => !hiddenKeys.has(draft.key)), [drafts, hiddenKeys])
  const notificationDrafts = useMemo(() => actionableDrafts.filter((draft) => draft.runId !== runId || heldKeys.has(draft.key)), [actionableDrafts, heldKeys, runId])
  const recoveryCandidates = notificationDrafts

  useEffect(() => {
    const newCandidates = recoveryCandidates.filter((draft) => !recoveryPromptedKeysRef.current.has(draft.key))
    if (!newCandidates.length) return
    newCandidates.forEach((draft) => recoveryPromptedKeysRef.current.add(draft.key))
    setFocusedRecoveryKey((current) => current && recoveryCandidates.some((draft) => draft.key === current) ? current : newCandidates[0].key)
    setRecoveryOpen(true)
  }, [recoveryCandidates])

  const openRecovery = useCallback((key?: string) => {
    const target = key ? actionableDrafts.find((draft) => draft.key === key) : recoveryCandidates[0]
    if (!target) return
    setFocusedRecoveryKey(target.key)
    setNotificationOpen(false)
    setRecoveryOpen(true)
  }, [actionableDrafts, recoveryCandidates])

  const hideDraft = useCallback((key: string) => {
    setHiddenKeys((current) => {
      const next = new Set(current)
      next.add(key)
      return next
    })
  }, [])

  const restoreDraft = useCallback((draft: DraftRecord) => {
    const activeKey = activeDraftKeys[draft.kind]
    const activeDraft = activeKey ? actionableDrafts.find((candidate) => candidate.key === activeKey) : undefined
    if (activeDraft && activeDraft.key !== draft.key) {
      setRecoveryOpen(false)
      setNotificationOpen(false)
      setRestoreConflict({ target: draft, active: activeDraft })
      return
    }
    hideDraft(draft.key)
    setHeldKeys((current) => {
      const next = new Set(current)
      next.delete(draft.key)
      return next
    })
    setPendingRestore(draft)
    setRecoveryOpen(false)
    setNotificationOpen(false)
    const destination = draftDestination(draft)
    if (destination) onNavigate(destination)
  }, [activeDraftKeys, actionableDrafts, hideDraft, onNavigate])

  const deferDraft = useCallback((draft: DraftRecord) => {
    setRecoveryOpen(false)
    setFocusedRecoveryKey(draft.key)
  }, [])

  const discardDraft = useCallback((draft: DraftRecord) => {
    if (typeof window !== 'undefined' && !window.confirm('この端末内下書きを削除しますか？削除した入力は復元できません。')) return
    setHiddenKeys((current) => {
      const next = new Set(current)
      next.add(draft.key)
      return next
    })
    void deleteDraft(draft.key).then(() => {
      setRecoveryOpen(false)
      setNotificationOpen(false)
      setFocusedRecoveryKey(null)
    }).catch(() => {
      setHiddenKeys((current) => {
        const next = new Set(current)
        next.delete(draft.key)
        return next
      })
      setHeldKeys((current) => {
        const next = new Set(current)
        next.delete(draft.key)
        return next
      })
    })
  }, [])

  const registerActiveDraft = useCallback((kind: DraftKind, key: string | null) => {
    setActiveDraftKeys((current) => {
      const next = { ...current }
      if (key) next[kind] = key
      else delete next[kind]
      return next
    })
    if (key) setHeldKeys((current) => { const next = new Set(current); next.delete(key); return next })
  }, [])

  const holdDraft = useCallback((draft: DraftRecord) => {
    setHeldKeys((current) => { const next = new Set(current); next.add(draft.key); return next })
    setActiveDraftKeys((current) => {
      const next = { ...current }
      if (next[draft.kind] === draft.key) delete next[draft.kind]
      return next
    })
  }, [])

  const resolveRestoreConflict = useCallback((action: 'hold' | 'discard') => {
    if (!restoreConflict) return
    const { target, active } = restoreConflict
    const complete = () => {
      if (action === 'hold') holdDraft(active)
      hideDraft(target.key)
      setHeldKeys((current) => { const next = new Set(current); next.delete(target.key); return next })
      setPendingRestore(target)
      setRestoreConflict(null)
      onNavigate(draftDestination(target)!)
    }
    if (action === 'discard') {
      void deleteDraft(active.key).then(complete).catch(() => undefined)
      return
    }
    complete()
  }, [hideDraft, holdDraft, onNavigate, restoreConflict])

  const acknowledgeRestore = useCallback((key: string) => {
    if (pendingRestore?.key === key) setPendingRestore(null)
  }, [pendingRestore])

  const getAutoResumeDraft = useCallback((kind: DraftKind) => {
    // 復元済みの下書きは通知再表示を抑えるため hiddenKeys に入っている。
    // ただし、ページ移動前に現在編集中だったキーは、同じ入力を復帰させるため最優先する。
    const activeKey = activeDraftKeys[kind]
    const activeDraft = activeKey ? drafts.find((draft) => draft.key === activeKey && !heldKeys.has(draft.key)) : null
    if (activeDraft) return activeDraft
    return actionableDrafts.find((draft) => draft.kind === kind && draft.runId === runId && !heldKeys.has(draft.key)) ?? null
  }, [activeDraftKeys, actionableDrafts, drafts, heldKeys, runId])

  const contextValue: DraftRecoveryContextValue = {
    drafts: actionableDrafts,
    notificationDrafts,
    currentRunId: runId,
    pendingRestore,
    notificationOpen,
    openNotifications: () => setNotificationOpen(true),
    closeNotifications: () => setNotificationOpen(false),
    openRecovery,
    restoreDraft,
    deferDraft,
    discardDraft,
    registerActiveDraft,
    holdDraft,
    acknowledgeRestore,
    getAutoResumeDraft,
    refreshDrafts,
  }

  return <DraftRecoveryContext.Provider value={contextValue}>
    {children}
    {notificationOpen && <NotificationCenterModal drafts={notificationDrafts} onClose={() => setNotificationOpen(false)} onOpenRecovery={openRecovery} onDelete={discardDraft} />}
    {recoveryOpen && <DraftRecoveryModal drafts={recoveryCandidates} focusedKey={focusedRecoveryKey} onClose={() => setRecoveryOpen(false)} onRestore={restoreDraft} onDefer={deferDraft} onDelete={discardDraft} />}
    {restoreConflict && <DraftConflictModal active={restoreConflict.active} target={restoreConflict.target} onHoldAndRestore={() => resolveRestoreConflict('hold')} onDiscardAndRestore={() => resolveRestoreConflict('discard')} onCancel={() => setRestoreConflict(null)} />}
  </DraftRecoveryContext.Provider>
}

function draftDestination(draft: DraftRecord): DraftRecoveryDestination | null {
  if (draft.kind.startsWith('sales-')) return { section: 'sales', recordId: draft.targetId }
  if (draft.kind.startsWith('maintenance-')) return { section: 'maintenance', recordId: draft.targetId }
  if (draft.kind.startsWith('customer-') || draft.kind.startsWith('vehicle-')) return { section: 'customers' }
  if (draft.kind === 'settings') return { section: 'settings' }
  return null
}

function draftKindLabel(kind: DraftKind) {
  if (kind === 'sales-new') return '販売書類（新規・未採番）'
  if (kind === 'sales-existing') return '販売書類の編集'
  if (kind === 'maintenance-new') return '整備書類（新規・未採番）'
  if (kind === 'maintenance-existing') return '整備書類の編集'
  if (kind === 'customer-new') return '顧客登録'
  if (kind === 'customer-existing') return '顧客情報の編集'
  if (kind === 'vehicle-new') return '車両登録'
  if (kind === 'vehicle-existing') return '車両情報の編集'
  return '設定'
}

function draftIcon(kind: DraftKind) {
  if (kind === 'settings') return Settings2
  if (kind.startsWith('customer-') || kind.startsWith('vehicle-')) return UserRound
  return FileText
}

function formatDraftTime(timestamp: number) {
  return new Intl.DateTimeFormat('ja-JP', { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit' }).format(timestamp)
}

function draftSummary(draft: DraftRecord) {
  const value = draft.value as { customerName?: string; name?: string; maker?: string; model?: string; number?: string; type?: string; category?: string }
  if (draft.kind === 'sales-new' || draft.kind === 'maintenance-new') return [value.type, value.category, value.customerName || '顧客未選択'].filter(Boolean).join(' ・ ')
  if (draft.kind === 'sales-existing' || draft.kind === 'maintenance-existing') return value.number || draft.targetId || '対象書類'
  if (draft.kind === 'customer-new' || draft.kind === 'customer-existing') return value.name || '顧客情報'
  if (draft.kind === 'vehicle-new' || draft.kind === 'vehicle-existing') return [value.maker, value.model].filter(Boolean).join(' ') || '車両情報'
  return '店舗情報・明細候補などの設定'
}

function DraftRow({ draft, focused, onRestore, onDefer, onDelete }: { draft: DraftRecord; focused?: boolean; onRestore: (draft: DraftRecord) => void; onDefer?: (draft: DraftRecord) => void; onDelete: (draft: DraftRecord) => void }) {
  const Icon = draftIcon(draft.kind)
  return <article className={`draft-recovery-row${focused ? ' is-focused' : ''}`}>
    <div className="draft-recovery-row-icon"><Icon size={20} aria-hidden="true" /></div>
    <div className="draft-recovery-row-body">
      <strong>{draftKindLabel(draft.kind)}</strong>
      <span>{draftSummary(draft)}</span>
      <span><Clock3 size={13} aria-hidden="true" />端末内保存：{formatDraftTime(draft.savedAt)}</span>
      <small>サーバー未保存の入力です</small>
    </div>
    <div className="draft-recovery-row-actions">
      <button className="button button-primary" type="button" onClick={() => onRestore(draft)}><RotateCcw size={15} />復元して開く</button>
      {onDefer && <button className="button button-secondary" type="button" onClick={() => onDefer(draft)}>保留</button>}
      <button className="icon-button draft-delete-button" type="button" aria-label="端末内下書きを削除" title="端末内下書きを削除" onClick={() => onDelete(draft)}><Trash2 size={17} /></button>
    </div>
  </article>
}

function NotificationCenterModal({ drafts, onClose, onOpenRecovery, onDelete }: { drafts: DraftRecord[]; onClose: () => void; onOpenRecovery: (key: string) => void; onDelete: (draft: DraftRecord) => void }) {
  return <div className="modal-backdrop notification-center-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose() }}>
    <section className="modal notification-center-modal" role="dialog" aria-modal="true" aria-labelledby="notification-center-title">
      <header className="notification-center-header"><div><span className="page-eyebrow">NOTIFICATIONS</span><h2 id="notification-center-title"><Bell size={22} />通知</h2></div><button className="icon-button" type="button" aria-label="通知を閉じる" onClick={onClose}><X size={21} /></button></header>
      <div className="notification-center-body">
        {drafts.length ? <div className="notification-list">{drafts.map((draft) => <div className="notification-list-item" key={draft.key} role="button" tabIndex={0} onClick={() => onOpenRecovery(draft.key)} onKeyDown={(event) => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); onOpenRecovery(draft.key) } }}><span className="notification-list-icon"><Bell size={17} /></span><span><strong>{draftKindLabel(draft.kind)}</strong><small>{draftSummary(draft)} ・ 端末内保存：{formatDraftTime(draft.savedAt)}</small></span><span className="notification-list-arrow">›</span><button className="notification-list-delete" type="button" aria-label="通知を削除" title="通知を削除" onClick={(event) => { event.stopPropagation(); onDelete(draft) }}><Trash2 size={15} /></button></div>)}</div> : <div className="notification-empty"><Bell size={28} /><strong>通知はありません</strong><span>対応が必要な通知があると、ここに表示されます。</span></div>}
      </div>
    </section>
  </div>
}

function DraftRecoveryModal({ drafts, focusedKey, onClose, onRestore, onDefer, onDelete }: { drafts: DraftRecord[]; focusedKey: string | null; onClose: () => void; onRestore: (draft: DraftRecord) => void; onDefer: (draft: DraftRecord) => void; onDelete: (draft: DraftRecord) => void }) {
  const orderedDrafts = [...drafts].sort((left, right) => (left.key === focusedKey ? -1 : right.key === focusedKey ? 1 : right.savedAt - left.savedAt))
  return <div className="modal-backdrop notification-center-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose() }}>
    <section className="modal draft-recovery-modal" role="dialog" aria-modal="true" aria-labelledby="draft-recovery-title">
      <header className="notification-center-header"><div><span className="page-eyebrow">DRAFT RECOVERY</span><h2 id="draft-recovery-title"><RotateCcw size={22} />端末内の入力を復元</h2></div><button className="icon-button" type="button" aria-label="復元案内を閉じる" onClick={onClose}><X size={21} /></button></header>
      <div className="draft-recovery-intro">前回、サーバーへ保存される前に画面が閉じられた可能性があります。復元する内容を選択してください。</div>
      <div className="draft-recovery-list">{orderedDrafts.length ? orderedDrafts.map((draft) => <DraftRow key={draft.key} draft={draft} focused={draft.key === focusedKey} onRestore={onRestore} onDefer={onDefer} onDelete={onDelete} />) : <div className="notification-empty"><strong>復元できる入力はありません</strong></div>}</div>
      <footer className="draft-recovery-footer"><span>保留した入力は、右上の通知ボタンから後で確認できます。</span><button className="button button-secondary" type="button" onClick={onClose}>保留して閉じる</button></footer>
    </section>
  </div>
}

function DraftConflictModal({ active, target, onHoldAndRestore, onDiscardAndRestore, onCancel }: { active: DraftRecord; target: DraftRecord; onHoldAndRestore: () => void; onDiscardAndRestore: () => void; onCancel: () => void }) {
  return <div className="modal-backdrop notification-center-backdrop" role="presentation">
    <section className="modal draft-conflict-modal" role="dialog" aria-modal="true" aria-labelledby="draft-conflict-title">
      <header className="notification-center-header"><div><span className="page-eyebrow">DRAFT CONFLICT</span><h2 id="draft-conflict-title">作成途中の入力があります</h2></div><button className="icon-button" type="button" aria-label="復元をキャンセル" onClick={onCancel}><X size={21} /></button></header>
      <div className="draft-conflict-body"><p>現在の入力を残したまま、別の作成途中データを復元しようとしています。</p><div className="draft-conflict-items"><div><small>現在の入力</small><strong>{draftKindLabel(active.kind)} ・ {draftSummary(active)}</strong></div><div><small>復元する入力</small><strong>{draftKindLabel(target.kind)} ・ {draftSummary(target)}</strong></div></div></div>
      <footer className="draft-recovery-footer"><button className="button button-secondary" type="button" onClick={onCancel}>キャンセル</button><button className="button button-secondary" type="button" onClick={onDiscardAndRestore}><Trash2 size={15} />現在の入力を削除して復元</button><button className="button button-primary" type="button" onClick={onHoldAndRestore}>現在の入力を保留して復元</button></footer>
    </section>
  </div>
}
