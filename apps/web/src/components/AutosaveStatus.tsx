import type { AutosaveStatus as AutosaveState } from '../hooks/useAutosave'

export function AutosaveStatus({ status, lastSavedAt }: { status: AutosaveState; lastSavedAt: number | null }) {
  if (status === 'idle') return null
  const label = status === 'waiting'
    ? '変更あり・自動保存を待機中'
    : status === 'local-saved'
      ? '端末に保存済み・サーバー保存を待機中'
      : status === 'saving'
        ? '自動保存中…'
        : status === 'saved'
          ? `自動保存済み${lastSavedAt ? `（${new Date(lastSavedAt).toLocaleTimeString('ja-JP', { hour: '2-digit', minute: '2-digit' })}）` : ''}`
          : status === 'blocked'
            ? '確認が必要なため自動保存を保留中'
            : '未同期・保存を再試行してください'
  return <span className={`autosave-status autosave-status-${status}`} role="status" aria-live="polite">{label}</span>
}
