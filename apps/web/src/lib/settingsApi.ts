import { apiFetch } from './api'

export type ShopSettings = {
  name: string
  postalCode: string
  address: string
  phone: string
  representative: string
  registrationNumber: string
  bankName: string
  bankAccount: string
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

export type AppSettings = {
  shop: ShopSettings
  document: DocumentSettings
  tax: TaxSettings
  salesItemPresets: string[]
  maintenanceItemPresets: string[]
}

export const defaultSettings: AppSettings = {
  shop: { name: '東京都心支店', postalCode: '', address: '', phone: '', representative: '', registrationNumber: '', bankName: '', bankAccount: '' },
  document: { defaultDueDays: 14, footerNote: '', paymentNote: '' },
  tax: { consumptionTaxRate: 10, display: '税込', rounding: '切り捨て' },
  salesItemPresets: ['車両本体価格', '付属品・特別仕様', '登録代行費用', '納車費用', 'リサイクル料金', '値引き'],
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
