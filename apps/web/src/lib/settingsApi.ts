import { apiFetch } from './api'

export type ShopSettings = {
  name: string
  postalCode: string
  address: string
  phone: string
  fax: string
  representative: string
  registrationNumber: string
  bankName: string
  bankAccount: string
  logoDataUrl: string
}

export type DocumentSettings = {
  defaultDueDays: number
  footerNote: string
  paymentNote: string
}

export type TaxSettings = {
  consumptionTaxRate: number
  display: '税込' | '税別'
  rounding: '切り捨て' | '四捨五入'
}

export type SalesItemPresetGroupKey = 'vehiclePrice' | 'fees' | 'accessories'

export type SalesItemPresetGroups = Record<SalesItemPresetGroupKey, string[]>

export type AppSettings = {
  shop: ShopSettings
  document: DocumentSettings
  tax: TaxSettings
  salesItemPresets: string[]
  salesItemPresetGroups: SalesItemPresetGroups
  maintenanceItemPresets: string[]
}

export const defaultSalesItemPresetGroups: SalesItemPresetGroups = {
  vehiclePrice: ['車両本体価格', '値引等', '工賃'],
  fees: ['登録代行費用', '納車費用', 'リサイクル料金', 'その他'],
  accessories: ['付属品・特別仕様', 'フロアマット', 'シートカバー', '純正SDナビ', '取付工賃'],
}

export function flattenSalesItemPresetGroups(groups: SalesItemPresetGroups) {
  return Array.from(new Set([...groups.vehiclePrice, ...groups.fees, ...groups.accessories].filter(Boolean)))
}

export const defaultSettings: AppSettings = {
  shop: { name: '', postalCode: '', address: '', phone: '', fax: '', representative: '', registrationNumber: '', bankName: '', bankAccount: '', logoDataUrl: '' },
  document: { defaultDueDays: 14, footerNote: '', paymentNote: '' },
  tax: { consumptionTaxRate: 10, display: '税込', rounding: '切り捨て' },
  salesItemPresets: flattenSalesItemPresetGroups(defaultSalesItemPresetGroups),
  salesItemPresetGroups: defaultSalesItemPresetGroups,
  maintenanceItemPresets: ['法定24か月点検', 'エンジンオイル交換', 'オイルフィルター交換', 'ブレーキ点検', 'タイヤ交換'],
}

export async function fetchSettings() {
  const response = await apiFetch<{ settings: AppSettings }>('/api/settings')
  return response.settings
}

export async function updateSettings(settings: AppSettings) {
  const response = await apiFetch<{ settings: AppSettings }>('/api/settings', { method: 'PATCH', body: JSON.stringify({ settings }) })
  return response.settings
}
