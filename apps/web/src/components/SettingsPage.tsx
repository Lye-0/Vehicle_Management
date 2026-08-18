import { useCallback, useEffect, useState, type ChangeEvent, type DragEvent as ReactDragEvent, type FormEvent } from 'react'
import type { User } from 'firebase/auth'
import { normalizePhone, normalizePostalCode, type NormalizableField } from '@vehicle-management/shared'
import { Archive, Banknote, Building2, CheckCircle2, Clock3, Copy, Download, FileText, FileUp, Plus, ReceiptText, RotateCcw, Save, Search, Settings2, ShieldCheck, Table2, Trash2, Upload, UserPlus, UserRound, UsersRound } from 'lucide-react'
import { fetchOrganizationPermissions, updateCurrentProfile, updateOrganizationPermissions, type OrganizationPermissions } from '../lib/organizationApi'
import { apiFetchBlob } from '../lib/api'
import { addEmailPasswordLogin, addGoogleLogin, changeCurrentDisplayName, changeCurrentEmail, changeCurrentPassword, refreshCurrentUser, removeLoginProvider, sendCurrentEmailVerification } from '../lib/auth'
import { defaultSettings, fetchSettings, flattenSalesItemPresetGroups, updateSettings, type AppSettings, type DocumentSettings, type SalesItemPresetGroupKey, type SalesItemPresetGroups, type ShopSettings, type TaxSettings } from '../lib/settingsApi'
import { createMember, fetchMembers, removeMemberFromOrganization, updateMember, type MemberRecord, type MemberRole } from '../lib/membersApi'
import { commitCsvImport, previewCsvImport, type CsvImportPreview, type CsvImportResource, type CsvImportResult } from '../lib/importApi'
import { fetchBackupSettings, updateBackupSettings, type BackupSettings } from '../lib/backupsApi'
import { deleteArchive, fetchArchives, restoreArchive, updateArchiveRetention, type ArchiveRecord } from '../lib/archivesApi'
import { BackupSettingsPanel } from './BackupSettingsPanel'
import { AbacusRegistrationPackagePanel } from './AbacusRegistrationPackagePanel'
import { IconWithChain } from './IconWithChain'
import { NormalizedInput } from './NormalizedValueInput'

type SettingsTab = 'shop' | 'tax' | 'masters' | 'archive' | 'data' | 'members' | 'permissions'
type CsvResource = 'customers' | 'vehicles' | 'sales' | 'maintenance' | 'payments'

const initialBackupSettings: BackupSettings = { autoEnabled: false, frequency: 'daily', destination: 'b2', retentionDays: 30, archiveRetentionDays: 30, pcRetentionDays: 30 }
const initialOrganizationPermissions: OrganizationPermissions = { employeeCanExportCsv: true, employeeCanEditShop: true, employeeCanEditTax: true, employeeCanCreateRestoreBackup: true, employeeCanManageBackupRetention: false, employeeCanManageArchiveRetention: false }

const tabs: Array<{ id: SettingsTab; label: string; description: string; icon: typeof Building2 }> = [
  { id: 'shop', label: '店舗情報', description: '店舗情報と帳票に表示する内容', icon: Building2 },
  { id: 'tax', label: '税・端数処理', description: '消費税と請求期限の初期値', icon: ReceiptText },
  { id: 'masters', label: '明細候補', description: '販売・整備で選べる項目', icon: Settings2 },
  { id: 'archive', label: 'アーカイブ', description: '削除した書類の復元と整理', icon: Archive },
  { id: 'data', label: 'データ', description: 'データの入出力とバックアップ', icon: Table2 },
  { id: 'members', label: '管理者・従業員', description: 'ユーザーとログイン情報', icon: UsersRound },
  { id: 'permissions', label: '権限管理', description: '従業員に許可する操作', icon: ShieldCheck },
]

const salesPresetColumns: Array<{ key: SalesItemPresetGroupKey; title: string; description: string }> = [
  { key: 'vehiclePrice', title: '車両販売価格内訳', description: '車両価格欄の自由入力行で表示します。' },
  { key: 'fees', title: '諸費用内訳', description: '法定費用・手続費用・実費欄で表示します。' },
  { key: 'accessories', title: '付属品・特別仕様明細', description: '付属品・特別仕様の品名欄で表示します。' },
]

export function SettingsPage({ user, onReloadSession, onUserUpdated }: { user: User; onReloadSession?: () => void; onUserUpdated?: (user: User) => void }) {
  const [settings, setSettings] = useState<AppSettings>(defaultSettings)
  const [backupSettings, setBackupSettings] = useState<BackupSettings>(initialBackupSettings)
  const [backupPermissions, setBackupPermissions] = useState({ canManageCreateRestore: false, canManageRetention: false })
  const [permissions, setPermissions] = useState<OrganizationPermissions>(initialOrganizationPermissions)
  const [canManagePermissions, setCanManagePermissions] = useState(false)
  const [activeTab, setActiveTab] = useState<SettingsTab>('shop')
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [saved, setSaved] = useState(false)
  const [error, setError] = useState('')
  const [exporting, setExporting] = useState<CsvResource | ''>('')

  useEffect(() => {
    let cancelled = false
    Promise.all([fetchSettings(), fetchBackupSettings(), fetchOrganizationPermissions()])
      .then(([nextSettings, backupResponse, permissionsResponse]) => {
        if (!cancelled) {
          setSettings(nextSettings)
          setBackupSettings(backupResponse.settings)
          setBackupPermissions({ canManageCreateRestore: backupResponse.canManageCreateRestore, canManageRetention: backupResponse.canManageRetention })
          setPermissions(permissionsResponse.permissions)
          setCanManagePermissions(permissionsResponse.canManage)
          setError('')
        }
      })
      .catch((reason: unknown) => {
        if (!cancelled) setError(reason instanceof Error ? reason.message : '設定を読み込めませんでした。')
      })
      .finally(() => {
        if (!cancelled) setLoading(false)
      })
    return () => { cancelled = true }
  }, [])

  function updateShop(field: keyof ShopSettings, value: string) {
    setSettings((current) => ({ ...current, shop: { ...current.shop, [field]: value } }))
    setSaved(false)
  }

  function updateDocument(field: keyof DocumentSettings, value: string | number) {
    setSettings((current) => ({ ...current, document: { ...current.document, [field]: value } }))
    setSaved(false)
  }

  function updateTax(field: keyof TaxSettings, value: string | number) {
    setSettings((current) => ({ ...current, tax: { ...current.tax, [field]: value } as TaxSettings }))
    setSaved(false)
  }

  function updatePermission(field: keyof OrganizationPermissions, value: boolean) {
    setPermissions((current) => ({ ...current, [field]: value }))
    setSaved(false)
  }

  function updateSalesPreset(group: SalesItemPresetGroupKey, index: number, value: string) {
    setSettings((current) => updateSalesPresetGroups(current, {
      ...current.salesItemPresetGroups,
      [group]: current.salesItemPresetGroups[group].map((item, itemIndex) => itemIndex === index ? value : item),
    }))
    setSaved(false)
  }

  function addSalesPreset(group: SalesItemPresetGroupKey) {
    setSettings((current) => updateSalesPresetGroups(current, {
      ...current.salesItemPresetGroups,
      [group]: [...current.salesItemPresetGroups[group], ''],
    }))
    setSaved(false)
  }

  function removeSalesPreset(group: SalesItemPresetGroupKey, index: number) {
    setSettings((current) => updateSalesPresetGroups(current, {
      ...current.salesItemPresetGroups,
      [group]: current.salesItemPresetGroups[group].filter((_, itemIndex) => itemIndex !== index),
    }))
    setSaved(false)
  }

  function updateMaintenancePreset(index: number, value: string) {
    setSettings((current) => ({ ...current, maintenanceItemPresets: current.maintenanceItemPresets.map((item, itemIndex) => itemIndex === index ? value : item) }))
    setSaved(false)
  }

  function addMaintenancePreset() {
    setSettings((current) => ({ ...current, maintenanceItemPresets: [...current.maintenanceItemPresets, ''] }))
    setSaved(false)
  }

  function removeMaintenancePreset(index: number) {
    setSettings((current) => ({ ...current, maintenanceItemPresets: current.maintenanceItemPresets.filter((_, itemIndex) => itemIndex !== index) }))
    setSaved(false)
  }

  async function save() {
    if (saving) return
    setSaving(true)
    setSaved(false)
    const normalizedSettings = {
      ...settings,
      shop: {
        ...settings.shop,
        postalCode: normalizePostalCode(settings.shop.postalCode),
        phone: normalizePhone(settings.shop.phone),
        fax: normalizePhone(settings.shop.fax),
      },
    }
    try {
      const [nextSettings, nextBackupSettings, nextPermissions] = await Promise.all([
        updateSettings(normalizedSettings),
        backupPermissions.canManageCreateRestore || backupPermissions.canManageRetention ? updateBackupSettings(backupSettings) : Promise.resolve(backupSettings),
        canManagePermissions ? updateOrganizationPermissions(permissions).then((response) => response.permissions) : Promise.resolve(permissions),
      ])
      setSettings(nextSettings)
      setBackupSettings(nextBackupSettings)
      setPermissions(nextPermissions)
      setSaved(true)
      setError('')
      onReloadSession?.()
    } catch (reason: unknown) {
      setError(reason instanceof Error ? reason.message : '設定を保存できませんでした。')
    } finally {
      setSaving(false)
    }
  }

  const updateBackupSettingsDraft = useCallback((nextSettings: BackupSettings) => {
    setBackupSettings(nextSettings)
    setSaved(false)
  }, [])

  const visibleTabs = tabs.filter((tab) => tab.id !== 'permissions' || canManagePermissions)

  async function exportCsv(resource: CsvResource) {
    setExporting(resource)
    setError('')
    try {
      const blob = await apiFetchBlob(`/api/export/${resource}`)
      const url = URL.createObjectURL(blob)
      const link = document.createElement('a')
      link.href = url
      link.download = `${csvResourceLabel(resource)}-${new Date().toISOString().slice(0, 10)}.csv`
      document.body.appendChild(link)
      link.click()
      link.remove()
      window.setTimeout(() => URL.revokeObjectURL(url), 10_000)
    } catch (reason: unknown) {
      setError(reason instanceof Error ? reason.message : 'CSVを出力できませんでした。')
    } finally {
      setExporting('')
    }
  }

  return (
    <>
      <div className="page-header settings-page-header"><div><span className="page-eyebrow">共通設定</span><h1>設定</h1><p>店舗情報、帳票、税金・保険料、明細候補を管理します。</p></div><button className="button button-primary" type="button" onClick={save} disabled={loading || saving}><Save size={18} />{saving ? '保存中…' : saved ? '保存済み' : '設定を保存'}</button></div>
      {error && <div className="customer-sync-status is-error"><span>{error}</span><button className="text-button" type="button" onClick={() => window.location.reload()}>再読み込み</button></div>}
      <div className="settings-layout">
        <nav className="panel settings-nav" aria-label="設定メニュー">{visibleTabs.map(({ id, label, description, icon: Icon }) => <button className={activeTab === id ? 'is-active' : ''} type="button" key={id} onClick={() => setActiveTab(id)}><span className="settings-nav-icon"><Icon size={18} /></span><span><strong>{label}</strong><small>{description}</small></span></button>)}</nav>
        <section className="settings-content" aria-live="polite">{loading ? <div className="panel settings-empty"><Settings2 size={28} /><strong>設定を読み込んでいます</strong><span>しばらくお待ちください。</span></div> : activeTab === 'shop' ? <ShopSettingsPanel settings={settings} editable={permissions.employeeCanEditShop} onUpdate={updateShop} /> : activeTab === 'tax' ? <TaxSettingsPanel settings={settings} editable={permissions.employeeCanEditTax} onUpdateTax={updateTax} onUpdateDocument={updateDocument} /> : activeTab === 'masters' ? <MasterSettingsPanel settings={settings} onUpdateSales={updateSalesPreset} onAddSales={addSalesPreset} onRemoveSales={removeSalesPreset} onUpdateMaintenance={updateMaintenancePreset} onAddMaintenance={addMaintenancePreset} onRemoveMaintenance={removeMaintenancePreset} /> : activeTab === 'archive' ? <ArchiveSettingsPanel backupSettings={backupSettings} onBackupSettingsChange={updateBackupSettingsDraft} /> : activeTab === 'data' ? <DataSettingsPanel exporting={exporting} onExport={exportCsv} backupSettings={backupSettings} onBackupSettingsChange={updateBackupSettingsDraft} /> : activeTab === 'permissions' ? <PermissionsPanel permissions={permissions} onUpdate={updatePermission} /> : <MemberSettingsPanel user={user} onUserUpdated={onUserUpdated} />}</section>
      </div>
    </>
  )
}

function updateSalesPresetGroups(settings: AppSettings, groups: SalesItemPresetGroups): AppSettings {
  return {
    ...settings,
    salesItemPresetGroups: groups,
    salesItemPresets: flattenSalesItemPresetGroups(groups),
  }
}

function DataSettingsPanel({ exporting, onExport, backupSettings, onBackupSettingsChange }: { exporting: CsvResource | ''; onExport: (resource: CsvResource) => void; backupSettings: BackupSettings; onBackupSettingsChange: (settings: BackupSettings) => void }) {
  return <div className="settings-panel-stack"><SettingsPanelHeader icon={Table2} title="データ" description="データの入出力とバックアップを管理します。" /><CsvExportPanel exporting={exporting} onExport={onExport} /><AbacusRegistrationPackagePanel /><CsvImportPanel /><BackupSettingsPanel backupSettings={backupSettings} onBackupSettingsChange={onBackupSettingsChange} /></div>
}

function PermissionsPanel({ permissions, onUpdate }: { permissions: OrganizationPermissions; onUpdate: (field: keyof OrganizationPermissions, value: boolean) => void }) {
  return <div className="settings-panel-stack"><SettingsPanelHeader icon={ShieldCheck} title="権限管理" description="従業員が設定・出力・バックアップ・アーカイブで実行できる操作を管理します。" /><section className="panel settings-panel"><div className="settings-section-heading"><ShieldCheck size={18} /><div><h2>従業員に許可する操作</h2><p>管理者・オーナーは常にすべての操作を実行できます。変更後は画面上部の「設定を保存」で反映します。</p></div></div><div className="settings-permission-list"><PermissionToggle label="CSV出力" description="顧客・車両・販売・整備・入金のCSV出力" checked={permissions.employeeCanExportCsv} onChange={(value) => onUpdate('employeeCanExportCsv', value)} /><PermissionToggle label="店舗情報の変更" description="店舗名、連絡先、振込先、帳票ロゴなど" checked={permissions.employeeCanEditShop} onChange={(value) => onUpdate('employeeCanEditShop', value)} /><PermissionToggle label="税率情報の変更" description="消費税率、表示方法、端数処理" checked={permissions.employeeCanEditTax} onChange={(value) => onUpdate('employeeCanEditTax', value)} /><PermissionToggle label="バックアップの作成・PC出力・インポート復元・B2復元" description="バックアップ作成、PCへの出力、PCインポート復元、B2復元" checked={permissions.employeeCanCreateRestoreBackup} onChange={(value) => onUpdate('employeeCanCreateRestoreBackup', value)} /><PermissionToggle label="バックアップの削除・永久保存・保持期限変更" description="B2/PCバックアップの削除、永久保存切替、保持期間の変更" checked={permissions.employeeCanManageBackupRetention} onChange={(value) => onUpdate('employeeCanManageBackupRetention', value)} /><PermissionToggle label="アーカイブの永久保存・保持期限変更" description="アーカイブ書類の永久保存切替、保管期間の変更" checked={permissions.employeeCanManageArchiveRetention} onChange={(value) => onUpdate('employeeCanManageArchiveRetention', value)} /></div></section></div>
}

function PermissionToggle({ label, description, checked, onChange }: { label: string; description: string; checked: boolean; onChange: (value: boolean) => void }) {
  return <label className="backup-toggle settings-permission-toggle"><span><strong>{label}</strong><small>{description}</small></span><input type="checkbox" checked={checked} onChange={(event) => onChange(event.target.checked)} /><span className="backup-toggle-track" aria-hidden="true"><span /></span></label>
}

function ArchiveSettingsPanel({ backupSettings, onBackupSettingsChange }: { backupSettings: BackupSettings; onBackupSettingsChange: (settings: BackupSettings) => void }) {
  const [archives, setArchives] = useState<ArchiveRecord[]>([])
  const [canManage, setCanManage] = useState(false)
  const [canManageRetention, setCanManageRetention] = useState(false)
  const [query, setQuery] = useState('')
  const [loading, setLoading] = useState('')
  const [error, setError] = useState('')
  const [message, setMessage] = useState('')

  const load = useCallback(async () => {
    try {
      const response = await fetchArchives(query)
      setArchives(response.archives)
      setCanManage(response.canManage)
      setCanManageRetention(response.canManageRetention)
      setError('')
    } catch (reason: unknown) {
      setError(reason instanceof Error ? reason.message : 'アーカイブ一覧を読み込めませんでした。')
    }
  }, [query])

  useEffect(() => { void load() }, [load])

  async function runRestore(record: ArchiveRecord) {
    if (!window.confirm(`${record.number}を通常の一覧へ復元しますか？`)) return
    setLoading(`restore-${record.kind}-${record.id}`)
    setError('')
    setMessage('')
    try {
      await restoreArchive(record)
      setArchives((current) => current.filter((item) => item.id !== record.id || item.kind !== record.kind))
      setMessage(`${record.number}を復元しました。`)
    } catch (reason: unknown) {
      setError(reason instanceof Error ? reason.message : '書類を復元できませんでした。')
    } finally {
      setLoading('')
    }
  }

  async function runPermanentDelete(record: ArchiveRecord) {
    if (!canManage) return
    if (!window.confirm(`${record.number}を完全に削除しますか？この操作は取り消せません。`)) return
    setLoading(`delete-${record.kind}-${record.id}`)
    setError('')
    setMessage('')
    try {
      await deleteArchive(record)
      setArchives((current) => current.filter((item) => item.id !== record.id || item.kind !== record.kind))
      setMessage(`${record.number}を完全に削除しました。`)
    } catch (reason: unknown) {
      setError(reason instanceof Error ? reason.message : '書類を完全に削除できませんでした。')
    } finally {
      setLoading('')
    }
  }

  async function runKeepForever(record: ArchiveRecord) {
    if (!canManageRetention) return
    setLoading(`keep-${record.kind}-${record.id}`)
    setError('')
    setMessage('')
    try {
      const response = await updateArchiveRetention(record, !record.keepForever)
      setArchives((current) => current.map((item) => item.id === record.id && item.kind === record.kind ? { ...item, keepForever: response.keepForever, purgeAt: response.purgeAt } : item))
      setMessage(response.keepForever ? `${record.number}を永久保存にしました。` : `${record.number}の永久保存を解除しました。`)
    } catch (reason: unknown) {
      setError(reason instanceof Error ? reason.message : '保存期間を変更できませんでした。')
    } finally {
      setLoading('')
    }
  }

  return <div className="settings-panel-stack"><SettingsPanelHeader icon={Archive} title="アーカイブ" description="削除した書類の復元と整理" /><section className="panel settings-panel archive-settings-panel"><div className="settings-section-heading"><Archive size={18} /><div><h2>アーカイブ</h2><p>削除した書類を一定期間保管し、必要に応じて復元します。</p></div><button className="button button-secondary settings-add-button" type="button" onClick={() => void load()} disabled={Boolean(loading)}><RotateCcw size={14} />更新</button></div><div className="archive-toolbar"><label className="archive-search"><Search size={15} /><input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="書類番号・顧客名・車両名で検索" /></label><span className="archive-count">{archives.length}件</span></div>{error && <div className="auth-error" role="alert">{error}</div>}{message && <div className="settings-success" role="status">{message}</div>}<section className="archive-retention-settings"><div className="archive-retention-copy"><strong>アーカイブ保管期間</strong><span>永久保存に設定していない書類は、指定期間を過ぎると自動削除されます。</span></div><label className="archive-retention-field"><span className="sr-only">アーカイブ保管期間（日）</span><span className="settings-number-input"><input type="number" min={1} max={3650} value={backupSettings.archiveRetentionDays} disabled={!canManageRetention || Boolean(loading)} onChange={(event) => onBackupSettingsChange({ ...backupSettings, archiveRetentionDays: Number(event.target.value) })} /><span>日</span></span></label></section>{archives.length === 0 ? <div className="settings-empty archive-empty"><Archive size={26} /><strong>アーカイブ済み書類はありません</strong><span>販売・整備画面でアーカイブした書類がここに表示されます。</span></div> : <div className="archive-list">{archives.map((record) => <div className="archive-row" key={`${record.kind}-${record.id}`}><IconWithChain visible={record.keepForever} className="archive-icon-chain" chainWidth="88%" chainTop="-2%" chainDepth={18} linkThickness={2} linkSize={5}><span className={`archive-kind archive-kind-${record.kind}`}>{record.kind === 'sales' ? '販売' : '整備'}</span></IconWithChain><div className="archive-copy"><strong>{record.number}</strong><span>{record.customerName || '顧客未設定'}{record.vehicle ? ` ・ ${record.vehicle}` : ''}</span><small>{record.type}{record.category ? ` ・ ${record.category}` : ''} ・ アーカイブ {formatBackupDate(record.archivedAt ?? '')}</small>{!record.keepForever && getArchiveExpiration(record, backupSettings.archiveRetentionDays) && <small className="archive-expiration-text">期限：{formatBackupDate(getArchiveExpiration(record, backupSettings.archiveRetentionDays) ?? '')}</small>}</div><div className="archive-meta">{record.keepForever ? <span className="archive-retention is-forever"><ShieldCheck size={13} />永久保存</span> : <span className="archive-retention"><Clock3 size={13} />自動削除予定</span>}<div className="archive-actions"><button className="button button-secondary" type="button" disabled={Boolean(loading)} onClick={() => void runRestore(record)}>{loading === `restore-${record.kind}-${record.id}` ? '復元中…' : <><RotateCcw size={14} />復元</>}</button>{canManageRetention && <button className="button button-secondary archive-retention-button" type="button" disabled={Boolean(loading)} onClick={() => void runKeepForever(record)}>{loading === `keep-${record.kind}-${record.id}` ? '更新中…' : record.keepForever ? <><ShieldCheck size={13} />保存解除</> : <><ShieldCheck size={13} />永久保存</>}</button>}{canManage && <button className="button button-danger archive-delete-button" type="button" disabled={Boolean(loading)} onClick={() => void runPermanentDelete(record)}>{loading === `delete-${record.kind}-${record.id}` ? '削除中…' : <><Trash2 size={13} />完全削除</>}</button>}</div></div></div>)}</div>}</section></div>
}

function MemberSettingsPanel({ user, onUserUpdated }: { user: User; onUserUpdated?: (user: User) => void }) {
  const [currentUser, setCurrentUser] = useState(user)
  const [displayName, setDisplayName] = useState(user.displayName ?? '')
  const [newEmail, setNewEmail] = useState(user.email ?? '')
  const [newPassword, setNewPassword] = useState('')
  const [passwordConfirmation, setPasswordConfirmation] = useState('')
  const [linkEmail, setLinkEmail] = useState(user.email ?? '')
  const [linkPasswordValue, setLinkPasswordValue] = useState('')
  const [loading, setLoading] = useState('')
  const [message, setMessage] = useState('')
  const [error, setError] = useState('')
  const [members, setMembers] = useState<MemberRecord[]>([])
  const [currentRole, setCurrentRole] = useState<MemberRole>('employee')
  const [membersLoading, setMembersLoading] = useState(true)
  const [memberLoading, setMemberLoading] = useState('')
  useEffect(() => {
    setCurrentUser(user)
    setDisplayName(user.displayName ?? '')
    setNewEmail(user.email ?? '')
    setLinkEmail(user.email ?? '')
  }, [user])

  const [memberError, setMemberError] = useState('')
  const [memberMessage, setMemberMessage] = useState('')
  const [memberModal, setMemberModal] = useState<'add' | 'temporaryPassword' | 'invitation' | null>(null)
  const [newMemberName, setNewMemberName] = useState('')
  const [newMemberEmail, setNewMemberEmail] = useState('')
  const [temporaryPassword, setTemporaryPassword] = useState('')
  const [invitation, setInvitation] = useState<{ code: string; email: string; expiresAt: string } | null>(null)
  const [accountModal, setAccountModal] = useState<'displayName' | 'password' | 'google' | null>(null)

  const hasPassword = currentUser.providerData.some((provider) => provider.providerId === 'password')
  const hasGoogle = currentUser.providerData.some((provider) => provider.providerId === 'google.com')
  const canManageMembers = currentRole === 'owner' || currentRole === 'admin'

  useEffect(() => {
    if (!accountModal) return
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !loading) setAccountModal(null)
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [accountModal, loading])

  useEffect(() => {
    if (!memberModal) return
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !memberLoading) {
        setMemberModal(null)
        setTemporaryPassword('')
        setInvitation(null)
      }
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [memberModal, memberLoading])

  function closeMemberModal() {
    if (memberLoading) return
    setMemberModal(null)
    setTemporaryPassword('')
    setInvitation(null)
  }

  useEffect(() => {
    let cancelled = false
    setMembersLoading(true)
    fetchMembers()
      .then((response) => {
        if (cancelled) return
        setMembers(response.members)
        setCurrentRole(response.currentRole)
        setMemberError('')
      })
      .catch((reason: unknown) => {
        if (!cancelled) setMemberError(getMemberError(reason))
      })
      .finally(() => {
        if (!cancelled) setMembersLoading(false)
      })
    return () => { cancelled = true }
  }, [])

  async function refreshUser(profile?: { displayName?: string; email?: string | null }) {
    const wasAnonymous = currentUser.isAnonymous
    const nextUser = await refreshCurrentUser()
    if (nextUser) {
      if (profile && !nextUser.isAnonymous) await updateCurrentProfile(profile)
      setCurrentUser(nextUser)
      setDisplayName(nextUser.displayName ?? '')
      setNewEmail(nextUser.email ?? '')
      setLinkEmail(nextUser.email ?? '')
      if (wasAnonymous && !nextUser.isAnonymous) {
        const response = await fetchMembers()
        setMembers(response.members)
        setCurrentRole(response.currentRole)
      }
      setMembers((current) => current.map((member) => member.isSelf ? { ...member, displayName: profile?.displayName ?? nextUser.displayName ?? member.displayName, email: profile?.email === undefined ? nextUser.email ?? member.email : profile.email } : member))
      onUserUpdated?.(nextUser)
    }
  }

  async function runAction(action: string, callback: () => Promise<void>, profile?: { displayName?: string; email?: string | null }): Promise<boolean> {
    setLoading(action)
    setError('')
    setMessage('')
    try {
      await callback()
      await refreshUser(profile)
      return true
    } catch (reason) {
      setError(getSettingsAuthError(reason))
      return false
    } finally {
      setLoading('')
    }
  }

  async function saveDisplayName() {
    if (!displayName.trim()) {
      setError('表示名を入力してください。')
      return
    }
    const completed = await runAction('displayName', async () => {
      await changeCurrentDisplayName(displayName)
      setMessage('表示名を更新しました。')
    }, { displayName: displayName.trim() })
    if (completed) setAccountModal(null)
  }

  async function saveEmail() {
    if (!newEmail.trim()) {
      setError('メールアドレスを入力してください。')
      return
    }
    const completed = await runAction('email', async () => {
      await changeCurrentEmail(newEmail)
      await sendCurrentEmailVerification()
      setMessage('メールアドレスを更新し、確認メールを送信しました。')
    }, { email: newEmail.trim().toLowerCase() })
    if (completed) setAccountModal(null)
  }

  async function savePassword() {
    if (newPassword.length < 8) {
      setError('パスワードは8文字以上で設定してください。')
      return
    }
    if (newPassword !== passwordConfirmation) {
      setError('パスワードが一致しません。')
      return
    }
    const completed = await runAction('password', async () => {
      await changeCurrentPassword(newPassword)
      setNewPassword('')
      setPasswordConfirmation('')
      setMessage('パスワードを更新しました。')
    })
    if (completed) setAccountModal(null)
  }

  async function linkPassword() {
    if (!linkEmail.trim() || linkPasswordValue.length < 8) {
      setError('メールアドレスと8文字以上のパスワードを入力してください。')
      return
    }
    const completed = await runAction('link-password', async () => {
      await addEmailPasswordLogin(linkEmail, linkPasswordValue)
      setLinkPasswordValue('')
      setMessage('メールアドレス＋パスワードを追加しました。')
    })
    if (completed) setAccountModal(null)
  }

  async function linkGoogle() {
    const completed = await runAction('link-google', async () => {
      await addGoogleLogin()
      setMessage('Googleログインを追加しました。')
    })
    if (completed) setAccountModal(null)
  }

  async function unlinkProvider(providerId: string) {
    if (!window.confirm('このログイン方法を解除しますか？')) return
    const completed = await runAction(`unlink-${providerId}`, async () => {
      await removeLoginProvider(providerId)
      setMessage('ログイン方法を解除しました。')
    })
    if (completed) setAccountModal(null)
  }

  async function addMember(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!newMemberName.trim() || !newMemberEmail.trim()) {
      setMemberError('表示名とメールアドレスを入力してください。')
      return
    }
    setMemberLoading('create')
    setMemberError('')
    setMemberMessage('')
    try {
      const response = await createMember({ displayName: newMemberName, email: newMemberEmail })
      const createdMember = response.member
      if (createdMember) setMembers((current) => current.some((member) => member.uid === createdMember.uid) ? current.map((member) => member.uid === createdMember.uid ? createdMember : member) : [...current, createdMember])
      setTemporaryPassword(response.temporaryPassword ?? '')
      setInvitation(response.invitation ?? null)
      setNewMemberName('')
      setNewMemberEmail('')
      if (response.temporaryPassword) {
        setMemberModal('temporaryPassword')
        setMemberMessage('従業員を登録しました。')
      } else if (response.invitation) {
        setMemberModal('invitation')
        setMemberMessage('既存のアカウントへ招待を作成しました。招待コードを本人へ安全に伝えてください。')
      } else {
        setMemberModal(null)
        setMemberMessage('従業員を登録しました。')
      }
    } catch (reason: unknown) {
      setMemberError(getMemberError(reason))
    } finally {
      setMemberLoading('')
    }
  }

  async function changeMember(uid: string, input: { role?: Exclude<MemberRole, 'owner'>; status?: 'active' | 'suspended' }) {
    setMemberLoading(uid)
    setMemberError('')
    setMemberMessage('')
    try {
      const response = await updateMember(uid, input)
      setMembers(response.members)
      setMemberMessage('所属情報を更新しました。')
    } catch (reason: unknown) {
      setMemberError(getMemberError(reason))
    } finally {
      setMemberLoading('')
    }
  }

  async function removeMember(uid: string) {
    const member = members.find((item) => item.uid === uid)
    if (!member) return
    if (!window.confirm(`「${member.displayName}」を現在の組織から削除しますか？削除後もアカウント自体は残ります。`)) return
    setMemberLoading(`remove-${uid}`)
    setMemberError('')
    setMemberMessage('')
    try {
      const response = await removeMemberFromOrganization(uid)
      setMembers(response.members)
      setMemberMessage('組織から削除しました。')
    } catch (reason: unknown) {
      setMemberError(getMemberError(reason))
    } finally {
      setMemberLoading('')
    }
  }
  async function copyTemporaryPassword() {
    if (!temporaryPassword) return
    if (!navigator.clipboard) {
      setMemberMessage('この環境では自動コピーできません。表示されたパスワードを安全に控えてください。')
      return
    }
    await navigator.clipboard.writeText(temporaryPassword)
    setMemberMessage('初期パスワードをコピーしました。')
  }

  async function copyInvitationCode() {
    if (!invitation?.code) return
    if (!navigator.clipboard) {
      setMemberMessage('この環境では自動コピーできません。招待コードを安全に控えてください。')
      return
    }
    await navigator.clipboard.writeText(invitation.code)
    setMemberMessage('招待コードをコピーしました。')
  }

  return (
    <div className="settings-panel-stack">
      <SettingsPanelHeader icon={UsersRound} title="管理者・従業員" description="自分のアカウントとログイン方法を管理します。" />
      <section className="panel settings-panel account-settings-panel">
        <div className="account-setting-list">
          <div className="account-setting-row account-setting-row-display-name">
            <div className="account-setting-label"><strong>表示名</strong><small>サイドバーなどに表示されます。</small></div>
            <div className="account-setting-value">{displayName || '未設定'}</div>
            <button className="button button-secondary account-change-button" type="button" disabled={Boolean(loading)} onClick={() => { setError(''); setMessage(''); setAccountModal('displayName') }}>変更</button>
          </div>
          <div className="account-setting-group">
            <h3>ログイン方法</h3>
            <div className={hasPassword ? 'account-login-row is-linked' : 'account-login-row is-unlinked'}>
              <strong>メールアドレス＋パスワードでログイン（{hasPassword ? '済み' : '未設定'}）</strong>
              <button className="button button-secondary account-change-button" type="button" disabled={Boolean(loading)} onClick={() => { setError(''); setMessage(''); setAccountModal('password') }}>変更</button>
            </div>
            <div className={hasGoogle ? 'account-login-row is-linked' : 'account-login-row is-unlinked'}>
              <strong>Googleでログイン（{hasGoogle ? '済み' : '未設定'}）</strong>
              <button className="button button-secondary account-change-button" type="button" disabled={Boolean(loading)} onClick={() => { setError(''); setMessage(''); if (hasGoogle) setAccountModal('google'); else void linkGoogle() }}>変更</button>
            </div>
          </div>
        </div>
        {error && !accountModal && <div className="auth-error" role="alert">{error}</div>}
        {message && <div className="settings-success" role="status">{message}</div>}
      </section>
      <section className="panel settings-panel">
        <div className="settings-section-heading"><UsersRound size={18} /><div><h2>組織ユーザー</h2><p>この組織に所属する管理者・従業員を確認します。</p></div>{canManageMembers && <button className="button button-secondary settings-add-button" type="button" onClick={() => { setMemberError(''); setMemberMessage(''); setMemberModal('add') }}><UserPlus size={15} />従業員を追加</button>}</div>
        {memberError && !memberModal && <div className="auth-error" role="alert">{memberError}</div>}
        {memberMessage && !memberModal && <div className="settings-success" role="status">{memberMessage}</div>}
        {membersLoading ? <div className="settings-empty member-list-empty"><UsersRound size={26} /><span>所属ユーザーを読み込んでいます。</span></div> : members.length === 0 ? <div className="settings-empty member-list-empty"><UsersRound size={26} /><span>所属ユーザーが見つかりません。</span></div> : <div className="member-list">{members.map((member) => <MemberRow key={member.uid} member={member} currentRole={currentRole} loading={memberLoading} onChange={changeMember} onRemove={removeMember} />)}</div>}
      </section>
      {accountModal && <div className="account-modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget && !loading) setAccountModal(null) }}>
        <section className="account-modal" role="dialog" aria-modal="true" aria-labelledby="account-modal-title" onMouseDown={(event) => event.stopPropagation()}>
          <div className="account-modal-header">
            <div><span className="page-eyebrow">アカウント設定</span><h2 id="account-modal-title">{accountModal === 'displayName' ? '表示名を変更' : accountModal === 'password' ? (hasPassword ? 'メールアドレス・パスワードを変更' : 'メールアドレス＋パスワードを追加') : 'Googleログインの設定'}</h2></div>
            <button className="account-modal-close" type="button" aria-label="閉じる" disabled={Boolean(loading)} onClick={() => setAccountModal(null)}>×</button>
          </div>
          {error && <div className="auth-error" role="alert">{error}</div>}
          {message && <div className="settings-success" role="status">{message}</div>}
          {accountModal === 'displayName' && <div className="account-modal-content">
            <SettingsField label="表示名" value={displayName} onChange={setDisplayName} disabled={Boolean(loading)} placeholder="例：山本 翔太" />
            <div className="account-modal-actions"><button className="button button-secondary" type="button" disabled={Boolean(loading)} onClick={() => setAccountModal(null)}>キャンセル</button><button className="button button-primary" type="button" disabled={Boolean(loading)} onClick={() => void saveDisplayName()}>{loading === 'displayName' ? '変更中…' : '変更する'}</button></div>
          </div>}
          {accountModal === 'password' && hasPassword && <div className="account-modal-content">
            <div className="settings-form-grid"><SettingsField label="メールアドレス" type="email" value={newEmail} onChange={setNewEmail} disabled={Boolean(loading)} /><SettingsField label="新しいパスワード" type="password" value={newPassword} onChange={setNewPassword} disabled={Boolean(loading)} /><SettingsField label="新しいパスワード（確認）" type="password" value={passwordConfirmation} onChange={setPasswordConfirmation} disabled={Boolean(loading)} /></div>
            <div className="account-modal-actions"><button className="button button-secondary" type="button" disabled={Boolean(loading)} onClick={() => setAccountModal(null)}>キャンセル</button><button className="button button-secondary" type="button" disabled={Boolean(loading)} onClick={() => void saveEmail()}>{loading === 'email' ? '変更中…' : 'メールアドレスを変更'}</button><button className="button button-primary" type="button" disabled={Boolean(loading)} onClick={() => void savePassword()}>{loading === 'password' ? '変更中…' : 'パスワードを変更'}</button></div>
            {currentUser.emailVerified ? <span className="verified-label">メール確認済み</span> : <button className="text-button" type="button" disabled={Boolean(loading)} onClick={() => void runAction('verification', async () => { await sendCurrentEmailVerification(); setMessage('確認メールを送信しました。') })}>{loading === 'verification' ? '送信中…' : '確認メールを送信'}</button>}
          </div>}
          {accountModal === 'password' && !hasPassword && <div className="account-modal-content">
            <p className="account-modal-note">このアカウントにメールアドレスとパスワードでログインする方法を追加します。</p>
            <div className="settings-form-grid"><SettingsField label="メールアドレス" type="email" value={linkEmail} onChange={setLinkEmail} disabled={Boolean(loading)} /><SettingsField label="パスワード" type="password" value={linkPasswordValue} onChange={setLinkPasswordValue} disabled={Boolean(loading)} /></div>
            <div className="account-modal-actions"><button className="button button-secondary" type="button" disabled={Boolean(loading)} onClick={() => setAccountModal(null)}>キャンセル</button><button className="button button-primary" type="button" disabled={Boolean(loading)} onClick={() => void linkPassword()}>{loading === 'link-password' ? '追加しています…' : 'メール認証を追加'}</button></div>
          </div>}
          {accountModal === 'google' && hasGoogle && <div className="account-modal-content">
            <p className="account-modal-note">このアカウントに連携しているGoogleログインを管理します。</p>
            {currentUser.providerData.length > 1 ? <div className="account-modal-actions"><button className="button button-secondary" type="button" disabled={Boolean(loading)} onClick={() => setAccountModal(null)}>キャンセル</button><button className="button button-primary" type="button" disabled={Boolean(loading)} onClick={() => void unlinkProvider('google.com')}>{loading === 'unlink-google.com' ? '解除しています…' : 'Googleログインを解除'}</button></div> : <><p className="account-modal-note">ログイン方法を1つ以上残す必要があるため、Googleログインは解除できません。</p><div className="account-modal-actions"><button className="button button-primary" type="button" onClick={() => setAccountModal(null)}>閉じる</button></div></>}
          </div>}
        </section>
      </div>}

      {memberModal && <div className="account-modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) closeMemberModal() }}>
        <section className="account-modal" role="dialog" aria-modal="true" aria-labelledby="member-modal-title" onMouseDown={(event) => event.stopPropagation()}>
          <div className="account-modal-header">
            <div><span className="page-eyebrow">組織ユーザー</span><h2 id="member-modal-title">{memberModal === 'add' ? '従業員を追加' : memberModal === 'temporaryPassword' ? '初期パスワード' : '組織への招待'}</h2></div>
            <button className="account-modal-close" type="button" aria-label="閉じる" disabled={Boolean(memberLoading)} onClick={closeMemberModal}>×</button>
          </div>
          {memberError && <div className="auth-error" role="alert">{memberError}</div>}
          {memberModal === 'add' ? <form className="account-modal-content" onSubmit={(event) => void addMember(event)}>
            <p className="account-modal-note">従業員の表示名とメールアドレスを入力します。既存アカウントにはパスワードを上書きせず、本人受け入れ用の招待コードを発行します。</p>
            <div className="settings-form-grid"><SettingsField label="表示名" value={newMemberName} onChange={setNewMemberName} placeholder="例：山本 翔太" required disabled={Boolean(memberLoading)} /><SettingsField label="メールアドレス" type="email" value={newMemberEmail} onChange={setNewMemberEmail} placeholder="employee@shop.jp" required disabled={Boolean(memberLoading)} /></div>
            <div className="account-modal-actions"><button className="button button-secondary" type="button" disabled={Boolean(memberLoading)} onClick={closeMemberModal}>キャンセル</button><button className="button button-primary" type="submit" disabled={Boolean(memberLoading)}>{memberLoading === 'create' ? '登録しています…' : '従業員を登録'}</button></div>
          </form> : memberModal === 'temporaryPassword' ? <div className="account-modal-content">
            <p className="account-modal-note">従業員へ安全な方法で伝えてください。このパスワードはこの画面を閉じると再表示できません。</p>
            <div className="temporary-password-value"><code>{temporaryPassword}</code><button className="button button-secondary" type="button" onClick={() => void copyTemporaryPassword()}><Copy size={15} />コピー</button></div>
            {memberMessage && <div className="settings-success" role="status">{memberMessage}</div>}
            <div className="account-modal-actions"><button className="button button-primary" type="button" onClick={closeMemberModal}>閉じる</button></div>
          </div> : <div className="account-modal-content">
            <p className="account-modal-note">{invitation?.email} の本人がログイン後、このワンタイム招待コードを入力して組織へ参加します。有効期限は {invitation ? formatBackupDate(invitation.expiresAt) : '7日間'} です。</p>
            <div className="temporary-password-value"><code>{invitation?.code}</code><button className="button button-secondary" type="button" onClick={() => void copyInvitationCode()}><Copy size={15} />コピー</button></div>
            {memberMessage && <div className="settings-success" role="status">{memberMessage}</div>}
            <div className="account-modal-actions"><button className="button button-primary" type="button" onClick={closeMemberModal}>閉じる</button></div>
          </div>}
        </section>
      </div>}
    </div>
  )
}

function MemberRow({ member, currentRole, loading, onChange, onRemove }: { member: MemberRecord; currentRole: MemberRole; loading: string; onChange: (uid: string, input: { role?: Exclude<MemberRole, 'owner'>; status?: 'active' | 'suspended' }) => void; onRemove: (uid: string) => void }) {
  const canEdit = !member.isSelf && member.role !== 'owner' && (currentRole === 'owner' || (currentRole === 'admin' && member.role === 'employee'))
  const canRemove = canEdit
  return <article className="member-row"><div className="member-avatar"><UserRound size={18} /></div><div className="member-main"><div className="member-heading"><strong>{member.displayName}</strong>{member.isSelf && <span className="member-self-badge">自分</span>}{member.mustChangePassword && <span className="member-pending-badge">初回変更待ち</span>}</div><small>{member.email || 'メールアドレス未設定'}</small></div><div className="member-role-control">{member.role === 'owner' ? <span className="member-role-badge"><ShieldCheck size={13} />オーナー</span> : canEdit ? <select aria-label={`${member.displayName}の権限`} value={member.role} disabled={Boolean(loading)} onChange={(event) => onChange(member.uid, { role: event.target.value as Exclude<MemberRole, 'owner'> })}><option value="employee">従業員</option><option value="admin">管理者</option></select> : <span className="member-role-badge">{member.role === 'admin' ? '管理者' : '従業員'}</span>}</div><div className="member-status-control">{canEdit ? <button className={`member-status-button is-${member.status}`} type="button" disabled={Boolean(loading)} onClick={() => onChange(member.uid, { status: member.status === 'active' ? 'suspended' : 'active' })}>{member.status === 'active' ? '利用中' : '停止中'}</button> : <span className={`member-status-badge is-${member.status}`}>{member.status === 'active' ? '利用中' : '停止中'}</span>}</div>{canRemove && <button className="text-button member-remove-button" type="button" disabled={Boolean(loading)} onClick={() => onRemove(member.uid)}>{loading === `remove-${member.uid}` ? '削除中…' : '組織から削除'}</button>}</article>
}

function getMemberError(error: unknown) {
  if (error instanceof Error && error.message) return error.message
  return '管理者・従業員情報の処理に失敗しました。'
}

function getSettingsAuthError(error: unknown) {
  if (!(error instanceof Error) || !error.message) return 'アカウント情報の更新に失敗しました。'
  if (error.message.includes('auth/requires-recent-login')) return '安全のため、いったんログアウトして再ログインしてからお試しください。'
  if (error.message.includes('auth/credential-already-in-use') || error.message.includes('auth/email-already-in-use')) return 'この認証情報は別のアカウントで使用されています。'
  if (error.message.includes('auth/provider-already-linked')) return 'このログイン方法はすでに連携されています。'
  if (error.message.includes('auth/account-exists-with-different-credential')) return 'このGoogleアカウントは別のユーザーに登録されています。'
  if (error.message.includes('auth/popup-blocked')) return 'ポップアップがブロックされました。ブラウザの設定を確認してください。'
  if (error.message.includes('auth/network-request-failed')) return '通信に失敗しました。接続を確認して再度お試しください。'
  if (error.message.includes('auth/weak-password')) return 'パスワードは8文字以上で設定してください。'
  if (error.message.includes('auth/popup-closed-by-user')) return 'Googleの認証画面が閉じられました。'
  return error.message
}

function CsvExportPanel({ exporting, onExport }: { exporting: CsvResource | ''; onExport: (resource: CsvResource) => void }) {
  const resources: Array<{ id: CsvResource; label: string; description: string }> = [
    { id: 'customers', label: '顧客一覧', description: '顧客情報を出力' },
    { id: 'vehicles', label: '車両一覧', description: '車両情報を出力' },
    { id: 'sales', label: '販売書類', description: '販売書類と明細を出力' },
    { id: 'maintenance', label: '整備書類', description: '整備書類と明細を出力' },
    { id: 'payments', label: '入金管理', description: '請求・入金状況を出力' },
  ]
  return <section className="panel settings-panel csv-export-panel"><div className="settings-section-heading"><Table2 size={18} /><div><h2>データ出力</h2><p>Excelで開けるUTF-8 CSVとして現在のデータをダウンロードします。</p></div></div><div className="csv-export-grid">{resources.map((resource) => <button className="csv-export-card" type="button" key={resource.id} disabled={Boolean(exporting)} onClick={() => onExport(resource.id)}><span className="csv-export-icon"><Download size={17} /></span><span><strong>{resource.label}</strong><small>{exporting === resource.id ? '出力中…' : resource.description}</small></span><ChevronRightIcon /></button>)}</div></section>
}

function CsvImportPanel() {
  const [resource, setResource] = useState<CsvImportResource>('customers')
  const [file, setFile] = useState<File | null>(null)
  const [preview, setPreview] = useState<CsvImportPreview | null>(null)
  const [result, setResult] = useState<CsvImportResult | null>(null)
  const [loading, setLoading] = useState<'preview' | 'import' | ''>('')
  const [error, setError] = useState('')
  const resourceOptions: Array<{ id: CsvImportResource; label: string; description: string }> = [
    { id: 'customers', label: '顧客一覧', description: '顧客情報を取込' },
    { id: 'vehicles', label: '車両一覧', description: '顧客に紐づく車両を取込' },
    { id: 'sales', label: '販売書類', description: '販売書類と明細を取込' },
    { id: 'maintenance', label: '整備書類', description: '整備書類と明細を取込' },
    { id: 'payments', label: '入金管理', description: '請求ごとの入金情報を取込' },
  ]

  function selectResource(nextResource: CsvImportResource) {
    setResource(nextResource)
    setFile(null)
    setPreview(null)
    setResult(null)
    setError('')
  }

  async function previewFile() {
    if (!file) {
      setError('CSVファイルを選択してください。')
      return
    }
    setLoading('preview')
    setError('')
    setResult(null)
    try {
      setPreview(await previewCsvImport(resource, file))
    } catch (reason: unknown) {
      setError(reason instanceof Error ? reason.message : 'CSVを確認できませんでした。')
    } finally {
      setLoading('')
    }
  }

  async function importFile() {
    if (!file || !preview || preview.totalRows <= preview.errors.length) return
    setLoading('import')
    setError('')
    try {
      setResult(await commitCsvImport(resource, file))
    } catch (reason: unknown) {
      setError(reason instanceof Error ? reason.message : 'CSVを取り込めませんでした。')
    } finally {
      setLoading('')
    }
  }

  const previewHeaders = preview?.previewRows[0] ? Object.keys(preview.previewRows[0]).slice(0, 5) : []
  return <section className="panel settings-panel csv-import-panel"><div className="settings-section-heading"><FileUp size={18} /><div><h2>CSVインポート・移行</h2><p>この画面から出力したCSVを確認してから、現在の組織へ取り込みます。実行には管理者権限が必要です。</p></div></div><div className="csv-import-controls"><label className="form-field"><span>取込対象</span><select value={resource} onChange={(event) => selectResource(event.target.value as CsvImportResource)} disabled={Boolean(loading)}>{resourceOptions.map((option) => <option key={option.id} value={option.id}>{option.label} - {option.description}</option>)}</select></label><label className="csv-file-input"><span><Upload size={16} />CSVファイルを選択</span><small>{file ? `${file.name} (${formatFileSize(file.size)})` : '5MB・5,000行以内'}</small><input type="file" accept=".csv,text/csv" disabled={Boolean(loading)} onChange={(event) => { const nextFile = event.target.files?.[0] ?? null; setFile(nextFile); setPreview(null); setResult(null); setError('') }} /></label><button className="button button-secondary" type="button" disabled={!file || Boolean(loading)} onClick={() => void previewFile()}>{loading === 'preview' ? '確認中…' : '内容を確認'}</button></div>{error && <div className="auth-error" role="alert">{error}</div>}{preview && <div className="csv-import-preview"><div className="csv-import-summary"><span>全{preview.totalRows}行</span><span className={preview.errors.length ? 'is-warning' : 'is-success'}>{preview.errors.length ? `要確認 ${preview.errors.length}行` : '入力エラーなし'}</span></div>{previewHeaders.length > 0 && <div className="csv-preview-table"><div className="csv-preview-row csv-preview-head">{previewHeaders.map((header) => <span key={header}>{header}</span>)}</div>{preview.previewRows.slice(0, 5).map((row, index) => <div className="csv-preview-row" key={index}>{previewHeaders.map((header) => <span key={header} title={row[header]}>{row[header] || '-'}</span>)}</div>)}</div>}{preview.errors.length > 0 && <ul className="csv-import-errors">{preview.errors.slice(0, 5).map((item) => <li key={`${item.row}-${item.message}`}>{item.row}行目: {item.message}</li>)}</ul>}<button className="button button-primary" type="button" disabled={Boolean(loading) || preview.totalRows <= preview.errors.length} onClick={() => void importFile()}>{loading === 'import' ? '取り込み中…' : 'この内容を取り込む'}</button></div>}{result && <div className="settings-success csv-import-result" role="status"><CheckCircle2 size={16} />{result.imported}件を追加、{result.updated}件を更新、{result.skipped}件をスキップしました。{result.errors.length > 0 && <span>エラー{result.errors.length}件</span>}</div>}</section>
}

function ChevronRightIcon() { return <span className="csv-export-arrow">›</span> }

function csvResourceLabel(resource: CsvResource) {
  return resource === 'customers' ? '顧客一覧' : resource === 'vehicles' ? '車両一覧' : resource === 'sales' ? '販売書類' : resource === 'maintenance' ? '整備書類' : '入金管理'
}

function formatFileSize(size: number) {
  if (size < 1024) return `${size} B`
  if (size < 1024 * 1024) return `${Math.round(size / 1024)} KB`
  return `${(size / (1024 * 1024)).toFixed(1)} MB`
}

function formatBackupDate(value: string) {
  const date = new Date(value.replace(' ', 'T'))
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString('ja-JP')
}

function getArchiveExpiration(record: ArchiveRecord, retentionDays: number) {
  return record.purgeAt ?? addRetentionDays(record.archivedAt ?? '', retentionDays)
}

function addRetentionDays(value: string, days: number) {
  const date = new Date(value.replace(' ', 'T'))
  if (Number.isNaN(date.getTime())) return null
  date.setUTCDate(date.getUTCDate() + days)
  return date.toISOString()
}

function ShopSettingsPanel({ settings, editable, onUpdate: saveUpdate }: { settings: AppSettings; editable: boolean; onUpdate: (field: keyof ShopSettings, value: string) => void }) {
  const onUpdate = editable ? saveUpdate : () => undefined
  return <div className="settings-panel-stack"><SettingsPanelHeader icon={Building2} title="店舗情報" description="見積書、請求書に表示する基本情報です。" /><section className="panel settings-panel"><div className="settings-section-heading"><Building2 size={18} /><div><h2>店舗情報</h2><p>店舗名や連絡先は帳票の発行元として利用します。</p></div></div><div className="settings-form-grid"><SettingsField label="店舗名" value={settings.shop.name} onChange={(value) => onUpdate('name', value)} required /><SettingsField label="郵便番号" normalization="postalCode" value={settings.shop.postalCode} onChange={(value) => onUpdate('postalCode', value)} placeholder="例：100-0001" /><SettingsField label="電話番号" normalization="phone" value={settings.shop.phone} onChange={(value) => onUpdate('phone', value)} placeholder="例：03-0000-0000" /><SettingsField label="FAX番号" normalization="phone" value={settings.shop.fax} onChange={(value) => onUpdate('fax', value)} placeholder="例：03-0000-0001" /><SettingsField label="適格請求書発行事業者番号" value={settings.shop.registrationNumber} onChange={(value) => onUpdate('registrationNumber', value)} placeholder="例：T1234567890123" /><SettingsField label="住所" value={settings.shop.address} onChange={(value) => onUpdate('address', value)} wide /><ShopLogoField value={settings.shop.logoDataUrl} onChange={(value) => onUpdate('logoDataUrl', value)} /></div></section><section className="panel settings-panel"><div className="settings-section-heading"><Banknote size={18} /><div><h2>振込先情報</h2><p>請求書などに表示する振込先を設定します。</p></div></div><div className="settings-form-grid"><SettingsField label="振込口座" value={settings.shop.bankName} onChange={(value) => onUpdate('bankName', value)} /><SettingsField label="口座名義" value={settings.shop.bankAccount} onChange={(value) => onUpdate('bankAccount', value)} /></div></section></div>
}

function ShopLogoField({ value, onChange }: { value: string; onChange: (value: string) => void }) {
  const [error, setError] = useState('')

  function readFile(file: File) {
    if (!['image/png', 'image/jpeg', 'image/webp'].includes(file.type)) {
      setError('PNG・JPEG・WebPの画像を選択してください。')
      return
    }
    if (file.size > 1_000_000) {
      setError('企業ロゴは1MB以下にしてください。')
      return
    }

    const reader = new FileReader()
    reader.onerror = () => setError('企業ロゴを読み込めませんでした。')
    reader.onload = () => {
      const dataUrl = typeof reader.result === 'string' ? reader.result : ''
      if (!dataUrl) {
        setError('企業ロゴを読み込めませんでした。')
        return
      }
      setError('')
      onChange(dataUrl)
    }
    reader.readAsDataURL(file)
  }

  function handleFileChange(event: ChangeEvent<HTMLInputElement>) {
    const file = event.currentTarget.files?.[0]
    event.currentTarget.value = ''
    if (file) readFile(file)
  }

  function handleDrop(event: ReactDragEvent<HTMLLabelElement>) {
    event.preventDefault()
    event.currentTarget.classList.remove('is-dragging')
    const file = event.dataTransfer.files[0]
    if (file) readFile(file)
  }

  return <div className="form-field settings-field-wide settings-logo-field"><span>企業ロゴ</span><div className="settings-logo-control">{value ? <div className="settings-logo-preview"><img src={value} alt="登録中の企業ロゴ" /><div className="settings-logo-actions"><label className="button button-secondary settings-logo-button">ロゴを変更<input className="settings-logo-input" type="file" accept="image/png,image/jpeg,image/webp" onChange={handleFileChange} /></label><button className="text-button settings-logo-remove" type="button" onClick={() => { setError(''); onChange('') }}><Trash2 size={14} />削除</button></div></div> : <label className="settings-logo-dropzone" onDragEnter={(event) => { event.preventDefault(); event.currentTarget.classList.add('is-dragging') }} onDragOver={(event) => { event.preventDefault(); event.currentTarget.classList.add('is-dragging') }} onDragLeave={(event) => event.currentTarget.classList.remove('is-dragging')} onDrop={handleDrop}><Upload size={22} /><strong>企業ロゴを選択</strong><small>PNG・JPEG・WebP / 1MB以下</small><input className="settings-logo-input" type="file" accept="image/png,image/jpeg,image/webp" onChange={handleFileChange} /></label>}{error && <small className="settings-logo-error" role="alert">{error}</small>}</div></div>
}

function TaxSettingsPanel({ settings, editable, onUpdateTax: saveTaxUpdate, onUpdateDocument }: { settings: AppSettings; editable: boolean; onUpdateTax: (field: keyof TaxSettings, value: string | number) => void; onUpdateDocument: (field: keyof DocumentSettings, value: string | number) => void }) {
  const onUpdateTax = editable ? saveTaxUpdate : () => undefined
  return <div className="settings-panel-stack"><SettingsPanelHeader icon={ReceiptText} title="税・端数処理" description="販売書類・整備書類の金額計算に使う初期値です。" /><section className="panel settings-panel"><div className="settings-section-heading"><ReceiptText size={18} /><div><h2>消費税</h2><p>書類作成時の税率と端数処理を設定します。</p></div></div><div className="settings-form-grid"><label className="form-field"><span>消費税率</span><div className="settings-number-input"><input type="number" min="0" max="100" value={settings.tax.consumptionTaxRate} onChange={(event) => onUpdateTax('consumptionTaxRate', Number(event.target.value))} /><span>%</span></div></label><label className="form-field"><span>端数処理</span><select value={settings.tax.rounding} onChange={(event) => onUpdateTax('rounding', event.target.value)}><option value="切り捨て">切り捨て</option><option value="四捨五入">四捨五入</option></select></label></div></section><section className="panel settings-panel"><div className="settings-section-heading"><FileText size={18} /><div><h2>帳票の初期値</h2><p>新しい販売書類を作成するときに適用します。</p></div></div><div className="settings-form-grid"><label className="form-field"><span>支払期限の初期日数</span><div className="settings-number-input"><input type="number" min="0" max="365" value={settings.document.defaultDueDays} onChange={(event) => onUpdateDocument('defaultDueDays', Number(event.target.value))} /><span>日後</span></div></label></div></section></div>
}

function MasterSettingsPanel({ settings, onUpdateSales, onAddSales, onRemoveSales, onUpdateMaintenance, onAddMaintenance, onRemoveMaintenance }: { settings: AppSettings; onUpdateSales: (group: SalesItemPresetGroupKey, index: number, value: string) => void; onAddSales: (group: SalesItemPresetGroupKey) => void; onRemoveSales: (group: SalesItemPresetGroupKey, index: number) => void; onUpdateMaintenance: (index: number, value: string) => void; onAddMaintenance: () => void; onRemoveMaintenance: (index: number) => void }) {
  return <div className="settings-panel-stack"><SettingsPanelHeader icon={Settings2} title="明細候補" description="販売書類・整備書類で選択できる定型項目です。" /><SalesPresetPanel groups={settings.salesItemPresetGroups} onUpdate={onUpdateSales} onAdd={onAddSales} onRemove={onRemoveSales} /><PresetPanel title="整備作業・部品候補" description="作業内容や部品名など" items={settings.maintenanceItemPresets} onUpdate={onUpdateMaintenance} onAdd={onAddMaintenance} onRemove={onRemoveMaintenance} /></div>
}

function SettingsPanelHeader({ icon: Icon, title, description }: { icon: typeof Building2; title: string; description: string }) {
  return <div className="settings-panel-heading"><span className="settings-panel-icon"><Icon size={22} /></span><div><span className="page-eyebrow">設定項目</span><h2>{title}</h2><p>{description}</p></div></div>
}

function SettingsField({ label, value, onChange, placeholder, required, wide, type = 'text', disabled = false, normalization }: { label: string; value: string; onChange: (value: string) => void; placeholder?: string; required?: boolean; wide?: boolean; type?: 'text' | 'email' | 'password'; disabled?: boolean; normalization?: NormalizableField }) {
  return <label className={`form-field${wide ? ' settings-field-wide' : ''}`}><span>{label}{required && <em>必須</em>}</span>{normalization ? <NormalizedInput field={normalization} inputMode={normalization === 'phone' ? 'tel' : 'numeric'} type="text" required={required} disabled={disabled} value={value} onChange={onChange} placeholder={placeholder} /> : <input type={type} required={required} disabled={disabled} value={value} onChange={(event) => onChange(event.target.value)} placeholder={placeholder} />}</label>
}

function SalesPresetPanel({ groups, onUpdate, onAdd, onRemove }: { groups: SalesItemPresetGroups; onUpdate: (group: SalesItemPresetGroupKey, index: number, value: string) => void; onAdd: (group: SalesItemPresetGroupKey) => void; onRemove: (group: SalesItemPresetGroupKey, index: number) => void }) {
  return <section className="panel settings-panel"><div className="settings-section-heading"><FileText size={18} /><div><h2>販売明細候補</h2><p>見積書プレビューの3つの内訳ブロックごとに、プルダウンへ表示する候補を管理します。</p></div></div><div className="settings-sales-preset-grid">{salesPresetColumns.map(({ key, title, description }) => {
    const items = groups[key]
    return <section className="settings-sales-preset-column" key={key} aria-labelledby={`sales-preset-${key}`}><div className="settings-sales-preset-column-heading"><div><h3 id={`sales-preset-${key}`}>{title}</h3><p>{description}</p></div><button className="button button-secondary" type="button" onClick={() => onAdd(key)}><Plus size={14} />追加</button></div><div className="settings-preset-list">{items.map((item, index) => <PresetRow key={`${key}-${index}`} item={item} index={index} ariaPrefix={title} onUpdate={(value) => onUpdate(key, index, value)} onRemove={() => onRemove(key, index)} />)}{items.length === 0 && <div className="settings-preset-empty settings-sales-preset-empty">項目はありません。「追加」から登録できます。</div>}</div></section>
  })}</div></section>
}

function PresetPanel({ title, description, items, onUpdate, onAdd, onRemove }: { title: string; description: string; items: string[]; onUpdate: (index: number, value: string) => void; onAdd: () => void; onRemove: (index: number) => void }) {
  return <section className="panel settings-panel"><div className="settings-section-heading"><FileText size={18} /><div><h2>{title}</h2><p>{description}</p></div><button className="button button-secondary settings-add-button" type="button" onClick={onAdd}><Plus size={15} />項目を追加</button></div><div className="settings-preset-list">{items.map((item, index) => <PresetRow key={`maintenance-${index}`} item={item} index={index} ariaPrefix={title} onUpdate={(value) => onUpdate(index, value)} onRemove={() => onRemove(index)} />)}{items.length === 0 && <div className="settings-preset-empty">登録されている項目はありません。右上の「項目を追加」から登録できます。</div>}</div></section>
}

function PresetRow({ item, index, ariaPrefix, onUpdate, onRemove }: { item: string; index: number; ariaPrefix: string; onUpdate: (value: string) => void; onRemove: () => void }) {
  return <div className="settings-preset-row"><span className="settings-preset-index">{index + 1}</span><input aria-label={`${ariaPrefix}の${index + 1}番目の項目`} value={item} onChange={(event) => onUpdate(event.target.value)} placeholder="項目名" /><button className="icon-button" type="button" aria-label={`${ariaPrefix}の${index + 1}番目の項目を削除`} onClick={onRemove}><Trash2 size={16} /></button></div>
}
