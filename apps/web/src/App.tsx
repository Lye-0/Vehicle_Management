import { useEffect, useState, type FormEvent, type ReactNode } from 'react'
import type { User } from 'firebase/auth'
import type { LucideIcon } from 'lucide-react'
import {
  ArrowUpRight,
  Bell,
  CalendarDays,
  CarFront,
  ChevronRight,
  CircleDollarSign,
  ClipboardCheck,
  FileText,
  LayoutDashboard,
  LogOut,
  Mail,
  Plus,
  Search,
  Settings,
  UserRound,
} from 'lucide-react'
import { CustomerVehiclePage } from './components/CustomerVehiclePage'
import { MaintenancePage } from './components/MaintenancePage'
import { PaymentsPage } from './components/PaymentsPage'
import { SalesPage } from './components/SalesPage'
import { SettingsPage } from './components/SettingsPage'
import { observeAuthState, sendPasswordReset, signInAnonymouslyForDevelopment, signInWithEmailPassword, signInWithGoogle, signOutCurrentUser } from './lib/auth'
import { fetchDashboard, type DashboardData } from './lib/dashboardApi'
import './App.css'

type SectionId = 'dashboard' | 'customers' | 'sales' | 'maintenance' | 'payments' | 'settings'

type PageMeta = {
  title: string
  description: string
  actionLabel: string
  icon: LucideIcon
}

const navItems: { id: SectionId; label: string; icon: LucideIcon }[] = [
  { id: 'dashboard', label: 'ダッシュボード', icon: LayoutDashboard },
  { id: 'customers', label: '顧客・車両', icon: CarFront },
  { id: 'sales', label: '販売', icon: FileText },
  { id: 'maintenance', label: '車検・点検・一般', icon: ClipboardCheck },
  { id: 'payments', label: '入金管理', icon: CircleDollarSign },
  { id: 'settings', label: '設定', icon: Settings },
]

const pageMeta: Record<SectionId, PageMeta> = {
  dashboard: { title: 'ダッシュボード', description: '店舗の状況をひと目で確認できます。', actionLabel: 'クイック操作', icon: LayoutDashboard },
  customers: { title: '顧客・車両', description: '顧客情報と、顧客に紐づく複数の車両を管理します。', actionLabel: '顧客を登録', icon: CarFront },
  sales: { title: '販売', description: '見積書・注文書・請求書を車両情報と連動して管理します。', actionLabel: '販売書類を作成', icon: FileText },
  maintenance: { title: '車検・点検・一般', description: '整備の受付から作業明細、納品書・請求書まで管理します。', actionLabel: '整備書類を作成', icon: ClipboardCheck },
  payments: { title: '入金管理', description: '請求に対する入金状況を確認し、未入金を管理します。', actionLabel: '入金を登録', icon: CircleDollarSign },
  settings: { title: '設定', description: '帳票、税金・保険料、作業項目などの共通設定を管理します。', actionLabel: '設定を追加', icon: Settings },
}

function App() {
  const [authState, setAuthState] = useState<{ loading: boolean; user: User | null; error: string }>({ loading: true, user: null, error: '' })

  useEffect(() => {
    try {
      return observeAuthState((user) => setAuthState({ loading: false, user, error: '' }))
    } catch (error) {
      setAuthState({ loading: false, user: null, error: getAuthErrorMessage(error) })
    }
  }, [])

  if (authState.loading) return <AuthLoading />
  if (!authState.user) return <LoginPage initialError={authState.error} />
  return <AuthenticatedApp user={authState.user} />
}

function AuthenticatedApp({ user }: { user: User }) {
  const [activeSection, setActiveSection] = useState<SectionId>('dashboard')

  return (
    <div className="app-shell">
      <Sidebar user={user} activeSection={activeSection} onSelect={setActiveSection} onSignOut={() => void signOutCurrentUser()} />
      <main className="app-main">
        <Topbar currentPage={pageMeta[activeSection]} />
        <div className="page-content">
          {activeSection === 'dashboard' ? <Dashboard /> : activeSection === 'customers' ? <CustomerVehiclePage /> : activeSection === 'sales' ? <SalesPage /> : activeSection === 'maintenance' ? <MaintenancePage /> : activeSection === 'payments' ? <PaymentsPage /> : <SettingsPage />}
        </div>
      </main>
    </div>
  )
}

function AuthLoading() {
  return <div className="auth-page"><div className="auth-card auth-loading"><span className="brand-mark" aria-hidden="true"><CarFront size={24} strokeWidth={2.4} /></span><strong>車両管理を起動しています</strong><span>認証状態を確認しています。</span></div></div>
}

function LoginPage({ initialError }: { initialError?: string }) {
  const [error, setError] = useState(initialError ?? '')
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [resetMode, setResetMode] = useState(false)
  const [loading, setLoading] = useState<'email' | 'google' | 'anonymous' | 'reset' | ''>('')

  async function runEmailSignIn(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setError('')
    setLoading('email')
    try {
      if (!email.trim() || !password) throw new Error('メールアドレスとパスワードを入力してください。')
      await signInWithEmailPassword(email, password)
    } catch (reason) {
      setError(getAuthErrorMessage(reason))
    } finally {
      setLoading('')
    }
  }

  async function runGoogleSignIn() {
    setError('')
    setLoading('google')
    try {
      await signInWithGoogle()
    } catch (reason) {
      setError(getAuthErrorMessage(reason))
    } finally {
      setLoading('')
    }
  }

  async function runPasswordReset(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setError('')
    setLoading('reset')
    try {
      if (!email.trim()) throw new Error('メールアドレスを入力してください。')
      await sendPasswordReset(email)
      window.alert('パスワード再設定メールを送信しました。メールをご確認ください。')
      setResetMode(false)
    } catch (reason) {
      setError(getAuthErrorMessage(reason))
    } finally {
      setLoading('')
    }
  }

  async function runAnonymousSignIn() {
    setError('')
    setLoading('anonymous')
    try {
      await signInAnonymouslyForDevelopment()
    } catch (reason) {
      setError(getAuthErrorMessage(reason))
    } finally {
      setLoading('')
    }
  }

  const isDevelopment = import.meta.env.DEV && Boolean(import.meta.env.VITE_FIREBASE_AUTH_EMULATOR_URL)
  return (
    <div className="auth-page">
      <section className="auth-card">
        <div className="auth-brand"><span className="brand-mark" aria-hidden="true"><CarFront size={24} strokeWidth={2.4} /></span><div><strong>車両管理</strong><small>ABACUS Refresh</small></div></div>
        <span className="page-eyebrow">SECURE SIGN IN</span>
        <h1>{resetMode ? 'パスワードを再設定' : '業務画面にログイン'}</h1>
        <p>{resetMode ? '登録済みのメールアドレスに再設定用のメールを送信します。' : '顧客・車両、販売、整備、入金の情報を安全に管理します。'}</p>
        {error && <div className="auth-error" role="alert">{error}</div>}
        {resetMode ? <form className="auth-form" onSubmit={(event) => void runPasswordReset(event)}>
          <label className="form-field"><span>メールアドレス</span><span className="auth-input-wrap"><Mail size={16} aria-hidden="true" /><input type="email" autoComplete="email" value={email} onChange={(event) => setEmail(event.target.value)} placeholder="example@shop.jp" disabled={Boolean(loading)} /></span></label>
          <button className="button button-primary auth-signin-button" type="submit" disabled={Boolean(loading)}>{loading === 'reset' ? '送信しています…' : '再設定メールを送信'}</button>
          <button className="text-button auth-back-button" type="button" disabled={Boolean(loading)} onClick={() => { setError(''); setResetMode(false) }}>ログイン画面に戻る</button>
        </form> : <>
          <form className="auth-form" onSubmit={(event) => void runEmailSignIn(event)}>
            <label className="form-field"><span>メールアドレス</span><span className="auth-input-wrap"><Mail size={16} aria-hidden="true" /><input type="email" autoComplete="email" value={email} onChange={(event) => setEmail(event.target.value)} placeholder="example@shop.jp" disabled={Boolean(loading)} /></span></label>
            <label className="form-field"><span>パスワード</span><input type="password" autoComplete="current-password" value={password} onChange={(event) => setPassword(event.target.value)} placeholder="パスワードを入力" disabled={Boolean(loading)} /></label>
            <button className="button button-primary auth-signin-button" type="submit" disabled={Boolean(loading)}>{loading === 'email' ? 'ログインしています…' : 'メールアドレスでログイン'}</button>
          </form>
          <button className="text-button auth-reset-button" type="button" disabled={Boolean(loading)} onClick={() => { setError(''); setResetMode(true) }}>パスワードを忘れた場合</button>
          <div className="auth-divider" aria-hidden="true"><span>または</span></div>
          <button className="button button-secondary auth-signin-button" type="button" disabled={Boolean(loading)} onClick={() => void runGoogleSignIn()}>
            {loading === 'google' ? 'ログインしています…' : 'Googleでログイン'}
          </button>
        </>}
        <small className="auth-hint">{isDevelopment ? '現在はFirebase Auth Emulatorに接続しています。' : 'ログインには登録済みのメールアドレスまたはGoogleアカウントを使用してください。'}</small>
      </section>
      {isDevelopment && <button className="auth-dev-login" type="button" disabled={Boolean(loading)} onClick={() => void runAnonymousSignIn()}>{loading === 'anonymous' ? '接続しています…' : '開発用匿名ログイン'}</button>}
    </div>
  )
}

function getAuthErrorMessage(error: unknown) {
  if (error instanceof Error && error.message) {
    if (error.message.includes('popup-closed-by-user')) return 'ログイン画面が閉じられました。もう一度お試しください。'
    if (error.message.includes('auth/network-request-failed')) return '認証サーバーに接続できません。Auth Emulatorまたはネットワークを確認してください。'
    if (error.message.includes('auth/invalid-credential') || error.message.includes('auth/invalid-login-credentials') || error.message.includes('auth/wrong-password') || error.message.includes('auth/user-not-found')) return 'メールアドレスまたはパスワードが正しくありません。'
    if (error.message.includes('auth/invalid-email')) return 'メールアドレスの形式を確認してください。'
    if (error.message.includes('auth/too-many-requests')) return '試行回数が多すぎます。時間を置いてから再度お試しください。'
    if (error.message.includes('auth/user-disabled')) return 'このアカウントは現在利用できません。管理者に確認してください。'
    return error.message
  }
  return 'ログインに失敗しました。設定と接続を確認してください。'
}

function Sidebar({ user, activeSection, onSelect, onSignOut }: { user: User; activeSection: SectionId; onSelect: (section: SectionId) => void; onSignOut: () => void }) {
  const profileName = user.displayName || user.email || 'ログインユーザー'
  const profileRole = user.isAnonymous ? '開発用アカウント' : 'サービスアドバイザー'
  return (
    <aside className="sidebar">
      <div className="sidebar-brand">
        <span className="brand-mark" aria-hidden="true"><CarFront size={24} strokeWidth={2.4} /></span>
        <span className="brand-copy"><strong>車両管理</strong><small>ABACUS Refresh</small></span>
      </div>
      <div className="branch-card"><span>店舗</span><strong>東京都心支店</strong></div>
      <nav className="sidebar-nav" aria-label="メインメニュー">
        {navItems.map(({ id, label, icon: Icon }) => {
          const isActive = activeSection === id
          return (
            <button className={`nav-item${isActive ? ' is-active' : ''}`} key={id} type="button" aria-current={isActive ? 'page' : undefined} onClick={() => onSelect(id)}>
              <Icon size={22} strokeWidth={2} /><span>{label}</span>
            </button>
          )
        })}
      </nav>
      <div className="sidebar-footer">
        <span className="avatar" aria-hidden="true">{user.photoURL ? <img src={user.photoURL} alt="" /> : <UserRound size={21} />}</span>
        <span className="profile-copy"><strong>{profileName}</strong><small>{profileRole}</small></span>
        <button className="sidebar-signout" type="button" aria-label="ログアウト" title="ログアウト" onClick={onSignOut}><LogOut size={17} /></button>
      </div>
    </aside>
  )
}

function Topbar({ currentPage }: { currentPage: PageMeta }) {
  const Icon = currentPage.icon
  return (
    <header className="topbar">
      <div className="breadcrumb"><span>車両管理</span><ChevronRight size={16} /><strong>{currentPage.title}</strong></div>
      <div className="topbar-actions">
        <button className="search-trigger" type="button"><Search size={18} /><span>顧客・車両を検索</span><kbd>⌘ K</kbd></button>
        <button className="icon-button notification-button" type="button" aria-label="通知"><Bell size={20} /><span className="notification-dot" /></button>
        <span className="topbar-page-icon" aria-hidden="true"><Icon size={20} /></span>
      </div>
    </header>
  )
}

function Dashboard() {
  const [dashboard, setDashboard] = useState<DashboardData | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')

  useEffect(() => {
    let cancelled = false
    fetchDashboard()
      .then((nextDashboard) => {
        if (!cancelled) {
          setDashboard(nextDashboard)
          setError('')
        }
      })
      .catch((reason: unknown) => {
        if (!cancelled) setError(reason instanceof Error ? reason.message : 'ダッシュボードを読み込めませんでした。')
      })
      .finally(() => {
        if (!cancelled) setLoading(false)
      })
    return () => { cancelled = true }
  }, [])

  const summary = dashboard?.summary
  return (
    <>
      <PageHeader eyebrow="本日の状況" title="ダッシュボード" description="店舗の状況をひと目で確認できます。" action={<button className="button button-primary" type="button"><Plus size={18} />クイック操作</button>} />
      {error && <div className="customer-sync-status is-error"><span>{error}</span><button className="text-button" type="button" onClick={() => window.location.reload()}>再読み込み</button></div>}
      {loading && <div className="customer-sync-status"><span>店舗データを集計しています。</span></div>}
      <section className="stats-grid" aria-label="概要">
        <StatCard label="登録車両" value={String(summary?.registeredVehicles ?? 0)} suffix="台" note="現在の登録台数" icon={CarFront} tone="blue" />
        <StatCard label="今月の売上" value={formatYen(summary?.monthlySales ?? 0)} note="販売・整備の合計" icon={ArrowUpRight} tone="green" />
        <StatCard label="車検期限30日以内" value={String(summary?.inspectionsWithin30Days ?? 0)} suffix="台" note={`期限超過 ${summary?.overdueInspections ?? 0}台`} icon={CalendarDays} tone="orange" />
        <StatCard label="未入金の請求" value={String(summary?.unpaidInvoices ?? 0)} suffix="件" note={`合計 ${formatYen(summary?.unpaidAmount ?? 0)}`} icon={CircleDollarSign} tone="red" />
      </section>
      <section className="dashboard-grid">
        <Panel title="車検・点検期限が近い車両" action="一覧を見る">
          <div className="data-list">{dashboard?.inspections.length ? dashboard.inspections.map((row) => <div className="data-list-row" key={`${row.customer}-${row.date}-${row.plate}`}><span className="row-icon row-icon-blue"><CarFront size={18} /></span><span className="row-copy"><strong>{row.customer}</strong><small>{row.vehicle} ・ {row.plate}</small></span><span className="row-trailing"><StatusBadge tone={row.tone}>{row.date}</StatusBadge></span></div>) : <DashboardEmpty loading={loading}>対象車両はありません。</DashboardEmpty>}</div>
        </Panel>
        <Panel title="未入金の請求" action="入金管理を見る">
          <div className="data-list">{dashboard?.unpaidInvoices.length ? dashboard.unpaidInvoices.map((row) => <div className="data-list-row" key={row.document}><span className="row-icon row-icon-orange"><CircleDollarSign size={18} /></span><span className="row-copy"><strong>{row.customer}</strong><small>{row.document}</small></span><span className="row-trailing row-trailing-payment"><strong>{formatYen(row.amount)}</strong><StatusBadge tone={row.tone}>{row.due}</StatusBadge></span></div>) : <DashboardEmpty loading={loading}>未入金の請求はありません。</DashboardEmpty>}</div>
        </Panel>
        <Panel title="最近の更新" action="履歴を見る">
          <div className="activity-list">{dashboard?.recentActivities.length ? dashboard.recentActivities.map((activity) => <div className="activity-row" key={`${activity.kind}-${activity.label}-${activity.at}`}><span className="activity-icon"><RecentActivityIcon kind={activity.kind} /></span><span className="row-copy"><strong>{activity.label}</strong><small>{activity.detail}</small></span><small className="activity-time">{formatRelativeTime(activity.at)}</small></div>) : <DashboardEmpty loading={loading}>最近の更新はありません。</DashboardEmpty>}</div>
        </Panel>
        <Panel title="クイック操作" className="quick-panel">
          <div className="quick-actions"><QuickAction icon={Search} label="顧客・車両を検索" /><QuickAction icon={FileText} label="販売書類を作成" /><QuickAction icon={ClipboardCheck} label="整備書類を作成" /><QuickAction icon={CircleDollarSign} label="入金を登録" /></div>
        </Panel>
      </section>
    </>
  )
}

function DashboardEmpty({ loading, children }: { loading: boolean; children: ReactNode }) {
  return <div className="dashboard-empty">{loading ? '読み込み中…' : children}</div>
}

function RecentActivityIcon({ kind }: { kind: DashboardData['recentActivities'][number]['kind'] }) {
  const Icon = kind === 'sales' ? FileText : kind === 'vehicle' ? CarFront : CircleDollarSign
  return <Icon size={17} />
}

function formatYen(amount: number) {
  return `¥${new Intl.NumberFormat('ja-JP').format(Math.round(amount))}`
}

function formatRelativeTime(value: string) {
  const date = new Date(value.replace(' ', 'T') + (value.includes('Z') ? '' : 'Z'))
  if (Number.isNaN(date.getTime())) return '日時不明'
  const diffMinutes = Math.max(0, Math.floor((Date.now() - date.getTime()) / 60000))
  if (diffMinutes < 1) return 'たった今'
  if (diffMinutes < 60) return `${diffMinutes}分前`
  const diffHours = Math.floor(diffMinutes / 60)
  if (diffHours < 24) return `${diffHours}時間前`
  return `${Math.floor(diffHours / 24)}日前`
}

function PageHeader({ eyebrow, title, description, action }: { eyebrow: string; title: string; description: string; action?: ReactNode }) {
  return <div className="page-header"><div><span className="page-eyebrow">{eyebrow}</span><h1>{title}</h1><p>{description}</p></div>{action}</div>
}

function StatCard({ label, value, suffix, note, icon: Icon, tone }: { label: string; value: string; suffix?: string; note: string; icon: LucideIcon; tone: 'blue' | 'green' | 'orange' | 'red' }) {
  return <article className="stat-card"><div className={`stat-icon stat-icon-${tone}`}><Icon size={20} /></div><span className="stat-label">{label}</span><div className="stat-value"><strong>{value}</strong>{suffix && <span>{suffix}</span>}</div><span className={`stat-note stat-note-${tone}`}>{note}</span></article>
}

function Panel({ title, action, children, className = '' }: { title: string; action?: string; children: ReactNode; className?: string }) {
  return <article className={`panel ${className}`}><div className="panel-header"><h2>{title}</h2>{action && <button className="text-button" type="button">{action}<ChevronRight size={16} /></button>}</div>{children}</article>
}

function StatusBadge({ tone, children }: { tone: string; children: ReactNode }) {
  return <span className={`status-badge status-${tone}`}><span className="status-dot" />{children}</span>
}

function QuickAction({ icon: Icon, label }: { icon: LucideIcon; label: string }) {
  return <button className="quick-action" type="button"><span className="quick-action-icon"><Icon size={19} /></span><span>{label}</span><ChevronRight size={16} /></button>
}

export default App
