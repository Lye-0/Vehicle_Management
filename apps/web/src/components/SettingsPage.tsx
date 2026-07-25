import { useEffect, useState } from 'react'
import { Banknote, Building2, FileText, Plus, ReceiptText, Save, Settings2, Trash2 } from 'lucide-react'
import { defaultSettings, fetchSettings, updateSettings, type AppSettings, type DocumentSettings, type ShopSettings, type TaxSettings } from '../lib/settingsApi'

type SettingsTab = 'shop' | 'tax' | 'masters'

const tabs: Array<{ id: SettingsTab; label: string; description: string; icon: typeof Building2 }> = [
  { id: 'shop', label: '店舗・帳票', description: '店舗情報と帳票に表示する内容', icon: Building2 },
  { id: 'tax', label: '税・端数処理', description: '消費税と請求期限の初期値', icon: ReceiptText },
  { id: 'masters', label: '明細候補', description: '販売・整備で選べる項目', icon: Settings2 },
]

export function SettingsPage() {
  const [settings, setSettings] = useState<AppSettings>(defaultSettings)
  const [activeTab, setActiveTab] = useState<SettingsTab>('shop')
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [saved, setSaved] = useState(false)
  const [error, setError] = useState('')

  useEffect(() => {
    let cancelled = false
    fetchSettings()
      .then((nextSettings) => {
        if (!cancelled) {
          setSettings(nextSettings)
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

  function updatePreset(kind: 'salesItemPresets' | 'maintenanceItemPresets', index: number, value: string) {
    setSettings((current) => ({ ...current, [kind]: current[kind].map((item, itemIndex) => itemIndex === index ? value : item) }))
    setSaved(false)
  }

  function addPreset(kind: 'salesItemPresets' | 'maintenanceItemPresets') {
    setSettings((current) => ({ ...current, [kind]: [...current[kind], ''] }))
    setSaved(false)
  }

  function removePreset(kind: 'salesItemPresets' | 'maintenanceItemPresets', index: number) {
    setSettings((current) => ({ ...current, [kind]: current[kind].filter((_, itemIndex) => itemIndex !== index) }))
    setSaved(false)
  }

  async function save() {
    if (saving) return
    setSaving(true)
    setSaved(false)
    try {
      const nextSettings = await updateSettings(settings)
      setSettings(nextSettings)
      setSaved(true)
      setError('')
    } catch (reason: unknown) {
      setError(reason instanceof Error ? reason.message : '設定を保存できませんでした。')
    } finally {
      setSaving(false)
    }
  }

  return (
    <>
      <div className="page-header settings-page-header"><div><span className="page-eyebrow">共通設定</span><h1>設定</h1><p>店舗情報、帳票、税金・保険料、明細候補を管理します。</p></div><button className="button button-primary" type="button" onClick={save} disabled={loading || saving}><Save size={18} />{saving ? '保存中…' : saved ? '保存済み' : '設定を保存'}</button></div>
      {error && <div className="customer-sync-status is-error"><span>{error}</span><button className="text-button" type="button" onClick={() => window.location.reload()}>再読み込み</button></div>}
      <div className="settings-layout">
        <nav className="panel settings-nav" aria-label="設定メニュー">{tabs.map(({ id, label, description, icon: Icon }) => <button className={activeTab === id ? 'is-active' : ''} type="button" key={id} onClick={() => setActiveTab(id)}><span className="settings-nav-icon"><Icon size={18} /></span><span><strong>{label}</strong><small>{description}</small></span></button>)}</nav>
        <section className="settings-content" aria-live="polite">{loading ? <div className="panel settings-empty"><Settings2 size={28} /><strong>設定を読み込んでいます</strong><span>しばらくお待ちください。</span></div> : activeTab === 'shop' ? <ShopSettingsPanel settings={settings} onUpdate={updateShop} onUpdateDocument={updateDocument} /> : activeTab === 'tax' ? <TaxSettingsPanel settings={settings} onUpdateTax={updateTax} onUpdateDocument={updateDocument} /> : <MasterSettingsPanel settings={settings} onUpdate={updatePreset} onAdd={addPreset} onRemove={removePreset} />}</section>
      </div>
    </>
  )
}

function ShopSettingsPanel({ settings, onUpdate, onUpdateDocument }: { settings: AppSettings; onUpdate: (field: keyof ShopSettings, value: string) => void; onUpdateDocument: (field: keyof DocumentSettings, value: string | number) => void }) {
  return <div className="settings-panel-stack"><SettingsPanelHeader icon={Building2} title="店舗・帳票情報" description="見積書、注文書、請求書に表示する基本情報です。" /><section className="panel settings-panel"><div className="settings-section-heading"><Building2 size={18} /><div><h2>店舗情報</h2><p>店舗名や連絡先は帳票の発行元として利用します。</p></div></div><div className="settings-form-grid"><SettingsField label="店舗名" value={settings.shop.name} onChange={(value) => onUpdate('name', value)} required /><SettingsField label="郵便番号" value={settings.shop.postalCode} onChange={(value) => onUpdate('postalCode', value)} placeholder="例：100-0001" /><SettingsField label="電話番号" value={settings.shop.phone} onChange={(value) => onUpdate('phone', value)} placeholder="例：03-0000-0000" /><SettingsField label="担当者名" value={settings.shop.representative} onChange={(value) => onUpdate('representative', value)} /><SettingsField label="適格請求書発行事業者番号" value={settings.shop.registrationNumber} onChange={(value) => onUpdate('registrationNumber', value)} placeholder="例：T1234567890123" /><SettingsField label="住所" value={settings.shop.address} onChange={(value) => onUpdate('address', value)} wide /></div></section><section className="panel settings-panel"><div className="settings-section-heading"><Banknote size={18} /><div><h2>振込先・帳票フッター</h2><p>請求書などに表示する支払情報を設定します。</p></div></div><div className="settings-form-grid"><SettingsField label="金融機関名" value={settings.shop.bankName} onChange={(value) => onUpdate('bankName', value)} /><SettingsField label="口座情報" value={settings.shop.bankAccount} onChange={(value) => onUpdate('bankAccount', value)} /><SettingsField label="帳票フッター" value={settings.document.footerNote} onChange={(value) => onUpdateDocument('footerNote', value)} wide /><SettingsField label="支払案内" value={settings.document.paymentNote} onChange={(value) => onUpdateDocument('paymentNote', value)} wide /></div></section></div>
}

function TaxSettingsPanel({ settings, onUpdateTax, onUpdateDocument }: { settings: AppSettings; onUpdateTax: (field: keyof TaxSettings, value: string | number) => void; onUpdateDocument: (field: keyof DocumentSettings, value: string | number) => void }) {
  return <div className="settings-panel-stack"><SettingsPanelHeader icon={ReceiptText} title="税・端数処理" description="販売書類・整備書類の金額計算に使う初期値です。" /><section className="panel settings-panel"><div className="settings-section-heading"><ReceiptText size={18} /><div><h2>消費税</h2><p>書類作成時の税率と表示方法を設定します。</p></div></div><div className="settings-form-grid"><label className="form-field"><span>消費税率</span><div className="settings-number-input"><input type="number" min="0" max="100" value={settings.tax.consumptionTaxRate} onChange={(event) => onUpdateTax('consumptionTaxRate', Number(event.target.value))} /><span>%</span></div></label><label className="form-field"><span>金額表示</span><select value={settings.tax.display} onChange={(event) => onUpdateTax('display', event.target.value)}><option value="税込">税込</option><option value="税別">税別</option></select></label><label className="form-field"><span>端数処理</span><select value={settings.tax.rounding} onChange={(event) => onUpdateTax('rounding', event.target.value)}><option value="切り捨て">切り捨て</option><option value="四捨五入">四捨五入</option></select></label></div></section><section className="panel settings-panel"><div className="settings-section-heading"><FileText size={18} /><div><h2>帳票の初期値</h2><p>新しい販売書類を作成するときに適用します。</p></div></div><div className="settings-form-grid"><label className="form-field"><span>支払期限の初期日数</span><div className="settings-number-input"><input type="number" min="0" max="365" value={settings.document.defaultDueDays} onChange={(event) => onUpdateDocument('defaultDueDays', Number(event.target.value))} /><span>日後</span></div></label></div></section></div>
}

function MasterSettingsPanel({ settings, onUpdate, onAdd, onRemove }: { settings: AppSettings; onUpdate: (kind: 'salesItemPresets' | 'maintenanceItemPresets', index: number, value: string) => void; onAdd: (kind: 'salesItemPresets' | 'maintenanceItemPresets') => void; onRemove: (kind: 'salesItemPresets' | 'maintenanceItemPresets', index: number) => void }) {
  return <div className="settings-panel-stack"><SettingsPanelHeader icon={Settings2} title="明細候補" description="販売書類・整備書類で選択できる定型項目です。" /><PresetPanel title="販売明細候補" description="車両本体、付属品、諸費用など" items={settings.salesItemPresets} kind="salesItemPresets" onUpdate={onUpdate} onAdd={onAdd} onRemove={onRemove} /><PresetPanel title="整備作業・部品候補" description="作業内容や部品名など" items={settings.maintenanceItemPresets} kind="maintenanceItemPresets" onUpdate={onUpdate} onAdd={onAdd} onRemove={onRemove} /></div>
}

function SettingsPanelHeader({ icon: Icon, title, description }: { icon: typeof Building2; title: string; description: string }) {
  return <div className="settings-panel-heading"><span className="settings-panel-icon"><Icon size={22} /></span><div><span className="page-eyebrow">設定項目</span><h2>{title}</h2><p>{description}</p></div></div>
}

function SettingsField({ label, value, onChange, placeholder, required, wide }: { label: string; value: string; onChange: (value: string) => void; placeholder?: string; required?: boolean; wide?: boolean }) {
  return <label className={`form-field${wide ? ' settings-field-wide' : ''}`}><span>{label}{required && <em>必須</em>}</span><input required={required} value={value} onChange={(event) => onChange(event.target.value)} placeholder={placeholder} /></label>
}

function PresetPanel({ title, description, items, kind, onUpdate, onAdd, onRemove }: { title: string; description: string; items: string[]; kind: 'salesItemPresets' | 'maintenanceItemPresets'; onUpdate: (kind: 'salesItemPresets' | 'maintenanceItemPresets', index: number, value: string) => void; onAdd: (kind: 'salesItemPresets' | 'maintenanceItemPresets') => void; onRemove: (kind: 'salesItemPresets' | 'maintenanceItemPresets', index: number) => void }) {
  return <section className="panel settings-panel"><div className="settings-section-heading"><FileText size={18} /><div><h2>{title}</h2><p>{description}</p></div><button className="button button-secondary settings-add-button" type="button" onClick={() => onAdd(kind)}><Plus size={15} />項目を追加</button></div><div className="settings-preset-list">{items.map((item, index) => <div className="settings-preset-row" key={`${kind}-${index}`}><span className="settings-preset-index">{index + 1}</span><input value={item} onChange={(event) => onUpdate(kind, index, event.target.value)} placeholder="項目名" /><button className="icon-button" type="button" aria-label={`${index + 1}番目の項目を削除`} onClick={() => onRemove(kind, index)}><Trash2 size={16} /></button></div>)}{items.length === 0 && <div className="settings-preset-empty">登録されている項目はありません。右上の「項目を追加」から登録できます。</div>}</div></section>
}
