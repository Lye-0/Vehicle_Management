import type { SalesDocument } from './salesApi'
import { buildSalesEstimateSections, calculateSalesEstimateTotals, salesDocumentAmountTitle, salesDocumentTitle } from './salesEstimate'
import type { AppSettings } from './settingsApi'

const WIDTH = 1055
const HEIGHT = 1491
const BLUE = '#06377d'
const BLUE_DARK = '#002b68'
const LINE = '#86a5d0'
const PALE = '#eaf3ff'
const YELLOW = '#fff7b0'

export type SalesEstimateSheetOptions = {
  imageHref?: string
}

const UPPER_COMPRESSION = 24
const META_TABLE_Y = 13
const META_ROW_HEIGHT = 24
const META_TABLE_HEIGHT = META_ROW_HEIGHT * 5
const TOP_BLOCK_GAP = 12
const AMOUNT_PANEL_HEIGHT = 318
const REQUIRED_BLOCK_HEIGHT = 162
const REQUIRED_NOTE_GAP = 9
const FEE_CATEGORY_WIDTH = 36
const FEE_DETAIL_WIDTH = 299
const FEE_DETAIL_LABEL_WIDTH = 182
const FEE_ROW_HEIGHT = 26
const FEE_SUBTOTAL_HEIGHT = 30
const FEE_GROUP_GAP = 2
const FEE_TOTAL_GAP = 12
const FEE_TOTAL_HEIGHT = 39
const ACCESSORY_NAME_WIDTH = 210
const ACCESSORY_AMOUNT_WIDTH = 114
const ACCESSORY_ROW_HEIGHT = 34
const ACCESSORY_ROW_COUNT = 14
const ACCESSORY_TOTAL_HEIGHT = 48
const VEHICLE_SECTION_HEADER_HEIGHT = 39
const VEHICLE_PRICE_HEIGHT = 267
const VEHICLE_TAX_HEIGHT = 144
const VEHICLE_PAYMENT_HEIGHT = 95

const feeGroupDefinitions = [
  { bucket: 'legalNonTaxable' as const, title: '税金／保険料（非課税）', rows: 5 },
  { bucket: 'taxableFees' as const, title: '手続代行費用（課税）', rows: 6 },
  { bucket: 'nonTaxableFees' as const, title: '実費・預託金（非課税）', rows: 5 },
]

function createSalesEstimateSheetLayout() {
  const lowerY = 742 - UPPER_COMPRESSION
  const amountPanelY = META_TABLE_Y + META_TABLE_HEIGHT + TOP_BLOCK_GAP
  const requiredY = amountPanelY + AMOUNT_PANEL_HEIGHT + TOP_BLOCK_GAP
  const noteY = requiredY + REQUIRED_BLOCK_HEIGHT + REQUIRED_NOTE_GAP
  const feeX = 363
  const feeDetailX = feeX + FEE_CATEGORY_WIDTH
  const feeBodyY = lowerY + 39
  let cursor = feeBodyY
  const feeGroups = feeGroupDefinitions.map((group, index) => {
    const startY = cursor
    const subtotalY = startY + group.rows * FEE_ROW_HEIGHT
    const endY = subtotalY + FEE_SUBTOTAL_HEIGHT
    cursor = endY + (index === feeGroupDefinitions.length - 1 ? FEE_TOTAL_GAP : FEE_GROUP_GAP)
    return { ...group, startY, subtotalY, endY }
  })
  const feeTotalY = cursor
  const feeBottomY = feeTotalY + FEE_TOTAL_HEIGHT
  const vehicleTopY = lowerY + VEHICLE_SECTION_HEADER_HEIGHT
  const vehiclePaymentY = feeBottomY - VEHICLE_PAYMENT_HEIGHT
  const vehicleSubBlockGap = (vehiclePaymentY - (vehicleTopY + VEHICLE_PRICE_HEIGHT) - VEHICLE_TAX_HEIGHT) / 2
  return {
    upperCompression: UPPER_COMPRESSION,
    metaTableY: META_TABLE_Y,
    metaRowHeight: META_ROW_HEIGHT,
    metaTableHeight: META_TABLE_HEIGHT,
    amountPanelY,
    amountPanelHeight: AMOUNT_PANEL_HEIGHT,
    imageCustomerHeight: 178,
    expandedCustomerHeight: 155,
    imageVehicleY: 290,
    expandedVehicleY: 272,
    imageTradeInY: 533,
    expandedTradeInY: 519,
    taxCaptionY: 668,
    requiredY,
    requiredHeight: REQUIRED_BLOCK_HEIGHT,
    noteY,
    lowerY,
    vehicle: {
      x: 23,
      width: 324,
      topY: vehicleTopY,
      topHeight: VEHICLE_PRICE_HEIGHT,
      taxY: vehicleTopY + VEHICLE_PRICE_HEIGHT + vehicleSubBlockGap,
      taxHeight: VEHICLE_TAX_HEIGHT,
      subBlockGap: vehicleSubBlockGap,
      paymentY: vehiclePaymentY,
      bottomY: feeBottomY,
    },
    fee: {
      x: feeX,
      categoryWidth: FEE_CATEGORY_WIDTH,
      detailX: feeDetailX,
      detailWidth: FEE_DETAIL_WIDTH,
      detailLabelWidth: FEE_DETAIL_LABEL_WIDTH,
      bodyY: feeBodyY,
      bodyHeight: feeBottomY - feeBodyY,
      groups: feeGroups,
      totalY: feeTotalY,
      totalHeight: FEE_TOTAL_HEIGHT,
      totalWidth: FEE_CATEGORY_WIDTH + FEE_DETAIL_WIDTH,
      totalSplitWidth: FEE_CATEGORY_WIDTH + FEE_DETAIL_LABEL_WIDTH,
      bottomY: feeBottomY,
    },
    accessory: {
      x: 713,
      width: ACCESSORY_NAME_WIDTH + ACCESSORY_AMOUNT_WIDTH,
      nameWidth: ACCESSORY_NAME_WIDTH,
      amountWidth: ACCESSORY_AMOUNT_WIDTH,
      bodyY: feeBodyY,
      bodyHeight: feeBottomY - feeBodyY,
      detailY: feeBodyY + 36,
      rowHeight: ACCESSORY_ROW_HEIGHT,
      rowCount: ACCESSORY_ROW_COUNT,
      totalY: feeBottomY - ACCESSORY_TOTAL_HEIGHT,
      totalHeight: ACCESSORY_TOTAL_HEIGHT,
      bottomY: feeBottomY,
    },
    creditY: feeBottomY + 15,
  }
}

export const salesEstimateSheetLayout = createSalesEstimateSheetLayout()

export function buildSalesEstimateSheetSvg(document: SalesDocument, settings: AppSettings, { imageHref = '' }: SalesEstimateSheetOptions = {}) {
  const totals = calculateSalesEstimateTotals(document, settings.tax.rounding)
  const sections = buildSalesEstimateSections(document)
  const vehicle = document.details.vehicleOverride ?? document.vehicleDetails
  const details = document.details
  const withImage = Boolean(imageHref)
  const requiredDocuments = [
    ['印鑑証明', details.requiredDocuments.sealCertificate],
    ['自認書・承諾書', details.requiredDocuments.selfDeclaration],
    ['住民票', details.requiredDocuments.residentCard],
    ['委任状', details.requiredDocuments.powerOfAttorney],
    ['軽自動車住民票', details.requiredDocuments.lightVehicleCertificate],
    ['譲渡証明書', details.requiredDocuments.transferCertificate],
    ['納税証明書', details.requiredDocuments.taxPaymentCertificate],
    ['保証人印鑑証明', details.requiredDocuments.guarantorSealCertificate],
  ] as const
  const legalLines = sections.legalNonTaxable.slice(0, 5)
  const taxableLines = sections.taxableFees.slice(0, 6)
  const actualLines = sections.nonTaxableFees.slice(0, 5)
  const accessories = sections.accessories.slice(0, salesEstimateSheetLayout.accessory.rowCount)

  return `<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 0 ${WIDTH} ${HEIGHT}" role="img" aria-label="${salesDocumentTitle(document.type)}">
  <defs>
    <style>
      text{font-family:"Noto Sans JP","Yu Gothic","Meiryo",sans-serif;fill:#101820}
      .blue{fill:${BLUE}} .white{fill:#fff}.muted{fill:#334a6c}.bold{font-weight:700}.heavy{font-weight:800}
      .label{fill:${BLUE};font-weight:700}.small{font-size:13px}.body{font-size:16px}.value{font-size:17px}
      .amount{font-variant-numeric:tabular-nums}.line{stroke:${LINE};stroke-width:1}.box{fill:#fff;stroke:${LINE};stroke-width:1.3}
      .section{fill:url(#sectionGradient)}.sectionText{fill:#fff;font-size:20px;font-weight:700;letter-spacing:2px}
    </style>
    <linearGradient id="sectionGradient" x1="0" x2="1"><stop stop-color="${BLUE_DARK}"/><stop offset="1" stop-color="#0050a9"/></linearGradient>
    <clipPath id="vehiclePhotoClip"><rect x="392" y="106" width="293" height="${salesEstimateSheetLayout.imageCustomerHeight}" rx="5"/></clipPath>
  </defs>
  <rect width="${WIDTH}" height="${HEIGHT}" fill="#fff"/>
  ${sheetTitle(document)}
  ${withImage ? imageCustomerBlock(document, imageHref) : expandedCustomerBlock(document)}
  ${amountPanel(document, totals)}
  ${vehicleBlock(vehicle, withImage ? salesEstimateSheetLayout.imageVehicleY : salesEstimateSheetLayout.expandedVehicleY)}
  ${tradeInBlock(details.tradeIn, withImage ? salesEstimateSheetLayout.imageTradeInY : salesEstimateSheetLayout.expandedTradeInY)}
  ${taxCaption(document, totals, salesEstimateSheetLayout.taxCaptionY)}
  ${requiredBlock(requiredDocuments)}
  ${noteBlock(document.note)}
  ${vehiclePriceCard(document, sections, totals)}
  ${feeCard(legalLines, taxableLines, actualLines, totals)}
  ${accessoryCard(accessories, totals.accessoryTotal)}
  ${creditBlock(details.credit)}
  ${shopBlock(settings)}
  </svg>`
}

function sheetTitle(document: SalesDocument) {
  const rows: Array<[string, string]> = [
    ['見積番号', document.number],
    ['見積日', formatJapaneseDate(document.issuedAt)],
    ['販売区分', document.details.salesCategory || '未設定'],
    ['担当者', document.details.staffName || '未設定'],
    ['ページ', '1 / 1'],
  ]
  const title = document.type === '見積書' ? 'お見積書' : document.type;
  const lineEnd = Math.round(27 + title.length * 50 + (title.length - 1) * 8);
  return `
  <text x="28" y="73" class="blue heavy" font-size="50" letter-spacing="8">${escapeXml(title)}</text>
  <line x1="27" y1="86" x2="${lineEnd}" y2="86" stroke="${BLUE}" stroke-width="5"/>
  <rect x="731" y="${salesEstimateSheetLayout.metaTableY}" width="306" height="${salesEstimateSheetLayout.metaTableHeight}" rx="4" class="box"/>
  ${rows.map(([label, value], index) => {
    const y = salesEstimateSheetLayout.metaTableY + index * salesEstimateSheetLayout.metaRowHeight
    return `${index ? `<line x1="731" y1="${y}" x2="1037" y2="${y}" class="line"/>` : ''}
      <line x1="844" y1="${y}" x2="844" y2="${y + salesEstimateSheetLayout.metaRowHeight}" class="line"/>
      ${text(744, y + 17, label, 'label', 13)}
      ${text(865, y + 17, value, '', 13)}`
  }).join('')}`
}

function imageCustomerBlock(document: SalesDocument, imageHref: string) {
  const customer = { ...document.customerDetails, ...document.details.customerOverride }
  return `
  <rect x="24" y="108" width="338" height="${salesEstimateSheetLayout.imageCustomerHeight}" rx="5" class="box"/>
  <circle cx="57" cy="144" r="19" fill="${BLUE}"/>
  <circle cx="57" cy="138" r="7" fill="#fff"/><path d="M45 158c1-10 6-14 12-14s11 4 12 14" fill="#fff"/>
  ${text(89, 151, `${customer.name || document.customerName || '未設定'} ${document.details.customerHonorific || '様'}`, 'blue heavy', 24)}
  ${text(43, 190, customer.postalCode ? `〒${customer.postalCode}` : '〒 未登録', 'body')}
  ${text(43, 222, customer.address || '住所未登録', 'body')}
  ${text(43, 259, `TEL：${customer.phone || document.phone || '未登録'}`, 'body')}
  <rect x="392" y="106" width="293" height="${salesEstimateSheetLayout.imageCustomerHeight}" rx="5" fill="#f2f4f7" stroke="${LINE}" stroke-width="1"/>
  <image href="${escapeAttribute(imageHref)}" x="392" y="106" width="293" height="${salesEstimateSheetLayout.imageCustomerHeight}" preserveAspectRatio="xMidYMid slice" clip-path="url(#vehiclePhotoClip)"/>`
}

function expandedCustomerBlock(document: SalesDocument) {
  const customer = { ...document.customerDetails, ...document.details.customerOverride }
  const details = document.details
  const rows: Array<[string, string]> = [
    ['生年月日', details.customerBirthDate || customer.birthDate || '未設定'],
    ['電話番号', customer.phone || document.phone || '未登録'],
    ['勤務先等', details.customerEmployer || customer.employer || '未設定'],
    ['連絡先TEL', details.customerContactPhone || customer.contactPhone || '未設定'],
  ]
  return `
  <rect x="27" y="103" width="650" height="${salesEstimateSheetLayout.expandedCustomerHeight}" rx="5" class="box"/>
  <line x1="377" y1="103" x2="377" y2="${103 + salesEstimateSheetLayout.expandedCustomerHeight}" class="line"/>
  <circle cx="58" cy="139" r="19" fill="${BLUE}"/>
  <circle cx="58" cy="133" r="7" fill="#fff"/><path d="M46 153c1-10 6-14 12-14s11 4 12 14" fill="#fff"/>
  ${text(90, 147, `${customer.name || document.customerName || '未設定'} ${details.customerHonorific || '様'}`, 'blue heavy', 24)}
  ${text(44, 181, customer.postalCode ? `〒${customer.postalCode}` : '〒 未登録', 'body')}
  ${text(44, 211, customer.address || '住所未登録', 'body')}
  ${text(44, 240, `TEL：${customer.phone || document.phone || '未登録'}`, 'body')}
  ${rows.map(([label, value], index) => {
    const rowHeight = salesEstimateSheetLayout.expandedCustomerHeight / rows.length
    const y = 103 + index * rowHeight
    return `${index ? `<line x1="377" y1="${y}" x2="677" y2="${y}" class="line"/>` : ''}
      <rect x="377" y="${y}" width="101" height="${rowHeight}" fill="${PALE}"/>
      <line x1="478" y1="${y}" x2="478" y2="${y + rowHeight}" class="line"/>
      ${text(397, y + rowHeight / 2 + 5, label, 'label', 14)}
      ${text(492, y + rowHeight / 2 + 5, value, '', 14)}`
  }).join('')}`
}

function amountPanel(document: SalesDocument, totals: ReturnType<typeof calculateSalesEstimateTotals>) {
  const y = salesEstimateSheetLayout.amountPanelY
  return `
  <rect x="707" y="${y}" width="330" height="${salesEstimateSheetLayout.amountPanelHeight}" rx="5" class="box"/>
  <path d="M712 ${y}h320a5 5 0 015 5v45H707v-45a5 5 0 015-5z" class="section"/>
  ${text(872, y + 34, salesDocumentAmountTitle(document.type), 'sectionText', 20, 'middle')}
  ${text(872, y + 103, formatYen(totals.total), 'blue heavy amount', 46, 'middle')}
  <line x1="712" y1="${y + 127}" x2="1032" y2="${y + 127}" class="line"/>
  ${amountLine(722, y + 153, `課税対象額（${formatPercent(document.taxRate)}）`, totals.taxableSubtotal)}
  ${amountLine(722, y + 186, `消費税（${formatPercent(document.taxRate)}）`, totals.tax)}
  ${amountLine(722, y + 219, '非課税対象額', totals.nonTaxableSubtotal + totals.outOfScopeSubtotal)}
  <line x1="718" y1="${y + 236}" x2="1026" y2="${y + 236}" stroke="${LINE}" stroke-dasharray="2 2"/>
  ${text(722, y + 269, `支払期限：${formatJapaneseDate(document.dueDate)}`, 'body')}
  ${text(722, y + 301, `状態：${document.status}`, 'body')}`
}

function amountLine(x: number, y: number, label: string, amount: number) {
  return `${text(x, y, label, 'body')}${text(1021, y, formatYen(amount), 'body amount', 16, 'end')}`
}

function vehicleBlock(vehicle: SalesDocument['vehicleDetails'], y: number) {
  const v = vehicle ?? { maker: '', name: '', modelType: '', plate: '', vin: '', year: '', inspectionDate: '', mileage: '', color: '', displacement: '', transmission: '', inspectionRecordAvailable: false }
  const rows: Array<Array<[string, number]>> = [
    [['メーカー', 92], [v.maker || '未設定', 100], ['車名・仕様', 95], [v.name || '未設定', 100], ['年式', 58], [v.year || '未設定', 82], ['排気量', 67], [v.displacement || '未設定', 67]],
    [['ミッション', 92], [v.transmission || '未設定', 100], ['車体色', 95], [v.color || '未設定', 274]],
    [['型式', 92], [v.modelType || '未設定', 277], ['車台番号', 90], [v.vin || '未設定', 202]],
    [['登録番号', 92], [v.plate || '未設定', 277], ['走行距離', 90], [v.mileage || '未設定', 202]],
    [['車検満了日', 92], [formatJapaneseDate(v.inspectionDate) || '未設定', 277], ['記録簿', 90], [v.inspectionRecordAvailable ? 'あり' : 'なし', 202]],
  ]
  return `${sectionHeader(24, y, 661, '▣  車両情報')}
  <rect x="24" y="${y + 39}" width="661" height="188" class="box"/>
  ${rows.map((row, index) => tableRow(24, y + 39 + index * 37.6, row, 37.6)).join('')}`
}

function tradeInBlock(tradeIn: SalesDocument['details']['tradeIn'], y: number) {
  const widths = [180, 105, 118, 137, 121]
  const labels = ['下取車名', '年式', '車検日', '走行距離', '車体色']
  const values = [tradeIn.name || 'なし', tradeIn.modelYear || '-', formatJapaneseDate(tradeIn.inspectionDate) || '-', tradeIn.mileage || '-', tradeIn.color || '-']
  return `${sectionHeader(24, y, 661, '▣  下取車情報')}
  <rect x="24" y="${y + 39}" width="661" height="61" class="box"/>
  ${simpleColumns(24, y + 39, widths, labels, 29, true)}
  ${simpleColumns(24, y + 68, widths, values, 32, false)}`
}

function taxCaption(document: SalesDocument, totals: ReturnType<typeof calculateSalesEstimateTotals>, y: number) {
  return `
  ${text(24, y, `※課税対象額（${formatPercent(document.taxRate)}）${formatYen(totals.taxableSubtotal)}　消費税（${formatPercent(document.taxRate)}）${formatYen(totals.tax)}　非課税対象額 ${formatYen(totals.nonTaxableSubtotal + totals.outOfScopeSubtotal)}`, 'small')}
  ${text(24, y + 27, '（※この見積では、自賠責保険料・重量税・印紙代・証紙代・リサイクル預託金を非課税として表示しています）', 'small')}`
}

function requiredBlock(rows: ReadonlyArray<readonly [string, boolean]>) {
  const y = salesEstimateSheetLayout.requiredY
  return `
  <rect x="707" y="${y}" width="330" height="${salesEstimateSheetLayout.requiredHeight}" rx="5" class="box"/>
  ${text(722, y + 31, '必要書類', 'blue bold', 20)}
  <line x1="707" y1="${y + 39}" x2="1037" y2="${y + 39}" class="line"/>
  ${rows.map(([label, checked], index) => {
    const col = index % 2
    const row = Math.floor(index / 2)
    const x = 725 + col * 156
    const rowY = y + 65 + row * 26
    return `<rect x="${x}" y="${rowY - 13}" width="11" height="11" fill="#fff" stroke="#222"/><text x="${x + 5.5}" y="${rowY - 3.5}" text-anchor="middle" font-size="12">${checked ? '✓' : ''}</text>${text(x + 20, rowY - 3, label, 'small')}`
  }).join('')}`
}

function noteBlock(note: string) {
  const y = salesEstimateSheetLayout.noteY
  return `<rect x="707" y="${y}" width="330" height="65" rx="5" class="box"/>
  ${text(722, y + 28, '備考', 'blue bold', 19)}
  <line x1="707" y1="${y + 36}" x2="1037" y2="${y + 36}" class="line"/>
  ${text(722, y + 57, note || '特になし', 'small')}`
}

function vehiclePriceCard(document: SalesDocument, sections: ReturnType<typeof buildSalesEstimateSections>, totals: ReturnType<typeof calculateSalesEstimateTotals>) {
  const { x, width: w, topY, topHeight, taxY, paymentY, bottomY } = salesEstimateSheetLayout.vehicle
  const y = salesEstimateSheetLayout.lowerY
  const rowH = 35
  const rows = [
    ['車両本体価格', totals.vehicleBasePrice, ''],
    ['値引等', totals.discount, 'discount'],
    ['本体課税対象額', totals.vehicleTaxableAmount, 'bold'],
    ['付属品／特別仕様', totals.accessoryTotal, ''],
    [sections.vehicleSideLabor[0]?.label ?? '', totals.vehicleSideLabor, ''],
  ] as const
  return `${sectionHeader(x, y, w, '車両販売価格内訳')}
  ${rows.map(([label, amount, tone], index) => valueRow(x, topY + index * rowH, w, label, amount, rowH, tone)).join('')}
  ${valueRow(x, y + 214, w, '車両販売合計', totals.vehicleSalesTotal, 46, 'bold pale')}
  ${valueRow(x, y + 260, w, '諸費用合計', totals.feesTotal, 46, 'bold pale')}
  <rect x="${x}" y="${topY}" width="${w}" height="${topHeight}" rx="5" fill="none" stroke="${LINE}" stroke-width="1.3"/>
  ${taxMatrix(document, totals, x, taxY, w)}
  ${valueRow(x, paymentY, w, '下取車価格', totals.tradeInPrice, 31, '')}
  ${valueRow(x, paymentY + 31, w, '頭金／現金／他', totals.downPayment, 31, '')}
  ${valueRow(x, paymentY + 62, w, '残金／所要資金', totals.remainingPayment, 33, 'bold dark')}
  <rect x="${x}" y="${paymentY}" width="${w}" height="${bottomY - paymentY}" rx="5" fill="none" stroke="${LINE}" stroke-width="1.3"/>`
}

function taxMatrix(document: SalesDocument, totals: ReturnType<typeof calculateSalesEstimateTotals>, x: number, y: number, w: number) {
  const c1 = 95
  const c2 = 115
  return `
  <rect x="${x}" y="${y}" width="${w}" height="144" rx="5" fill="#fff"/>
  <rect x="${x + 1}" y="${y + 105}" width="${w - 2}" height="38" rx="4" fill="${YELLOW}"/>
  <line x1="${x + c1}" y1="${y}" x2="${x + c1}" y2="${y + 144}" class="line"/>
  <line x1="${x + c1 + c2}" y1="${y}" x2="${x + c1 + c2}" y2="${y + 105}" class="line"/>
  <line x1="${x}" y1="${y + 35}" x2="${x + w}" y2="${y + 35}" class="line"/>
  <line x1="${x}" y1="${y + 70}" x2="${x + w}" y2="${y + 70}" class="line"/>
  <line x1="${x}" y1="${y + 105}" x2="${x + w}" y2="${y + 105}" class="line"/>
  <rect x="${x}" y="${y}" width="${w}" height="144" rx="5" fill="none" stroke="${LINE}" stroke-width="1.3"/>
  ${text(x + c1 + c2 / 2, y + 24, `課税対象（${formatPercent(document.taxRate)}）`, 'label small', 13, 'middle')}
  ${text(x + c1 + c2 + (w - c1 - c2) / 2, y + 24, '非課税対象', 'label small', 13, 'middle')}
  ${text(x + 10, y + 59, '対象額合計', 'small')}
  ${text(x + c1 + c2 - 8, y + 59, formatYen(totals.taxableSubtotal), 'small amount', 13, 'end')}
  ${text(x + w - 8, y + 59, formatYen(totals.nonTaxableSubtotal + totals.outOfScopeSubtotal), 'small amount', 13, 'end')}
  ${text(x + 10, y + 94, `消費税(${formatPercent(document.taxRate)})`, 'small')}
  ${text(x + c1 + c2 - 8, y + 94, formatYen(totals.tax), 'small amount', 13, 'end')}
  ${text(x + 10, y + 132, '総額', 'blue bold', 17)}
  ${text(x + c1 + (w - c1) / 2, y + 134, formatYen(totals.total), 'heavy amount', 23, 'middle')}`
}

function feeCard(
  legal: Array<{ label: string; amount: number }>,
  taxable: Array<{ label: string; amount: number }>,
  actual: Array<{ label: string; amount: number }>,
  totals: ReturnType<typeof calculateSalesEstimateTotals>,
) {
  const layout = salesEstimateSheetLayout.fee
  const fillRows = (rows: Array<{ label: string; amount: number }>, slots: number) => {
    const visible = [...rows]
    while (visible.length < slots) visible.push({ label: '', amount: 0 })
    return visible.slice(0, slots)
  }
  const rowsByBucket = { legalNonTaxable: legal, taxableFees: taxable, nonTaxableFees: actual }
  const totalsByBucket = { legalNonTaxable: totals.legalNonTaxable, taxableFees: totals.taxableFeeTotal, nonTaxableFees: totals.nonTaxableFeeTotal }
  const groups = layout.groups.map((group) => {
    const rows = fillRows(rowsByBucket[group.bucket], group.rows)
    const category = `<rect x="${layout.x}" y="${group.startY}" width="${layout.categoryWidth}" height="${group.endY - group.startY}" fill="#fff" stroke="${LINE}"/>
      ${verticalText(layout.x + layout.categoryWidth / 2, group.startY + (group.endY - group.startY) / 2, group.title, 'label bold', 13)}`
    const detailRows = rows.map((line, index) => valueRow(layout.detailX, group.startY + index * FEE_ROW_HEIGHT, layout.detailWidth, line.label, line.amount, FEE_ROW_HEIGHT, '')).join('')
    const subtotal = valueRow(layout.detailX, group.subtotalY, layout.detailWidth, '小計', totalsByBucket[group.bucket], FEE_SUBTOTAL_HEIGHT, 'bold pale')
    return category + detailRows + subtotal
  }).join('')
  return `${sectionHeader(layout.x, salesEstimateSheetLayout.lowerY, layout.totalWidth, '諸費用内訳')}
  <rect x="${layout.x}" y="${layout.bodyY}" width="${layout.totalWidth}" height="${layout.bodyHeight}" class="box"/>
  ${groups}
  ${valueRow(layout.x, layout.totalY, layout.totalWidth, '諸費用合計', totals.feesTotal, layout.totalHeight, 'bold pale', layout.totalSplitWidth)}`
}

function accessoryCard(rows: Array<{ label: string; amount: number }>, total: number) {
  const layout = salesEstimateSheetLayout.accessory
  const y = salesEstimateSheetLayout.lowerY
  const visible = [...rows]
  while (visible.length < layout.rowCount) visible.push({ label: '', amount: 0 })
  return `${sectionHeader(layout.x, y, layout.width, '付属品・特別仕様明細')}
  <rect x="${layout.x}" y="${layout.bodyY}" width="${layout.width}" height="${layout.bodyHeight}" class="box"/>
  ${simpleColumns(layout.x, layout.bodyY, [layout.nameWidth, layout.amountWidth], ['品名', '金額'], 36, true)}
  ${visible.slice(0, layout.rowCount).map((line, index) => valueRow(layout.x, layout.detailY + index * layout.rowHeight, layout.width, line.label, line.amount, layout.rowHeight, '', layout.nameWidth)).join('')}
  ${valueRow(layout.x, layout.totalY, layout.width, '付属品・特別仕様合計', total, layout.totalHeight, 'bold pale', layout.nameWidth)}`
}

function creditBlock(credit: SalesDocument['details']['credit']) {
  const y = salesEstimateSheetLayout.creditY
  const values = [
    credit.paymentCount,
    credit.bonusPayment ? formatYen(credit.bonusPayment) : '',
    credit.fee ? String(credit.fee) : '',
    credit.bonusMonths,
  ]
  return `
  <rect x="23" y="${y}" width="475" height="110" rx="5" class="box"/>
  ${text(48, y + 32, '▣  クレジットお支払いプラン', 'blue bold', 21)}
  <line x1="23" y1="${y + 44}" x2="498" y2="${y + 44}" class="line"/>
  ${simpleColumns(23, y + 44, [119, 119, 119, 118], ['回数', 'ボーナス払', '金利', '支払開始月'], 32, true)}
  ${simpleColumns(23, y + 76, [119, 119, 119, 118], values, 34, false)}`
}

function shopBlock(settings: AppSettings) {
  const y = salesEstimateSheetLayout.creditY
  const shop = settings.shop
  const hasLogo = Boolean(shop.logoDataUrl)
  const companyLeft = hasLogo ? 780 : 733
  const infoLeft = companyLeft + 20
  const logoMarkup = hasLogo
    ? `<image href="${escapeAttribute(shop.logoDataUrl)}" x="544" y="${y + 15}" width="207" height="95" preserveAspectRatio="xMidYMid meet"/>`
    : text(597, y + 118, '▦', 'blue bold', 113)
  return `
  ${logoMarkup}
  ${text(companyLeft, y + 35, shop.name || '店舗名未設定', 'blue heavy', 20)}
  ${text(infoLeft, y + 62, shop.postalCode ? `〒${shop.postalCode}` : '', '', 13)}
  ${text(infoLeft, y + 84, shop.address, '', 13)}
  ${text(infoLeft, y + 107, `TEL ${shop.phone}${shop.fax ? `　FAX ${shop.fax}` : ''}`, '', 13)}`
}

function sectionHeader(x: number, y: number, width: number, title: string) {
  return `<path d="M${x + 5} ${y}h${width - 10}a5 5 0 015 5v34H${x}V${y + 5}a5 5 0 015-5z" class="section"/>
  ${text(x + width / 2, y + 27, title, 'sectionText', 20, 'middle')}`
}

function tableRow(x: number, y: number, cells: Array<[string, number]>, height: number) {
  let cursor = x
  return cells.map(([value, width], index) => {
    const label = index % 2 === 0
    const result = `<rect x="${cursor}" y="${y}" width="${width}" height="${height}" fill="${label ? PALE : '#fff'}" stroke="${LINE}"/>
      ${text(label ? cursor + width / 2 : cursor + 12, y + height / 2 + 6, value, label ? 'label small' : 'small', 13, label ? 'middle' : 'start')}`
    cursor += width
    return result
  }).join('')
}

function simpleColumns(x: number, y: number, widths: number[], values: string[], height: number, heading: boolean) {
  let cursor = x
  return values.map((value, index) => {
    const width = widths[index]
    const result = `<rect x="${cursor}" y="${y}" width="${width}" height="${height}" fill="${heading ? PALE : '#fff'}" stroke="${LINE}"/>
      ${text(cursor + width / 2, y + height / 2 + 6, value, heading ? 'label small' : 'small', 13, 'middle')}`
    cursor += width
    return result
  }).join('')
}

function valueRow(x: number, y: number, width: number, label: string, amount: number, height: number, tone: string, splitWidth?: number) {
  const split = splitWidth ?? Math.round(width * 0.61)
  const dark = tone.includes('dark')
  const fill = tone.includes('pale') ? PALE : dark ? BLUE : '#fff'
  const amountText = label || amount ? formatYen(amount) : ''
  const labelClass = dark ? 'white bold body' : tone.includes('bold') ? 'bold body' : 'small'
  const amountClass = `${dark ? 'white ' : ''}${tone.includes('bold') ? 'bold body' : 'small'} amount${tone.includes('discount') ? ' discount' : ''}`
  return `<rect x="${x}" y="${y}" width="${width}" height="${height}" fill="${fill}" stroke="${LINE}"/>
  <line x1="${x + split}" y1="${y}" x2="${x + split}" y2="${y + height}" class="line"/>
  ${text(x + 12, y + height / 2 + 6, label, labelClass)}
  ${text(x + width - 10, y + height / 2 + 6, amountText, amountClass, tone.includes('bold') ? 16 : 13, 'end')}`
}

function text(x: number, y: number, value: string, className = '', size?: number, anchor: 'start' | 'middle' | 'end' = 'start') {
  const style = ''
  return `<text x="${x}" y="${y}" class="${className}"${size ? ` font-size="${size}"` : ''} text-anchor="${anchor}"${style}>${escapeXml(value)}</text>`
}

function verticalText(x: number, y: number, value: string, className = '', size = 12) {
  const characters = Array.from(value)
  const lineHeight = size * 1.05
  const firstY = y - ((characters.length - 1) * lineHeight) / 2
  return characters.map((character, index) => `<text x="${x}" y="${firstY + index * lineHeight}" class="${className}" font-size="${size}" text-anchor="middle" dominant-baseline="middle">${escapeXml(character)}</text>`).join('')
}

function formatYen(amount: number) {
  return `¥${new Intl.NumberFormat('ja-JP').format(Math.round(amount))}`
}

function formatPercent(rate: number) {
  const value = rate * 100
  return `${Number.isInteger(value) ? value : value.toFixed(2)}%`
}

function formatJapaneseDate(value: string) {
  const match = value?.replaceAll('/', '-').match(/^(\d{4})-(\d{2})-(\d{2})$/)
  return match ? `${Number(match[1])}年${Number(match[2])}月${Number(match[3])}日` : value || '未設定'
}

function escapeXml(value: string) {
  return String(value).replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;').replaceAll("'", '&apos;')
}

function escapeAttribute(value: string) {
  return escapeXml(value)
}
