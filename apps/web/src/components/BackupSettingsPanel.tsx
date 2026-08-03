import { useCallback, useEffect, useState } from 'react'
import { AlertTriangle, Archive, ChevronDown, Clock3, FileUp, FolderOpen, HardDrive, RotateCcw, ShieldCheck, Trash2, X } from 'lucide-react'
import { createBackup, deleteBackup, exportBackup, fetchBackups, restoreBackup, restoreImportedBackup, updateBackupRetention, type BackupRecord, type BackupSettings } from '../lib/backupsApi'
import { changePcBackupDirectory, choosePcBackupDirectory, deletePcBackupFile, listDefaultPcBackups, preparePcBackupDestination, readPcBackup, saveBackupToPc, updatePcBackupFileRetention, type PcBackupFile, type PcBackupListing } from '../lib/pcBackup'
import { IconWithChain } from './IconWithChain'

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
  const [pcRestoreOpen, setPcRestoreOpen] = useState(false)
  const [pcRestoreFiles, setPcRestoreFiles] = useState<PcBackupFile[]>([])
  const [pcRestoreSelected, setPcRestoreSelected] = useState<PcBackupFile | null>(null)
  const [pcRestoreSource, setPcRestoreSource] = useState<'default' | 'custom'>('default')
  const [pcRestoreParentName, setPcRestoreParentName] = useState<string | null>(null)
  const [pcRestoreDirectoryName, setPcRestoreDirectoryName] = useState<string | null>(null)
  const [pcRestoreLoading, setPcRestoreLoading] = useState(false)
  const [pcRestoreAction, setPcRestoreAction] = useState('')
  const [pcRestoreError, setPcRestoreError] = useState('')
  const [pcBackupWarningOpen, setPcBackupWarningOpen] = useState(false)

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
      if (result.mode === 'cancelled') {
        setPcBackupWarningOpen(true)
        return
      }
      if (result.mode === 'unsupported') {
        setPcBackupWarningOpen(true)
        return
      }
      setMessage(`${result.directoryName}をPCバックアップ先に設定しました。`)
    } catch {
      setPcBackupWarningOpen(true)
    } finally {
      setLoading('')
    }
  }

  function applyPcRestoreListing(source: 'default' | 'custom', listing: PcBackupListing) {
    setPcRestoreSource(source)
    setPcRestoreParentName(listing.parentName)
    setPcRestoreDirectoryName(listing.directoryName)
    setPcRestoreFiles(listing.files)
    setPcRestoreSelected(null)
  }

  async function openPcRestore() {
    setPcRestoreOpen(true)
    setPcRestoreError('')
    setPcRestoreSource('default')
    setPcRestoreParentName(null)
    setPcRestoreDirectoryName(null)
    setPcRestoreFiles([])
    setPcRestoreSelected(null)
    await loadDefaultPcBackups()
  }

  async function loadDefaultPcBackups() {
    setPcRestoreLoading(true)
    setPcRestoreError('')
    try {
      const listing = await listDefaultPcBackups()
      applyPcRestoreListing('default', listing)
      if (!listing.available) setPcRestoreError(listing.parentName ? '既定のPCバックアップ先を読み込めませんでした。保存先の権限を確認してください。' : '既定のPCバックアップ先がまだ設定されていません。別のフォルダを選択してください。')
    } catch (reason: unknown) {
      setPcRestoreError(reason instanceof Error ? reason.message : '既定のPCバックアップ一覧を読み込めませんでした。')
    } finally {
      setPcRestoreLoading(false)
    }
  }

  async function runChoosePcRestoreDirectory() {
    setPcRestoreLoading(true)
    setPcRestoreError('')
    try {
      const result = await choosePcBackupDirectory()
      if (result.mode === 'cancelled') return
      if (result.mode === 'unsupported') {
        setPcRestoreError('このブラウザではフォルダを選択できません。')
        return
      }
      applyPcRestoreListing('custom', result)
    } catch (reason: unknown) {
      setPcRestoreError(reason instanceof Error ? reason.message : '選択したフォルダを読み込めませんでした。')
    } finally {
      setPcRestoreLoading(false)
    }
  }

  async function runPcRestore() {
    if (!pcRestoreSelected) return
    if (!window.confirm(`PCバックアップ（${formatBackupDate(pcRestoreSelected.createdAt)}）を復元します。復元前に現在の状態をB2へ保存してから、組織データを置き換えます。続行しますか？`)) return
    setLoading('pc-restore')
    setPcRestoreError('')
    setError('')
    setMessage('')
    try {
      const backup = await readPcBackup(pcRestoreSelected.name)
      const response = await restoreImportedBackup(backup)
      setPcRestoreOpen(false)
      setMessage(`${response.rowCount}件のデータを復元しました。復元前のバックアップ（${response.safetyBackupId}）も保存されています。画面を再読み込みします。`)
      window.setTimeout(() => window.location.reload(), 1_200)
    } catch (reason: unknown) {
      setPcRestoreError(reason instanceof Error ? reason.message : 'PCバックアップを復元できませんでした。')
    } finally {
      setLoading('')
    }
  }

  async function runPcRestoreKeepForever(file: PcBackupFile) {
    setPcRestoreAction(`keep-${file.name}`)
    setPcRestoreError('')
    try {
      const updated = await updatePcBackupFileRetention(file.name, !file.keepForever)
      setPcRestoreFiles((current) => current.map((item) => item.name === file.name ? updated : item))
      setPcRestoreSelected((current) => current?.name === file.name ? updated : current)
    } catch (reason: unknown) {
      setPcRestoreError(reason instanceof Error ? reason.message : 'PCバックアップの保存期間を変更できませんでした。')
    } finally {
      setPcRestoreAction('')
    }
  }

  async function runDeletePcBackup(file: PcBackupFile) {
    if (!window.confirm(`このPCバックアップ（${formatBackupDate(file.createdAt)}）を削除しますか？この操作は元に戻せません。`)) return
    setPcRestoreAction(`delete-${file.name}`)
    setPcRestoreError('')
    try {
      await deletePcBackupFile(file.name)
      setPcRestoreFiles((current) => current.filter((item) => item.name !== file.name))
      setPcRestoreSelected((current) => current?.name === file.name ? null : current)
    } catch (reason: unknown) {
      setPcRestoreError(reason instanceof Error ? reason.message : 'PCバックアップを削除できませんでした。')
    } finally {
      setPcRestoreAction('')
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
      if (hasPc && !await preparePcBackupDestination()) {
        setPcBackupWarningOpen(true)
        return
      }
      if (hasB2) {
        await createBackup(manualBackupNote)
        b2Created = true
      }
      let pcMessage = ''
      if (hasPc) {
        const backup = await exportBackup(manualBackupNote)
        const result = await saveBackupToPc(backup, false, backupSettings.pcRetentionDays)
        if (result.mode === 'unavailable') {
          setPcBackupWarningOpen(true)
          return
        }
        pcMessage = `${result.directoryName}にPC保存`
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
      setBackups((current) => current.map((item) => item.id === backup.id ? { ...item, keepForever: response.keepForever, protectedUntil: response.protectedUntil } : item))
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

    <section className="backup-retention-settings">
      <div className="backup-retention-title-row">
        <div className="backup-retention-heading"><strong>バックアップの保存設定</strong><span>保存期間とPC保存先</span></div>
      </div>
      <div className="backup-retention-values">
        <div className="backup-retention-field"><div className="backup-retention-field-heading"><label htmlFor="backup-retention-b2">オンライン（B2）</label></div><span className="settings-number-input"><input id="backup-retention-b2" type="number" min={7} max={3650} value={backupSettings.retentionDays} disabled={!canManage || Boolean(loading)} onChange={(event) => onBackupSettingsChange({ ...backupSettings, retentionDays: Number(event.target.value) })} /><span>日</span></span></div>
        <div className="backup-retention-field"><div className="backup-retention-field-heading"><label htmlFor="backup-retention-pc">PC保存（期間）</label></div><span className="settings-number-input"><input id="backup-retention-pc" type="number" min={1} max={3650} value={backupSettings.pcRetentionDays} disabled={!canManage || Boolean(loading)} onChange={(event) => onBackupSettingsChange({ ...backupSettings, pcRetentionDays: Number(event.target.value) })} /><span>日</span></span></div>
      </div>
      <div className="backup-retention-destination-row"><div className="backup-retention-destination-copy"><strong>PC保存先</strong><span>選択した親フォルダ内に「Vehicle Management Backup」フォルダを作成して保存します。</span></div>{canManage && <div className="backup-retention-destination-action"><button className="button button-secondary backup-directory-button" type="button" disabled={Boolean(loading)} onClick={() => void runChangePcBackupDirectory()}>{loading === 'pc-directory' ? '選択中…' : <><FolderOpen size={14} />PC保存先を変更</>}</button></div>}</div>
      <p className="backup-destination-note">永久保存に設定していないバックアップは、指定期間を過ぎると自動削除されます。</p>
    </section>

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
        <p className="backup-destination-note">PC保存では、選択した親フォルダ内の「Vehicle Management Backup」へ保存します。PC保存先を利用できない場合は警告を表示して処理を中止します。</p>
      </div>}
    </section>

    <section className="backup-history-section">
      <div className="backup-history-heading"><h3>バックアップ一覧</h3><div className="backup-history-heading-actions"><span>{backups.length}件</span>{canManage && <button className="button button-secondary backup-history-restore-button" type="button" disabled={Boolean(loading) || pcRestoreLoading || Boolean(pcRestoreAction)} onClick={() => void openPcRestore()}><FileUp size={14} />PCからバックアップを復元</button>}</div></div>
      {backups.length === 0 ? <div className="settings-empty backup-empty"><Archive size={26} /><span>バックアップ履歴はありません。</span></div> : <div className="backup-list">{backups.map((backup) => <div className="backup-row" key={backup.id}>
        <IconWithChain visible={backup.keepForever} className="backup-icon-chain" chainWidth="100%" chainTop="-2%" chainDepth={18} linkThickness={2.5} linkSize={6}><span className="backup-icon"><Archive size={17} /></span></IconWithChain>
        <span className="backup-copy"><strong>{formatBackupDate(backup.createdAt)} ・ {backup.trigger === 'automatic' ? '自動' : backup.trigger === 'pre-restore' ? '復元前' : '手動'}</strong><small>{backup.rowCount}行 ・ 添付{backup.fileCount}件</small>{!backup.keepForever && getBackupExpiration(backup, backupSettings.retentionDays) && <small className="backup-expiration-text">期限：{formatBackupDate(getBackupExpiration(backup, backupSettings.retentionDays) ?? '')}</small>}{backup.note && <small className="backup-note-text">メモ：{backup.note}</small>}</span>
        <div className="backup-meta"><span className={`backup-retention-status${backup.keepForever ? ' is-forever' : ''}`}>{backup.keepForever ? <><ShieldCheck size={13} />永久保存</> : <><Clock3 size={13} />自動削除予定</>}</span>{canManage && <span className="backup-actions"><button className="button button-secondary" type="button" disabled={Boolean(loading)} onClick={() => void runRestore(backup)}>{loading === `restore-${backup.id}` ? '復元中…' : <><RotateCcw size={14} />復元</>}</button><button className="button button-secondary backup-retention-button" type="button" disabled={Boolean(loading)} onClick={() => void runBackupKeepForever(backup)}>{loading === `keep-${backup.id}` ? '更新中…' : backup.keepForever ? <><ShieldCheck size={13} />保存解除</> : <><ShieldCheck size={13} />永久保存</>}</button><button className="button button-danger backup-delete-button" type="button" disabled={Boolean(loading)} onClick={() => void runDelete(backup)}>{loading === `delete-${backup.id}` ? '削除中…' : <><Trash2 size={13} />削除</>}</button></span>}</div>
      </div>)}</div>}
    </section>

    {pcRestoreOpen && <div className="modal-backdrop pc-restore-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) setPcRestoreOpen(false) }}>
      <section className="modal pc-restore-modal" role="dialog" aria-modal="true" aria-labelledby="pc-restore-title">
        <div className="modal-header pc-restore-modal-header"><div><h2 id="pc-restore-title">PCバックアップから復元</h2><p>復元するバックアップを選択してください。</p></div><button className="modal-close" type="button" aria-label="閉じる" onClick={() => setPcRestoreOpen(false)}><X size={18} /></button></div>
        <div className="pc-restore-modal-content">
          <div className="pc-restore-directory-bar">
            <div className="pc-restore-directory-copy"><strong>{pcRestoreSource === 'default' ? '既定の保存先' : '一時的に選択したフォルダ'}</strong><small>{formatPcDirectoryLabel(pcRestoreParentName, pcRestoreDirectoryName)}</small></div>
            <div className="pc-restore-directory-actions"><button className="button button-secondary" type="button" disabled={pcRestoreLoading || Boolean(loading) || Boolean(pcRestoreAction)} onClick={() => void runChoosePcRestoreDirectory()}><FolderOpen size={14} />別のフォルダを選択</button><button className="button button-secondary" type="button" disabled={pcRestoreSource === 'default' || pcRestoreLoading || Boolean(loading) || Boolean(pcRestoreAction)} onClick={() => void loadDefaultPcBackups()}>既定のフォルダを選択</button></div>
          </div>
          {pcRestoreError && <div className="pc-restore-error" role="alert">{pcRestoreError}</div>}
          {pcRestoreLoading ? <div className="pc-restore-empty"><span>バックアップ一覧を読み込んでいます…</span></div> : pcRestoreFiles.length === 0 ? <div className="pc-restore-empty"><Archive size={26} /><strong>バックアップが見つかりません</strong><span>選択したフォルダ内に復元できるバックアップファイルがありません。</span></div> : <div className="pc-restore-list" role="listbox" aria-label="PCバックアップ一覧">{pcRestoreFiles.map((file) => <div className={`pc-restore-file${pcRestoreSelected?.name === file.name ? ' is-selected' : ''}`} key={file.name} role="option" aria-selected={pcRestoreSelected?.name === file.name}>
            <button className="pc-restore-file-select" type="button" disabled={Boolean(loading) || Boolean(pcRestoreAction)} onClick={() => setPcRestoreSelected(file)}>
              <IconWithChain visible={file.keepForever} className="pc-restore-icon-chain" chainWidth="88%" chainTop="-2%" chainDepth={18} linkThickness={2} linkSize={5}><span className="pc-restore-file-icon"><Archive size={16} /></span></IconWithChain>
              <span className="pc-restore-file-copy"><strong>{formatBackupDate(file.createdAt)}</strong><small className="pc-restore-file-name">{formatFileSize(file.size)} ・ {file.name}</small><small>{file.rowCount}行 ・ 添付{file.fileCount}件</small>{!file.keepForever && getPcBackupExpiration(file, backupSettings.pcRetentionDays) && <small className="pc-restore-expiration-text">期限：{formatBackupDate(getPcBackupExpiration(file, backupSettings.pcRetentionDays) ?? '')}</small>}{file.note && <small className="pc-restore-note-text">メモ：{file.note}</small>}</span>
            </button>
            <div className="pc-restore-file-meta"><span className={`pc-restore-retention${file.keepForever ? ' is-forever' : ''}`}>{file.keepForever ? <><ShieldCheck size={13} />永久保存</> : <><Clock3 size={13} />自動削除予定</>}</span><span className="pc-restore-file-actions"><button className="button button-secondary pc-restore-retention-button" type="button" disabled={Boolean(loading) || Boolean(pcRestoreAction)} onClick={() => void runPcRestoreKeepForever(file)}>{pcRestoreAction === `keep-${file.name}` ? '更新中…' : file.keepForever ? <><ShieldCheck size={13} />保存解除</> : <><ShieldCheck size={13} />永久保存</>}</button><button className="button button-danger pc-restore-delete-button" type="button" disabled={Boolean(loading) || Boolean(pcRestoreAction)} onClick={() => void runDeletePcBackup(file)}>{pcRestoreAction === `delete-${file.name}` ? '削除中…' : <><Trash2 size={13} />削除</>}</button></span></div>
          </div>)}</div>}
          <div className="pc-restore-modal-footer"><button className="button button-secondary" type="button" disabled={Boolean(loading) || Boolean(pcRestoreAction)} onClick={() => setPcRestoreOpen(false)}>キャンセル</button><button className="button button-primary" type="button" disabled={!pcRestoreSelected || Boolean(loading) || pcRestoreLoading || Boolean(pcRestoreAction)} onClick={() => void runPcRestore()}>{loading === 'pc-restore' ? '復元中…' : <><RotateCcw size={14} />選択したバックアップを復元</>}</button></div>
        </div>
      </section>
    </div>}

    {pcBackupWarningOpen && <div className="modal-backdrop pc-backup-warning-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) setPcBackupWarningOpen(false) }}>
      <section className="modal pc-backup-warning-modal" role="dialog" aria-modal="true" aria-labelledby="pc-backup-warning-title">
        <div className="modal-header pc-backup-warning-header"><div className="pc-backup-warning-title"><AlertTriangle size={20} /><h2 id="pc-backup-warning-title">PCへバックアップできませんでした</h2></div><button className="modal-close" type="button" aria-label="閉じる" onClick={() => setPcBackupWarningOpen(false)}><X size={18} /></button></div>
        <div className="pc-backup-warning-content">
          <p className="pc-backup-warning-lead">選択したフォルダをPC保存先として利用できないため、今回のバックアップ処理を中止しました。通常のダウンロード保存は行っていません。</p>
          <div className="pc-backup-warning-instructions"><strong>PCに保存する場合</strong><ol><li>システムファイルを含まない空の親フォルダを作成します。</li><li>その中に <code>Vehicle Management Backup</code> フォルダを手動で作成します。</li><li>「PC保存先を変更」から、作成したフォルダを含む親フォルダを選択します。</li></ol></div>
          <p className="pc-backup-warning-note">例：新しく作成した空のフォルダを選択すると、その中の「Vehicle Management Backup」にバックアップを保存できます。</p>
          <div className="pc-backup-warning-footer"><button className="button button-secondary" type="button" onClick={() => setPcBackupWarningOpen(false)}>閉じる</button></div>
        </div>
      </section>
    </div>}
  </section>
}

function formatBackupDate(value: string) {
  const date = new Date(value.replace(' ', 'T'))
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString('ja-JP')
}

function formatPcDirectoryLabel(parentName: string | null, directoryName: string | null) {
  if (!parentName && !directoryName) return 'PC保存先が未設定です'
  if (!directoryName || parentName === directoryName) return directoryName ?? parentName ?? '保存先を確認できません'
  return `${parentName} / ${directoryName}`
}

function formatFileSize(value: number) {
  if (value < 1024) return `${value} B`
  if (value < 1024 ** 2) return `${(value / 1024).toFixed(1)} KB`
  return `${(value / 1024 ** 2).toFixed(1)} MB`
}

function getBackupExpiration(backup: BackupRecord, retentionDays: number) {
  return backup.protectedUntil ?? addRetentionDays(backup.createdAt, retentionDays)
}

function getPcBackupExpiration(file: PcBackupFile, retentionDays: number) {
  return addRetentionDays(file.lastModified, retentionDays)
}

function addRetentionDays(value: string | number, days: number) {
  const date = typeof value === 'number' ? new Date(value) : new Date(value.replace(' ', 'T'))
  if (Number.isNaN(date.getTime())) return null
  date.setUTCDate(date.getUTCDate() + days)
  return date.toISOString()
}
