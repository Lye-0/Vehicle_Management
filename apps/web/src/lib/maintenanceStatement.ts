import type { MaintenanceDocumentLike, MaintenanceLineItem } from './maintenanceApi'
import type { AppSettings } from './settingsApi'

export const maintenanceStatementWidth = 1122
export const maintenanceStatementHeight = 1407

export type MaintenanceStatementTotals = {
  partsSubtotal: number
  technicalSubtotal: number
  taxableSubtotal: number
  tax: number
  workTotal: number
  feesTotal: number
  total: number
}

export type MaintenanceStatementSvgOptions = {
  hideEditableValues?: boolean
}

export function calculateMaintenanceStatementTotals(
  document: MaintenanceDocumentLike,
): MaintenanceStatementTotals {
  const rows = statementRows(document)
  const partsSubtotal = rows.reduce((sum, item) => sum + (item.partAmount ?? (item.quantity ?? 0) * (item.unitPrice ?? 0)), 0)
  const technicalSubtotal = rows.reduce((sum, item) => sum + (item.technicalFee ?? 0), 0)
  const calculatedTaxableSubtotal = Math.max(0, partsSubtotal + technicalSubtotal)
  const taxValue = calculatedTaxableSubtotal * document.taxRate
  const calculatedTax = document.taxRounding === '四捨五入' ? Math.round(taxValue) : Math.floor(taxValue)
  const taxableSubtotal = document.abacusAmounts?.subtotal ?? calculatedTaxableSubtotal
  const tax = document.abacusAmounts?.tax ?? calculatedTax
  const workTotal = taxableSubtotal + tax
  const feesTotal = Object.values(document.fees).reduce((sum, fee) => sum + fee, 0) + document.adjustment
  return { partsSubtotal, technicalSubtotal, taxableSubtotal, tax, workTotal, feesTotal, total: document.abacusAmounts?.total ?? workTotal + feesTotal }
}

export function buildMaintenanceStatementSvg(document: MaintenanceDocumentLike, settings: AppSettings, options: MaintenanceStatementSvgOptions = {}) {
  const hideEditableValues = options.hideEditableValues ?? false
  const totals = calculateMaintenanceStatementTotals(document)
  const customer = document.details.customerOverride ?? document.customerDetails
  const vehicle = document.details.vehicleOverride ?? document.vehicleDetails ?? emptyVehicle
  const labels = document.details.labels
  const documentTitle = defaultDocumentTitle(document.type)
  const amountTitle = document.type === '整備請求書' ? 'ご請求金額（税込）' : 'お見積金額（税込）'
  const workSectionTitle = '作業内容／部品名等'
  const rows = statementRows(document)
  const rowHeight = 28
  const rowTop = 587

  return `<svg xmlns="http://www.w3.org/2000/svg" width="${maintenanceStatementWidth}" height="${maintenanceStatementHeight}" viewBox="0 0 ${maintenanceStatementWidth} ${maintenanceStatementHeight}" role="img" aria-label="${escapeXml(documentTitle)}">
  <defs>
    <linearGradient id="maintenance-blue" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="#063378"/>
      <stop offset="1" stop-color="#0062bd"/>
    </linearGradient>
    <style>
      text { font-family: "Noto Sans JP", "Yu Gothic", "Meiryo", sans-serif; fill: #111827; }
      .blue { fill: #073c87; }
      .white { fill: #fff; }
      .label { font-size: 14px; font-weight: 700; fill: #073c87; }
      .value { font-size: 15px; }
      .small { font-size: 12px; }
      .head { font-size: 14px; font-weight: 700; fill: #073c87; }
      .section { font-size: 23px; font-weight: 700; fill: #fff; letter-spacing: 1px; }
      .line { stroke: #6e91c4; stroke-width: 1; }
      .box { fill: #fff; stroke: #6e91c4; stroke-width: 1.25; }
      .pale { fill: #f4f8fd; }
    </style>
  </defs>
  <rect width="1122" height="1407" fill="#fff"/>

  ${roundedBox(16, 12, 506, 55)}
  ${brandMark(32, 26)}
  ${brandMark(485, 26, true)}
  ${centerText(269, 47, documentTitle, 25, '700', '#073c87')}

  ${roundedBox(611, 12, 472, 65)}
  ${gridLines([611, 729, 847, 965, 1083], [12, 43, 77])}
  ${headerText(670, 34, '日付')}${headerText(788, 34, '担当')}${headerText(906, 34, '書類番号')}${headerText(1024, 34, 'ページ')}
  ${valueText(670, 65, statementValue(dateSlash(document.issuedAt), hideEditableValues), 'middle', 14)}
  ${valueText(788, 65, statementValue(document.details.staffName, hideEditableValues), 'middle', 14)}
  ${valueText(906, 65, statementValue(document.number, hideEditableValues), 'middle', 14)}
  ${valueText(1024, 65, '1/1', 'middle')}

  ${roundedBox(16, 90, 794, 168)}
  ${rect(17, 91, 107, 166, '#dcecff')}${rect(536, 91, 108, 166, '#dcecff')}
  ${line(125, 90, 125, 257)}${line(535, 90, 535, 257)}${line(645, 90, 645, 257)}
  ${line(16, 174, 810, 174)}${line(535, 132, 810, 132)}${line(535, 216, 810, 216)}
  ${headerText(70, 137, 'お名前')}${headerText(70, 224, 'ご住所')}
  ${valueText(145, 127, statementValue(customer.name, hideEditableValues), 'start', 24)}
  ${valueText(145, 150, statementValue(customer.kana, hideEditableValues), 'start', 11)}
  ${valueText(504, 143, statementValue(document.details.customerHonorific, hideEditableValues), 'end', 22)}
  ${valueText(145, 205, statementValue(customer.postalCode ? `〒${customer.postalCode}` : '', hideEditableValues), 'start')}
  ${valueText(145, 233, statementValue(customer.address, hideEditableValues), 'start')}
  ${headerText(590, 115, '生年月日')}${headerText(590, 157, '電話番号')}${headerText(590, 200, '勤務先等')}${headerText(590, 242, '連絡先TEL')}
  ${valueText(665, 119, statementValue(dateSlash(document.details.customerBirthDate), hideEditableValues), 'start', 14)}
  ${valueText(665, 158, statementValue(customer.phone, hideEditableValues), 'start', 14)}
  ${valueText(665, 200, statementValue(document.details.customerEmployer, hideEditableValues), 'start', 14)}
  ${valueText(665, 242, statementValue(document.details.customerContactPhone, hideEditableValues), 'start', 14)}

  ${roundedBox(832, 90, 250, 255)}
  ${rect(832, 90, 250, 43, 'url(#maintenance-blue)')}
  ${centerText(957, 119, amountTitle, 18, '700', '#fff')}
  ${centerText(957, 184, `${number(totals.total)}円`, 39, '800', '#073c87')}
  ${line(847, 206, 1068, 206, '#c2cfdf')}
  ${valueText(847, 239, '課税対象額（作業料金）', 'start', 13, '700')}
  ${valueText(1068, 239, `${number(totals.taxableSubtotal)}円`, 'end', 13)}
  ${valueText(847, 272, `消費税（${Math.round(document.taxRate * 100)}％）`, 'start', 13, '700')}
  ${valueText(1068, 272, `${number(totals.tax)}円`, 'end', 13)}
  ${valueText(847, 305, '非課税対象額（諸費用計）', 'start', 13, '700')}
  ${valueText(1068, 305, `${number(totals.feesTotal)}円`, 'end', 13)}

  ${roundedBox(16, 288, 794, 207)}
  ${rect(16, 288, 794, 38, 'url(#maintenance-blue)')}
  ${centerText(413, 315, '車両情報', 22, '700', '#fff')}
  ${vehicleGrid(vehicle, hideEditableValues)}

  ${roundedBox(832, 361, 250, 121)}
  ${rect(832, 361, 250, 40, 'url(#maintenance-blue)')}
  ${centerText(957, 389, '入庫／出庫', 18, '700', '#fff')}
  ${rect(833, 402, 248, 39, '#dcecff')}
  ${gridLines([832, 916, 999, 1082], [401, 442, 482])}
  ${headerText(874, 429, '入庫区分')}${headerText(957, 429, '入庫日')}${headerText(1040, 429, '出庫日')}
  ${valueText(874, 469, document.category, 'middle', 12, '700')}
  ${valueText(957, 469, statementValue(dateSlash(document.intakeDate), hideEditableValues), 'middle', 12)}
  ${valueText(1040, 469, statementValue(dateSlash(document.plannedReleaseDate || document.completionDate), hideEditableValues), 'middle', 12)}

  ${roundedBox(16, 509, 1067, 620)}
  ${rect(16, 509, 1067, 42, 'url(#maintenance-blue)')}
  ${centerText(550, 538, workSectionTitle, 24, '700', '#fff')}
  ${rect(17, 552, 1065, 34, '#dcecff')}
  ${workGridLines(rowTop, rowHeight)}
  ${headerText(45, 575, 'No.')}${headerText(233, 575, '作業内容／部品名等')}${headerText(432, 575, '数量')}${headerText(514, 575, '単位')}${headerText(612, 575, '部品単価')}${headerText(727, 575, '部品金額')}${headerText(868, 575, '技術料／他')}${headerText(1017, 575, '摘要')}
  ${rows.map((item, index) => workRow(item, index, rowTop + rowHeight * index, hideEditableValues)).join('')}
  ${rect(557, 1092, 111, 36, '#dcecff')}
  ${line(16, 1091, 1083, 1091)}
  ${line(556, 1091, 556, 1129)}${line(669, 1091, 669, 1129)}${line(785, 1091, 785, 1129)}${line(951, 1091, 951, 1129)}
  ${headerText(612, 1117, '小計金額')}
  ${valueText(727, 1117, number(totals.partsSubtotal), 'middle', 14, '700')}
  ${valueText(868, 1117, number(totals.technicalSubtotal), 'middle', 14)}

  ${summaryBoxes(document, totals, labels.otherFee, hideEditableValues)}
  ${document.type === '整備請求書' ? bankBox(settings) : ''}
  ${shopBox(settings)}
  </svg>`
}

function vehicleGrid(vehicle: NonNullable<MaintenanceDocumentLike['vehicleDetails']>, hideEditableValues: boolean) {
  const topX = [16, 115, 351, 455, 568, 683, 810]
  const bottomX = [16, 140, 350, 530, 670, 810]
  return `${rect(17, 327, 792, 38, '#dcecff')}${rect(17, 407, 792, 38, '#dcecff')}${gridLines(topX, [326, 366, 406])}${gridLines(bottomX, [406, 446, 495])}
  ${headerText(65, 354, 'メーカー')}${headerText(233, 354, '車名・仕様')}${headerText(403, 354, '年式')}${headerText(512, 354, '排気量')}${headerText(625, 354, 'ミッション')}${headerText(746, 354, '車体色')}
  ${valueText(65, 394, statementValue(vehicle.maker, hideEditableValues), 'middle', 14)}${valueText(233, 394, statementValue(vehicle.name, hideEditableValues), 'middle', 14)}${valueText(403, 394, statementValue(vehicle.year, hideEditableValues), 'middle', 14)}${valueText(512, 394, statementValue(suffix(vehicle.displacement, 'cc'), hideEditableValues), 'middle', 14)}${valueText(625, 394, statementValue(vehicle.transmission, hideEditableValues), 'middle', 14)}${valueText(746, 394, statementValue(vehicle.color, hideEditableValues), 'middle', 14)}
  ${headerText(78, 434, '型式')}${headerText(245, 434, '車台番号')}${headerText(440, 434, '登録番号')}${headerText(600, 434, '走行距離')}${headerText(740, 434, '車検日')}
  ${valueText(78, 479, statementValue(vehicle.modelType, hideEditableValues), 'middle', 14)}${valueText(245, 479, statementValue(vehicle.vin, hideEditableValues), 'middle', 14)}${valueText(440, 479, statementValue(vehicle.plate, hideEditableValues), 'middle', 14)}${valueText(600, 479, statementValue(vehicle.mileage, hideEditableValues), 'middle', 14)}${valueText(740, 479, statementValue(dateSlash(vehicle.inspectionDate), hideEditableValues), 'middle', 14)}`
}

function workGridLines(rowTop: number, rowHeight: number) {
  const tableBottom = rowTop + rowHeight * 18
  const vertical = [16, 74, 392, 472, 556, 669, 785, 951, 1083].map((x) => line(x, 551, x, tableBottom)).join('')
  const horizontal = Array.from({ length: 19 }, (_, index) => line(16, rowTop + rowHeight * index, 1083, rowTop + rowHeight * index)).join('')
  return vertical + horizontal
}

function workRow(item: StatementRow, index: number, y: number, hideEditableValues: boolean) {
  const baseline = y + 20
  return `${valueText(45, baseline, String(index + 1), 'middle', 13)}
  ${valueText(89, baseline, statementValue(item.description, hideEditableValues), 'start', 14)}
  ${valueText(432, baseline, statementValue(displayQuantity(item), hideEditableValues), 'middle', 13)}
  ${valueText(514, baseline, statementValue(item.unit, hideEditableValues), 'middle', 13)}
  ${valueText(654, baseline, statementValue(item.unitPrice ? number(item.unitPrice) : '', hideEditableValues), 'end', 13)}
  ${valueText(770, baseline, item.partAmount ? number(item.partAmount) : '', 'end', 13)}
  ${valueText(936, baseline, statementValue(item.technicalFee ? number(item.technicalFee) : '', hideEditableValues), 'end', 13, '400', '#111827')}
  ${valueText(966, baseline, statementValue(item.summary, hideEditableValues), 'start', 12)}`
}

function summaryBoxes(document: MaintenanceDocumentLike, totals: MaintenanceStatementTotals, otherFeeLabel: string, hideEditableValues: boolean) {
  const fee = document.fees
  return `${roundedBox(16, 1144, 300, 75)}
  ${rect(17, 1145, 298, 36, '#dcecff')}
  ${rect(216, 1145, 99, 73, '#dcecff')}
  ${gridLines([16, 116, 216, 316], [1144, 1182, 1219])}
  ${headerText(66, 1170, '作業料金')}${headerText(166, 1170, `消費税(${Math.round(document.taxRate * 100)}%)`)}${headerText(266, 1170, '作業料金＋税')}
  ${valueText(66, 1207, number(totals.taxableSubtotal), 'middle', 16)}${valueText(166, 1207, number(totals.tax), 'middle', 16, '700')}${valueText(266, 1207, number(totals.workTotal), 'middle', 17, '800', '#073c87')}
  ${roundedBox(335, 1144, 523, 75)}
  ${rect(336, 1145, 521, 36, '#dcecff')}
  ${rect(770, 1145, 87, 73, '#dcecff')}
  ${gridLines([335, 422, 509, 596, 683, 770, 858], [1144, 1182, 1219])}
  ${headerText(378.5, 1170, '自賠責')}${headerText(465.5, 1170, '重量税')}${headerText(552.5, 1170, '印紙代')}${headerText(639.5, 1170, otherFeeLabel)}${headerText(726.5, 1170, '端数値引')}${headerText(814, 1170, '諸費用計')}
  ${valueText(378.5, 1207, statementValue(number(fee.自賠責), hideEditableValues), 'middle', 13)}${valueText(465.5, 1207, statementValue(number(fee.重量税), hideEditableValues), 'middle', 13)}${valueText(552.5, 1207, statementValue(number(fee.印紙代), hideEditableValues), 'middle', 13)}${valueText(639.5, 1207, statementValue(number(fee.リサイクル料金), hideEditableValues), 'middle', 13)}${valueText(726.5, 1207, statementValue(number(document.adjustment), hideEditableValues), 'middle', 13)}${valueText(814, 1207, number(totals.feesTotal), 'middle', 14, '800', '#073c87')}
  ${roundedBox(882, 1144, 200, 75, '#fff7b0')}
  ${line(882, 1182, 1082, 1182)}
  ${headerText(982, 1170, '作業料金＋税＋諸費用計')}
  ${centerText(982, 1209, number(totals.total), 18, '800', '#073c87')}`
}

function bankBox(settings: AppSettings) {
  return `${roundedBox(16, 1263, 510, 114)}
  ${rect(16, 1263, 510, 42, 'url(#maintenance-blue)')}
  ${centerText(271, 1292, 'お振込先', 20, '700', '#fff')}
  ${rect(17, 1306, 107, 70, '#dcecff')}
  ${gridLines([16, 124, 526], [1305, 1340, 1377])}
  ${headerText(70, 1329, '振込口座')}${headerText(70, 1365, '口座名義')}
  ${valueText(145, 1329, settings.shop.bankName, 'start', 18)}
  ${valueText(145, 1365, settings.shop.bankAccount, 'start', 18)}`
}

function shopBox(settings: AppSettings) {
  const shop = settings.shop
  const hasLogo = Boolean(shop.logoDataUrl)
  const companyX = hasLogo ? 830 : 780
  const infoX = companyX + 20
  const infoLines = [
    shop.registrationNumber ? `インボイス番号 ${shop.registrationNumber}` : '',
    shop.postalCode ? `〒 ${shop.postalCode}` : '',
    shop.address,
    shop.phone ? `TEL ${shop.phone}` : '',
    shop.fax ? `FAX ${shop.fax}` : '',
  ].filter(Boolean)
  const logoMarkup = hasLogo
    ? `<image href="${escapeXml(shop.logoDataUrl)}" x="578" y="1275" width="220" height="90" preserveAspectRatio="xMidYMid meet"/>`
    : valueText(635, 1365, '▦', 'start', 110, '700', '#073c87')
  return `${logoMarkup}
  ${valueText(companyX, 1269, shop.name, 'start', 20, '700', '#073c87')}
  ${infoLines.map((line, index) => valueText(infoX, 1292 + index * 19, line, 'start', 12)).join('')}`
}

type StatementRow = Omit<MaintenanceLineItem, 'quantity' | 'unit' | 'unitPrice' | 'technicalFee' | 'summary'> & {
  quantity: number | null
  unit: string | null
  unitPrice: number | null
  technicalFee: number | null
  summary: string | null
  partAmount: number | null
}

function statementRows(document: MaintenanceDocumentLike): StatementRow[] {
  const rows = document.items.map((item) => {
    const detail = item.abacusDetail
    const quantity = detail ? detail.quantity : item.quantity
    const unit = detail ? detail.unit : item.unit
    const unitPrice = detail ? detail.unitPrice : item.unitPrice
    const technicalFee = detail ? detail.technicalFees : item.technicalFee
    const summary = detail ? detail.summary : item.summary
    const partAmount = detail ? detail.partAmount : Math.round((item.quantity ?? 0) * (item.unitPrice ?? 0))
    return { ...item, description: detail ? detail.description ?? '' : item.description, quantity, unit, unitPrice, technicalFee, summary, partAmount }
  })
  return rows.slice(0, 18)
}

const emptyVehicle: NonNullable<MaintenanceDocumentLike['vehicleDetails']> = {
  maker: '', name: '', modelType: '', plate: '', vin: '', year: '', inspectionDate: '', mileage: '', color: '', displacement: '', transmission: '', inspectionRecordAvailable: false,
}

function defaultDocumentTitle(type: MaintenanceDocumentLike['type']) {
  if (type === '整備見積書') return '見積書'
  return '請求書'
}

function dateSlash(value: string) {
  return value ? value.slice(0, 10).replaceAll('.', '/').replaceAll('-', '/') : ''
}

function suffix(value: string, unit: string) {
  return value && !value.toLocaleLowerCase().endsWith(unit.toLocaleLowerCase()) ? `${value}${unit}` : value
}

function displayQuantity(item: StatementRow) {
  return item.description || item.unitPrice !== null || item.partAmount !== null || item.technicalFee !== null ? (item.quantity === null ? '' : String(item.quantity)) : ''
}

function statementValue(value: string | number | null | undefined, hideEditableValues: boolean) {
  return hideEditableValues ? '' : value ?? ''
}

function number(value: number) {
  return new Intl.NumberFormat('ja-JP').format(Math.round(value))
}

function escapeXml(value: string | number) {
  return String(value).replace(/[&<>"']/g, (character) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&apos;' })[character]!)
}

function rect(x: number, y: number, width: number, height: number, fill = '#fff') {
  return `<rect x="${x}" y="${y}" width="${width}" height="${height}" fill="${fill}"/>`
}

function roundedBox(x: number, y: number, width: number, height: number, fill = '#fff') {
  return `<rect x="${x}" y="${y}" width="${width}" height="${height}" rx="6" fill="${fill}" stroke="#6e91c4" stroke-width="1.25"/>`
}

function line(x1: number, y1: number, x2: number, y2: number, stroke = '#6e91c4') {
  return `<line x1="${x1}" y1="${y1}" x2="${x2}" y2="${y2}" stroke="${stroke}" stroke-width="1"/>`
}

function gridLines(xs: number[], ys: number[]) {
  return xs.map((x) => line(x, ys[0], x, ys[ys.length - 1])).join('') + ys.map((y) => line(xs[0], y, xs[xs.length - 1], y)).join('')
}

function valueText(x: number, y: number, value: string | number, anchor: 'start' | 'middle' | 'end' = 'start', size = 15, weight = '400', fill = '#111827') {
  return `<text x="${x}" y="${y}" text-anchor="${anchor}" font-size="${size}" font-weight="${weight}" style="fill:${fill}">${escapeXml(value)}</text>`
}

function headerText(x: number, y: number, value: string) {
  return valueText(x, y, value, 'middle', 14, '700', '#073c87')
}

function centerText(x: number, y: number, value: string, size: number, weight: string, fill: string) {
  return valueText(x, y, value, 'middle', size, weight, fill)
}

function brandMark(x: number, y: number, reversed = false) {
  if (reversed) {
    return `<g fill="#073c87">
      <rect x="${x + 9}" y="${y}" width="9" height="9"/>
      <rect x="${x}" y="${y + 9}" width="9" height="9"/>
      <rect x="${x + 9}" y="${y + 18}" width="9" height="9"/>
    </g>`
  }

  return `<g fill="#073c87">
    <rect x="${x}" y="${y}" width="9" height="9"/>
    <rect x="${x + 9}" y="${y + 9}" width="9" height="9"/>
    <rect x="${x}" y="${y + 18}" width="9" height="9"/>
  </g>`
}
