import type { SalesDocument, SalesDocumentDetails, SalesDocumentLike, SalesLineItem, SalesTaxCategory } from './salesApi'

export function salesDocumentTitle(type: SalesDocument['type']) {
  return type === '請求書' ? '請求書' : 'お見積書'
}

export function salesDocumentAmountTitle(type: SalesDocument['type']) {
  return type === '請求書' ? '請求金額（税込み）' : 'お見積金額（税込）'
}

export type SalesEstimateLine = {
  id: string
  label: string
  amount: number
  itemType: string
  taxCategory: SalesTaxCategory
}

export type SalesEstimateSections = {
  vehicleBase: SalesEstimateLine[]
  discounts: SalesEstimateLine[]
  accessories: SalesEstimateLine[]
  vehicleSideLabor: SalesEstimateLine[]
  legalNonTaxable: SalesEstimateLine[]
  taxableFees: SalesEstimateLine[]
  nonTaxableFees: SalesEstimateLine[]
  outOfScopeFees: SalesEstimateLine[]
  tradeIns: SalesEstimateLine[]
  payments: SalesEstimateLine[]
}

export type SalesEstimateEditableBucket =
  | 'vehicleBase'
  | 'discounts'
  | 'accessories'
  | 'vehicleSideLabor'
  | 'legalNonTaxable'
  | 'taxableFees'
  | 'nonTaxableFees'
  | 'tradeIns'

export type SalesTotals = {
  subtotal: number
  lineItemsSubtotal: number
  vehicleBasePrice: number
  discount: number
  vehicleTaxableAmount: number
  accessoryTotal: number
  vehicleSideLabor: number
  vehicleSalesTotal: number
  legalNonTaxable: number
  taxableFeeTotal: number
  nonTaxableFeeTotal: number
  feesTotal: number
  taxableSubtotal: number
  nonTaxableSubtotal: number
  outOfScopeSubtotal: number
  recycleFee: number
  tradeInPrice: number
  downPayment: number
  tax: number
  total: number
  remainingPayment: number
}

type SalesItemBucket = keyof SalesEstimateSections

const legalFeeKeywords = ['自動車税', '取得税', '環境性能割', '重量税', '自賠責', '印紙代']
const nonTaxableFeeKeywords = ['証紙', '預託金']
const taxableFeeKeywords = ['車庫証明', '登録費用', '登録代行', '登録手続', '検査', '納車', '手数料', '査定料']

export function calculateSalesLineAmount(item: Pick<SalesLineItem, 'quantity' | 'unitPrice' | 'otherAmount' | 'abacusDetail'>) {
  const imported = item.abacusDetail
  const quantity = imported ? imported.quantity ?? 0 : Number.isFinite(item.quantity) ? item.quantity : 0
  const unitPrice = imported ? imported.unitPrice ?? 0 : Number.isFinite(item.unitPrice) ? item.unitPrice : 0
  const otherAmount = imported ? (imported.partAmount ?? 0) + (imported.technicalFees ?? 0) - Math.round(quantity * unitPrice) : Number.isFinite(item.otherAmount) ? item.otherAmount : 0
  return Math.round(quantity * unitPrice) + Math.round(otherAmount)
}

export function calculateSalesEstimateTotals(document: SalesDocumentLike): SalesTotals {
  const sections = buildSalesEstimateSections(document)
  const sum = (lines: SalesEstimateLine[]) => lines.reduce((total, line) => total + line.amount, 0)
  const lineItemsSubtotal = document.items.reduce((total, item) => total + calculateSalesLineAmount(item), 0)
  const vehicleBasePrice = sum(sections.vehicleBase)
  const discount = sum(sections.discounts)
  const vehicleTaxableAmount = vehicleBasePrice + discount
  const accessoryTotal = sum(sections.accessories)
  const vehicleSideLabor = sum(sections.vehicleSideLabor)
  const vehicleSalesTotal = vehicleTaxableAmount + accessoryTotal + vehicleSideLabor
  const legalNonTaxable = sum(sections.legalNonTaxable)
  const taxableFeeTotal = sum(sections.taxableFees)
  const nonTaxableFeeTotal = sum(sections.nonTaxableFees)
  const outOfScopeSubtotal = sum(sections.outOfScopeFees)
  const taxableSubtotal = vehicleSalesTotal + taxableFeeTotal
  const nonTaxableSubtotal = legalNonTaxable + nonTaxableFeeTotal
  const subtotal = taxableSubtotal + nonTaxableSubtotal + outOfScopeSubtotal
  // SalesDocument.taxRate is kept as a decimal in the web layer (0.1 = 10%).
  const taxValue = Math.max(0, taxableSubtotal) * document.taxRate
  const tax = document.taxRounding === '四捨五入' ? Math.round(taxValue) : Math.floor(taxValue)
  const total = subtotal + tax
  const tradeInPrice = sum(sections.tradeIns)
  const downPayment = Math.max(0, Number.isFinite(document.details.downPayment) ? document.details.downPayment : 0)

  const originalAmounts = document.abacusAmounts
  // ABACUSの小計は税抜、明細行の金額は帳票表示どおり税込で保持しています。
  // 移行書類だけは、非課税行を小計から除いた値を課税対象額として表示します。
  const displayTaxableSubtotal = originalAmounts
    ? Math.max(0, originalAmounts.subtotal - nonTaxableSubtotal - outOfScopeSubtotal)
    : taxableSubtotal
  return {
    subtotal: originalAmounts?.subtotal ?? subtotal,
    lineItemsSubtotal,
    vehicleBasePrice,
    discount,
    vehicleTaxableAmount,
    accessoryTotal,
    vehicleSideLabor,
    vehicleSalesTotal,
    legalNonTaxable,
    taxableFeeTotal,
    nonTaxableFeeTotal,
    feesTotal: legalNonTaxable + taxableFeeTotal + nonTaxableFeeTotal + outOfScopeSubtotal,
    taxableSubtotal: displayTaxableSubtotal,
    nonTaxableSubtotal,
    outOfScopeSubtotal,
    recycleFee: findRecycleFee(sections),
    tradeInPrice,
    downPayment,
    tax: originalAmounts?.tax ?? tax,
    total: originalAmounts?.total ?? total,
    remainingPayment: (originalAmounts?.total ?? total) - tradeInPrice - downPayment,
  }
}

export function buildSalesEstimateSections(document: SalesDocumentLike): SalesEstimateSections {
  const sections: SalesEstimateSections = {
    vehicleBase: [],
    discounts: [],
    accessories: [],
    vehicleSideLabor: [],
    legalNonTaxable: [],
    taxableFees: [],
    nonTaxableFees: [],
    outOfScopeFees: [],
    tradeIns: [],
    payments: [],
  }
  let hasRecycleLine = false

  document.items.forEach((item) => {
    const amount = calculateSalesLineAmount(item)
    const line: SalesEstimateLine = { id: item.id, label: salesLineLabel(item), amount, itemType: item.itemType, taxCategory: item.taxCategory }
    const bucket = classifySalesItem(item)
    if (bucket === 'nonTaxableFees' && isRecycleItem(item)) hasRecycleLine = true
    sections[bucket].push(line)
  })

  if (!hasRecycleLine && document.details.recycleFee > 0) {
    sections.nonTaxableFees.push({ id: 'recycle-fee', label: 'リサイクル預託金', amount: Math.round(document.details.recycleFee), itemType: 'リサイクル料金', taxCategory: '非課税' })
  }

  return sections
}

export function salesLineLabel(item: Pick<SalesLineItem, 'itemType' | 'description' | 'abacusDetail'>) {
  return item.abacusDetail?.description?.trim() || item.description.trim() || item.itemType.trim() || 'その他'
}

export function emptySalesDocumentDetails(): SalesDocumentDetails {
  return {
    salesCategory: '中古車',
    staffName: '',
    customerHonorific: '様',
    customerBirthDate: '',
    customerEmployer: '',
    customerContactPhone: '',
    selectedImageAttachmentId: '',
    customerOverride: null,
    vehicleOverride: null,
    tradeIn: { name: '', modelYear: '', inspectionDate: '', mileage: '', color: '' },
    recycleFee: 0,
    downPayment: 0,
    remainingPayment: 0,
    credit: { enabled: false, paymentCount: '', fee: 0, monthlyPayment: 0, initialPayment: 0, bonusMonths: '', bonusPayment: 0 },
    requiredDocuments: { sealCertificate: false, selfDeclaration: false, residentCard: false, powerOfAttorney: false, lightVehicleCertificate: false, transferCertificate: false, taxPaymentCertificate: false, guarantorSealCertificate: false, warrantyCertificate: false, other: '' },
  }
}

function classifySalesItem(item: SalesLineItem): SalesItemBucket {
  const itemType = item.itemType.trim()
  const description = item.abacusDetail?.description ?? item.description

  // itemType での分類を優先
  if (itemType === '法定費用') return 'legalNonTaxable'
  if (itemType === '手続代行費用') return 'taxableFees'
  if (itemType === '実費・預託金') return 'nonTaxableFees'
  if (itemType === '車両本体価格') return 'vehicleBase'
  if (itemType === '値引き') return 'discounts'
  if (itemType === '付属品・特別仕様' || itemType === '取付工賃') return 'accessories'
  if (itemType === '車両販売工賃') return 'vehicleSideLabor'
  if (itemType === '下取車') return 'tradeIns'
  if (itemType === '頭金' || itemType === '残金') return 'payments'

  // itemType で分類できない場合のみ、description で分類
  const label = `${itemType} ${description}`
  if (isRecycleItem(item)) return 'nonTaxableFees'
  if (label.includes('車両本体価格')) return 'vehicleBase'
  if (label.includes('値引')) return 'discounts'
  if (label.includes('付属品') || label.includes('特別仕様')) return 'accessories'
  if (label.includes('車両販売側工賃')) return 'vehicleSideLabor'
  if (label.includes('下取')) return 'tradeIns'
  if (label.includes('頭金') || label.includes('残金')) return 'payments'
  if (legalFeeKeywords.some((keyword) => label.includes(keyword))) return 'legalNonTaxable'
  if (nonTaxableFeeKeywords.some((keyword) => label.includes(keyword))) return 'nonTaxableFees'
  if (taxableFeeKeywords.some((keyword) => label.includes(keyword))) return 'taxableFees'
  if (item.taxCategory === '非課税') return 'nonTaxableFees'
  if (item.taxCategory === '対象外') return 'outOfScopeFees'
  return 'taxableFees'
}

function isRecycleItem(item: Pick<SalesLineItem, 'itemType' | 'description' | 'abacusDetail'>) {
  return `${item.itemType} ${item.abacusDetail?.description ?? item.description}`.includes('リサイクル')
}

function findRecycleFee(sections: SalesEstimateSections) {
  return sections.nonTaxableFees.filter((line) => line.label.includes('リサイクル') || line.itemType.includes('リサイクル')).reduce((total, line) => total + line.amount, 0)
}
