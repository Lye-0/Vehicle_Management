import { useState, type ReactNode } from 'react'
import type { LucideIcon } from 'lucide-react'
import {
  ArrowUpRight,
  Bell,
  CalendarDays,
  CarFront,
  CheckCircle2,
  ChevronRight,
  CircleDollarSign,
  ClipboardCheck,
  FileText,
  LayoutDashboard,
  Paperclip,
  Plus,
  Search,
  Settings,
  UserRound,
} from 'lucide-react'
import { CustomerVehiclePage } from './components/CustomerVehiclePage'
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

const inspectionRows = [
  { customer: '佐藤 太郎', vehicle: 'トヨタ プリウス', plate: '品川 500 あ 1234', date: '2026/10/15', tone: 'normal' },
  { customer: '田中 花子', vehicle: 'ホンダ フィット', plate: '横浜 300 い 5678', date: '2026/08/20', tone: 'warning' },
  { customer: '鈴木 一郎', vehicle: 'ニッサン ノート', plate: '大宮 400 う 9012', date: '2025/12/01', tone: 'danger' },
]

const paymentRows = [
  { customer: '高橋 美咲', document: '販売請求書 #S-2026-041', amount: '¥1,280,000', due: '期限まで8日', tone: 'warning' },
  { customer: '伊藤 雄介', document: '整備請求書 #M-2026-118', amount: '¥86,420', due: '期限超過', tone: 'danger' },
  { customer: '山田 恵子', document: '販売請求書 #S-2026-039', amount: '¥420,000', due: '期限まで15日', tone: 'normal' },
]

const recentActivities = [
  { label: '販売見積書を作成', detail: '佐藤 太郎・トヨタ プリウス', time: '10分前', icon: FileText },
  { label: '車両情報を更新', detail: '田中 花子・ホンダ フィット', time: '1時間前', icon: CarFront },
  { label: '入金を登録', detail: '山田 恵子・¥120,000', time: '昨日', icon: CircleDollarSign },
]

function App() {
  const [activeSection, setActiveSection] = useState<SectionId>('dashboard')

  return (
    <div className="app-shell">
      <Sidebar activeSection={activeSection} onSelect={setActiveSection} />
      <main className="app-main">
        <Topbar currentPage={pageMeta[activeSection]} />
        <div className="page-content">
          {activeSection === 'dashboard' ? <Dashboard /> : activeSection === 'customers' ? <CustomerVehiclePage /> : <SectionPlaceholder section={activeSection} />}
        </div>
      </main>
    </div>
  )
}

function Sidebar({ activeSection, onSelect }: { activeSection: SectionId; onSelect: (section: SectionId) => void }) {
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
        <span className="avatar" aria-hidden="true"><UserRound size={21} /></span>
        <span className="profile-copy"><strong>山本 翔太</strong><small>サービスアドバイザー</small></span>
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
  return (
    <>
      <PageHeader eyebrow="本日の状況" title="ダッシュボード" description="店舗の状況をひと目で確認できます。" action={<button className="button button-primary" type="button"><Plus size={18} />クイック操作</button>} />
      <section className="stats-grid" aria-label="概要">
        <StatCard label="登録車両" value="128" suffix="台" note="先月比 +6台" icon={CarFront} tone="blue" />
        <StatCard label="今月の売上" value="¥8,420,000" note="先月比 +12.4%" icon={ArrowUpRight} tone="green" />
        <StatCard label="車検期限30日以内" value="12" suffix="台" note="要確認 3台" icon={CalendarDays} tone="orange" />
        <StatCard label="未入金の請求" value="3" suffix="件" note="合計 ¥1,786,420" icon={CircleDollarSign} tone="red" />
      </section>
      <section className="dashboard-grid">
        <Panel title="車検・点検期限が近い車両" action="一覧を見る">
          <div className="data-list">{inspectionRows.map((row) => <div className="data-list-row" key={`${row.customer}-${row.date}`}><span className="row-icon row-icon-blue"><CarFront size={18} /></span><span className="row-copy"><strong>{row.customer}</strong><small>{row.vehicle} ・ {row.plate}</small></span><span className="row-trailing"><StatusBadge tone={row.tone}>{row.date}</StatusBadge></span></div>)}</div>
        </Panel>
        <Panel title="未入金の請求" action="入金管理を見る">
          <div className="data-list">{paymentRows.map((row) => <div className="data-list-row" key={row.document}><span className="row-icon row-icon-orange"><CircleDollarSign size={18} /></span><span className="row-copy"><strong>{row.customer}</strong><small>{row.document}</small></span><span className="row-trailing row-trailing-payment"><strong>{row.amount}</strong><StatusBadge tone={row.tone}>{row.due}</StatusBadge></span></div>)}</div>
        </Panel>
        <Panel title="最近の更新" action="履歴を見る">
          <div className="activity-list">{recentActivities.map(({ label, detail, time, icon: Icon }) => <div className="activity-row" key={`${label}-${time}`}><span className="activity-icon"><Icon size={17} /></span><span className="row-copy"><strong>{label}</strong><small>{detail}</small></span><small className="activity-time">{time}</small></div>)}</div>
        </Panel>
        <Panel title="クイック操作" className="quick-panel">
          <div className="quick-actions"><QuickAction icon={Search} label="顧客・車両を検索" /><QuickAction icon={FileText} label="販売書類を作成" /><QuickAction icon={ClipboardCheck} label="整備書類を作成" /><QuickAction icon={CircleDollarSign} label="入金を登録" /></div>
        </Panel>
      </section>
    </>
  )
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

function SectionPlaceholder({ section }: { section: SectionId }) {
  const currentPage = pageMeta[section]
  const Icon = currentPage.icon
  return (
    <>
      <PageHeader eyebrow="業務メニュー" title={currentPage.title} description={currentPage.description} action={<button className="button button-primary" type="button"><Plus size={18} />{currentPage.actionLabel}</button>} />
      <section className="placeholder-card"><span className="placeholder-icon"><Icon size={30} /></span><div><span className="page-eyebrow">UI基盤</span><h2>{currentPage.title}の画面を準備中です</h2><p>共通のレイアウトと操作部品を確認するための仮画面です。各業務機能は設計順に実装します。</p><div className="placeholder-items"><span><CheckCircle2 size={16} />画面レイアウト</span><span><CheckCircle2 size={16} />入力・一覧部品</span><span><Paperclip size={16} />添付ファイル対応</span></div></div></section>
    </>
  )
}

export default App
