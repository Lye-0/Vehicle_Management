import { useCallback, useEffect, useState } from 'react'
import { Archive, ChevronDown, FolderOpen, HardDrive, RotateCcw } from 'lucide-react'
import { createBackup, deleteBackup, exportBackup, fetchBackups, restoreBackup, updateBackupRetention, type BackupRecord, type BackupSettings } from '../lib/backupsApi'
import { changePcBackupDirectory, saveBackupToPc } from '../lib/pcBackup'

export function BackupSettingsPanel({ backupSettings, onBackupSettingsChange }: { backupSettings: BackupSettings; onBackupSettingsChange: (settings: BackupSettings) => void }) {
  const [backups, setBackups] = useState<BackupRecord[]>([])
  const [canManage, setCanManage] = useState(false)
  const [loading, setLoading] = useState('')
  const [error, setError] = useState('')
  const [message, setMessage] = useState('')
  const [manualBackupNote, setManualBackupNote] = useState('')
  const [manualDestination, setManualDestination] = useState<BackupSettings['destination']>(backupSettings.destination)
  const [periodicOpen, setPeriodicOpen] = useState(false)
  const [manualOpen, setManualOpen] = useState(false)

  const load = useCallback(async () => {
    try {
      const backupResponse = await fetchBackups()
      setBackups(backupResponse.backups)
      setCanManage(backupResponse.canManage)
      setError('')
    } catch (reason: unknown) {
      setError(reason instanceof Error ? reason.message : 'バックアップ情報を読み込めませんでした。')
    }
  }, [])

  useEffect(() => { void load() }, [load])

  async function runChangePcBackupDirectory() {
    setLoading('pc-directory')
    setError('')
    setMessage('')
    try {
      const result = await changePcBackupDirectory()
      if (result.mode === 'cancelled') return
      if (result.mode === 'unsupported') {
        setError('このブラウザではPCの保存先を指定できません。バックアップ実行時はダウンロードとして保存されます。')
        return
      }
      setMessage(`${result.directoryName}をPCバックアップ先に設定しました。`)
    } catch (reason: unknown) {
      setError(reason instanceof Error ? reason.message : 'PCバックアップ先を変更できませんでした。')
    } finally {
      setLoading('')
    }
  }

  async function runManualBackup() {
    setLoading('manual-create')
    setError('')
    setMessage('')
    const hasB2 = manualDestination === 'b2' || manualDestination === 'both'
    const hasPc = manualDestination === 'pc' || manualDestination === 'both'
    try {
      let b2Created = false
      if (hasB2) {
        await createBackup(manualBackupNote)
        b2Created = true
      }
      let pcMessage = ''
      if (hasPc) {
        const backup = await exportBackup(manualBackupNote)
        const result = await saveBackupToPc(backup, false, backupSettings.pcRetentionDays)
        pcMessage = result.mode === 'folder' ? `${result.directoryName}にPC保存` : 'PCへダウンロード'
      }
      setManualBackupNote('')
      setMessage(hasB2 && hasPc ? `B2保存と${pcMessage}が完了しました。` : b2Created ? 'B2へのバックアップを作成しました。' : `${pcMessage}が完了しました。`)
      if (b2Created) await load()
    } catch (reason: unknown) {
      setError(reason instanceof Error ? reason.message : '手動バックアップを作成できませんでした。')
    } finally {
      setLoading('')
    }
  }

  async function runRestore(backup: BackupRecord) {
    if (!window.confirm(`復元前に現在の状態をB2へ保存してから、このバックアップ（${formatBackupDate(backup.createdAt)}）で組織データを置き換えます。続行しますか？`)) return
    setLoading(`restore-${backup.id}`)
    setError('')
    setMessage('')
    try {
      const response = await restoreBackup(backup.id)
      setMessage(`${response.rowCount}件のデータを復元しました。復元前のバックアップ（${response.safetyBackupId}）も保存されています。画面を再読み込みします。`)
      window.setTimeout(() => window.location.reload(), 1_200)
    } catch (reason: unknown) {
      setError(reason instanceof Error ? reason.message : 'バックアップを復元できませんでした。')
    } finally {
      setLoading('')
    }
  }

  async function runDelete(backup: BackupRecord) {
    if (!window.confirm(`このバックアップ（${formatBackupDate(backup.createdAt)}）を削除しますか？`)) return
    setLoading(`delete-${backup.id}`)
    setError('')
    setMessage('')
    try {
      await deleteBackup(backup.id)
      setBackups((current) => current.filter((item) => item.id !== backup.id))
      setMessage('バックアップを削除しました。')
    } catch (reason: unknown) {
      setError(reason instanceof Error ? reason.message : 'バックアップを削除できませんでした。')
    } finally {
      setLoading('')
    }
  }

  async function runBackupKeepForever(backup: BackupRecord) {
    setLoading(`keep-${backup.id}`)
    setError('')
    setMessage('')
    try {
      const response = await updateBackupRetention(backup.id, !backup.keepForever)
      setBackups((current) => current.map((item) => item.id === backup.id ? { ...item, keepForever: response.keepForever } : item))
      setMessage(response.keepForever ? 'バックアップを永久保存にしました。' : 'バックアップの永久保存を解除しました。')
    } catch (reason: unknown) {
      setError(reason instanceof Error ? reason.message : 'バックアップの保存期間を変更できませんでした。')
    } finally {
      setLoading('')
    }
  }

  const periodicSummary = backupSettings.autoEnabled ? `${backupSettings.frequency === 'daily' ? '毎日' : '毎週'}・オンライン（B2）` : '停止中'
  return <section className="panel settings-panel backup-panel">
    <div className="settings-section-heading"><Archive size={18} /><div><h2>バックアップ・復元</h2><p>D1の組織データとB2の車両添付ファイルをまとめて保存します。</p></div></div>
    {error && <div className="auth-error" role="alert">{error}</div>}
    {message && <div className="settings-success" role="status">{message}</div>}
    {!canManage && !error && <div className="backup-notice">バックアップの作成・復元・設定変更は管理者のみ実行できます。</div>}

    <section className="backup-retention-settings"><div className="backup-retention-title-row"><div className="backup-retention-heading"><strong>保存期間</strong><span>定期・手動バックアップに共通</span></div>{canManage && <button className="button button-secondary backup-directory-button" type="button" disabled={Boolean(loading)} onClick={() => void runChangePcBackupDirectory()}>{loading === 'pc-directory' ? '選択中…' : <><FolderOpen size={14} />PCへのバックアップ先を変更</>}</button>}</div><div className="backup-retention-values"><label><span>オンライン（B2）</span><span className="settings-number-input"><input type="number" min={7} max={3650} value={backupSettings.retentionDays} disabled={!canManage || Boolean(loading)} onChange={(event) => onBackupSettingsChange({ ...backupSettings, retentionDays: Number(event.target.value) })} /><span>日</span></span></label><label><span>PC</span><span className="settings-number-input"><input type="number" min={1} max={3650} value={backupSettings.pcRetentionDays} disabled={!canManage || Boolean(loading)} onChange={(event) => onBackupSettingsChange({ ...backupSettings, pcRetentionDays: Number(event.target.value) })} /><span>日</span></span></label></div><p className="backup-destination-note">永久保存に設定していないバックアップは、指定期間を過ぎると削除されます。PC保存では、選択した親フォルダ内の「Vehicle Management Backup」へ保存します。</p></section>

    <section className="backup-accordion">
      <div className="backup-accordion-header">
        <button className="backup-accordion-trigger" type="button" aria-expanded={periodicOpen} aria-controls="periodic-backup-settings" onClick={() => setPeriodicOpen((open) => !open)}>
          <span className="backup-accordion-heading"><strong>定期バックアップ</strong><small>{periodicSummary}</small></span><ChevronDown size={16} aria-hidden="true" />
        </button>
        <label className="backup-toggle"><span className="sr-only">定期バックアップを有効にする</span><input type="checkbox" checked={backupSettings.autoEnabled} disabled={!canManage || Boolean(loading)} onChange={(event) => { onBackupSettingsChange({ ...backupSettings, autoEnabled: event.target.checked }); setPeriodicOpen(event.target.checked) }} /><span className="backup-toggle-track" aria-hidden="true"><span /></span></label>
      </div>
      {periodicOpen && <div className="backup-accordion-content" id="periodic-backup-settings">
        <div className="backup-settings-table" role="table" aria-label="定期バックアップ設定">
          <div className="backup-settings-table-row backup-settings-table-head" role="row"><span role="columnheader">設定項目</span><span role="columnheader">オンライン（B2）</span><span role="columnheader">PC</span></div>
          <div className="backup-settings-table-row" role="row"><span className="backup-settings-table-label" role="rowheader">定期バックアップ</span><span className="backup-table-value" role="cell">上のトグルで設定</span><span className="backup-table-unavailable" role="cell">未実装</span></div>
          <div className="backup-settings-table-row" role="row"><span className="backup-settings-table-label" role="rowheader">頻度</span><span role="cell"><select value={backupSettings.frequency} disabled={!canManage || Boolean(loading)} onChange={(event) => onBackupSettingsChange({ ...backupSettings, frequency: event.target.value as BackupSettings['frequency'] })}><option value="daily">毎日</option><option value="weekly">毎週</option></select></span><span className="backup-table-unavailable" role="cell">未実装</span></div>
        </div>
        <p className="backup-destination-note">定期バックアップは現在オンライン（B2）のみ対応しています。PCへの手動保存は下の「今すぐ手動バックアップ」から実行できます。</p>
      </div>}
    </section>

    <section className="backup-accordion">
      <button className="backup-accordion-trigger backup-accordion-trigger-full" type="button" aria-expanded={manualOpen} aria-controls="manual-backup-settings" onClick={() => setManualOpen((open) => !open)}><span className="backup-accordion-heading"><strong>今すぐ手動バックアップ</strong></span><ChevronDown size={16} aria-hidden="true" /></button>
      {manualOpen && <div className="backup-accordion-content" id="manual-backup-settings">
        <div className="backup-manual-grid">
          <label className="backup-note-field"><span>手動バックアップのメモ（任意）</span><textarea value={manualBackupNote} maxLength={500} rows={3} disabled={!canManage || Boolean(loading)} onChange={(event) => setManualBackupNote(event.target.value)} placeholder="例：請求書修正前" /></label>
          <label className="backup-settings-field"><span>保存先</span><select value={manualDestination} disabled={!canManage || Boolean(loading)} onChange={(event) => setManualDestination(event.target.value as BackupSettings['destination'])}><option value="b2">オンライン（B2）</option><option value="pc">PC</option><option value="both">両方</option></select></label>
          {canManage && <div className="backup-manual-execute"><button className="button button-secondary backup-execute-button" type="button" disabled={Boolean(loading)} onClick={() => void runManualBackup()}>{loading === 'manual-create' ? '作成中…' : <><HardDrive size={14} />バックアップを実行</>}</button></div>}
        </div>
        <p className="backup-destination-note">PC保存では、ユーザーが選択したフォルダへバックアップファイルを保存します。フォルダを選択できない環境ではダウンロードとして保存されます。</p>
      </div>}
    </section>

    <section className="backup-history-section"><div className="backup-history-heading"><h3>バックアップ一覧</h3><span>{backups.length}件</span></div>{backups.length === 0 ? <div className="settings-empty backup-empty"><Archive size={26} /><span>バックアップ履歴はありません。</span></div> : <div className="backup-list">{backups.map((backup) => <div className="backup-row" key={backup.id}><span className="backup-icon"><Archive size={17} /></span><span className="backup-copy"><strong>{formatBackupDate(backup.createdAt)} ・ {backup.trigger === 'automatic' ? '自動' : backup.trigger === 'pre-restore' ? '復元前' : '手動'}</strong><small>{backup.rowCount}行 ・ 添付{backup.fileCount}件{backup.keepForever ? ' ・ 永久保存' : ''}{backup.protectedUntil ? ` ・ ${formatBackupDate(backup.protectedUntil)}まで保護` : ''}</small>{backup.note && <small className="backup-note-text">メモ：{backup.note}</small>}</span>{canManage && <span className="backup-actions"><button className="button button-secondary" type="button" disabled={Boolean(loading)} onClick={() => void runRestore(backup)}>{loading === `restore-${backup.id}` ? '復元中…' : <><RotateCcw size={14} />復元</>}</button><button className="text-button" type="button" disabled={Boolean(loading)} onClick={() => void runBackupKeepForever(backup)}>{loading === `keep-${backup.id}` ? '更新中…' : backup.keepForever ? '保存解除' : '永久保存'}</button><button className="text-button" type="button" disabled={Boolean(loading)} onClick={() => void runDelete(backup)}>{loading === `delete-${backup.id}` ? '削除中…' : '削除'}</button></span>}</div>)}</div>}</section>
  </section>
}

function formatBackupDate(value: string) {
  const date = new Date(value.replace(' ', 'T'))
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString('ja-JP')
}
