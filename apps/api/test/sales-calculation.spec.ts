import { describe, expect, it } from 'vitest'
import { calculateSalesTotals, parseSalesDetails } from '../src/routes/sales-routes'

const details = {
  salesCategory: '中古車',
  staffName: '佐藤',
  customerHonorific: '様',
  customerBirthDate: '1990-10-10',
  customerEmployer: '松上電機（株）',
  customerContactPhone: '090-1234-5678',
  selectedImageAttachmentId: '',
  customerOverride: null,
  vehicleOverride: null,
  tradeIn: { name: 'なし', modelYear: '', inspectionDate: '', mileage: '', color: '' },
  recycleFee: 10800,
  downPayment: 100000,
  remainingPayment: 0,
  credit: { enabled: false, paymentCount: '', fee: 0, monthlyPayment: 0, initialPayment: 0, bonusMonths: '', bonusPayment: 0 },
  requiredDocuments: { sealCertificate: true, selfDeclaration: true, residentCard: true, powerOfAttorney: true, lightVehicleCertificate: false, transferCertificate: true, taxPaymentCertificate: false, guarantorSealCertificate: false, warrantyCertificate: true, other: '' },
}

const line = (itemType: string, description: string, amount: number, taxCategory = '課税') => ({
  itemType,
  description,
  quantity: 1,
  unit: '式',
  unitPrice: amount,
  taxCategory,
  otherAmount: 0,
  summary: '',
  amount,
})

describe('sales estimate calculation', () => {
  it('keeps document-specific customer, vehicle, and required-document overrides', () => {
    const parsed = parseSalesDetails({
      customerOverride: { name: '手入力 顧客', kana: 'てにゅう こきゃく', phone: '090-0000-0000', postalCode: '100-0001', address: '東京都' },
      vehicleOverride: { maker: '手入力メーカー', name: '手入力車', modelType: 'ABC-1', plate: '品川 100 あ 1', vin: 'VIN-1', year: '2026年', inspectionDate: '2027/01/02', mileage: '1,000km', color: '白', displacement: '1,500cc', transmission: 'AT', inspectionRecordAvailable: true },
      requiredDocuments: { sealCertificate: true, selfDeclaration: true, powerOfAttorney: true, guarantorSealCertificate: true },
    })

    expect(parsed.customerOverride?.name).toBe('手入力 顧客')
    expect(parsed.vehicleOverride?.inspectionDate).toBe('2027-01-02')
    expect(parsed.vehicleOverride?.inspectionRecordAvailable).toBe(true)
    expect(parsed.requiredDocuments).toMatchObject({ sealCertificate: true, selfDeclaration: true, powerOfAttorney: true, guarantorSealCertificate: true })
  })

  it('matches the reference estimate without double-counting installation labor', () => {
    const totals = calculateSalesTotals([
      line('車両本体価格', '車両本体価格', 1280000),
      line('値引き', '値引等', -80000),
      line('付属品・特別仕様', 'フロアマット', 20000),
      line('付属品・特別仕様', 'シートカバー', 28000),
      line('付属品・特別仕様', '純正SDナビ', 120000),
      line('取付工賃', '取付工賃', 25000),
      line('自賠責保険', '自賠責保険料（24か月）', 17650, '非課税'),
      line('重量税', '自動車重量税', 24600, '非課税'),
      line('その他', '印紙代', 1800, '非課税'),
      line('車庫証明費用', '検査／登録／届出', 18000),
      line('車庫証明費用', '車庫証明手続費用', 12000),
      line('登録費用', '下取車諸手続', 6000),
      line('納車費用', '納車費用', 8000),
      line('その他', '車庫証明証紙代', 2500, '非課税'),
      line('下取車', '下取車価格', 150000, '対象外'),
    ], 10, '切り捨て', details)

    expect(totals).toEqual({ subtotal: 1494350, tax: 143700, total: 1638050 })
  })

  it('keeps freely named preview rows in their selected estimate blocks', () => {
    const totals = calculateSalesTotals([
      line('車両本体価格', '特選車価格', 1000000),
      line('法定費用', '自由入力した法定費用', 30000, '非課税'),
      line('手続代行費用', '自由入力した代行費用', 40000),
      line('実費・預託金', '自由入力した預託金', 5000, '非課税'),
      line('付属品・特別仕様', '自由入力した用品', 20000),
    ], 10, '切り捨て', { ...details, recycleFee: 0 })

    expect(totals).toEqual({ subtotal: 1095000, tax: 106000, total: 1201000 })
  })

  it('applies the selected rounding mode to document tax', () => {
    const items = [line('車両本体価格', '車両本体価格', 100005)]

    const calculationDetails = { ...details, recycleFee: 0 }
    expect(calculateSalesTotals(items, 10, '切り捨て', calculationDetails)).toEqual({ subtotal: 100005, tax: 10000, total: 110005 })
    expect(calculateSalesTotals(items, 10, '四捨五入', calculationDetails)).toEqual({ subtotal: 100005, tax: 10001, total: 110006 })
  })
})
