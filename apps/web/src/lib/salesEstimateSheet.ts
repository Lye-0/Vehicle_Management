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
const META_TABLE_X = 613
const META_TABLE_WIDTH = 424
const META_TABLE_HEADER_HEIGHT = 29
const META_TABLE_HEIGHT = 65
const META_COLUMN_WIDTHS = [94, 70, 70, 120, 70]
const TOP_BLOCK_GAP = 12
const TITLE_X = 24
const TITLE_Y = 13
const TITLE_WIDTH = 520
const TITLE_HEIGHT = 55
const CUSTOMER_X = 24
const CUSTOMER_Y = 90
const CUSTOMER_WIDTH = 661
const CUSTOMER_HEIGHT = 168
const IMAGE_CUSTOMER_WIDTH = 338
const IMAGE_X = 392
const IMAGE_WIDTH = 293
const AMOUNT_X = 707
const AMOUNT_PANEL_HEIGHT = 318
const REQUIRED_BLOCK_HEIGHT = 162
const REQUIRED_NOTE_GAP = 9
const LOWER_SECTION_SHIFT = 14
const CREDIT_TOP_GAP = 29
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
  const lowerY = 742 - UPPER_COMPRESSION - LOWER_SECTION_SHIFT
  const amountPanelY = META_TABLE_Y + META_TABLE_HEIGHT + TOP_BLOCK_GAP
  const requiredY = amountPanelY + AMOUNT_PANEL_HEIGHT + TOP_BLOCK_GAP
  const noteY = requiredY + REQUIRED_BLOCK_HEIGHT + REQUIRED_NOTE_GAP
  const vehicleY = CUSTOMER_Y + CUSTOMER_HEIGHT + TOP_BLOCK_GAP
  const tradeInY = vehicleY + 227 + TOP_BLOCK_GAP
  const taxCaptionY = tradeInY + 100 + 20
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
    metaTableX: META_TABLE_X,
    metaTableWidth: META_TABLE_WIDTH,
    metaHeaderHeight: META_TABLE_HEADER_HEIGHT,
    metaTableHeight: META_TABLE_HEIGHT,
    metaColumnWidths: META_COLUMN_WIDTHS,
    title: {
      x: TITLE_X,
      y: TITLE_Y,
      width: TITLE_WIDTH,
      height: TITLE_HEIGHT,
    },
    customer: {
      x: CUSTOMER_X,
      y: CUSTOMER_Y,
      width: CUSTOMER_WIDTH,
      height: CUSTOMER_HEIGHT,
      imageWidth: IMAGE_CUSTOMER_WIDTH,
      imageX: IMAGE_X,
      imageWidthTotal: IMAGE_WIDTH,
    },
    amountPanelY,
    amountPanelX: AMOUNT_X,
    amountPanelHeight: AMOUNT_PANEL_HEIGHT,
    imageCustomerHeight: CUSTOMER_HEIGHT,
    expandedCustomerHeight: CUSTOMER_HEIGHT,
    imageVehicleY: vehicleY,
    expandedVehicleY: vehicleY,
    imageTradeInY: tradeInY,
    expandedTradeInY: tradeInY,
    taxCaptionY,
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
    creditY: feeBottomY + CREDIT_TOP_GAP,
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
      .label{fill:${BLUE};font-weight:700}.small{font-size:13px}.body{font-size:16px}.customerBody{font-size:13px}.customerValue{font-size:11px}.value{font-size:17px}
      .amount{font-variant-numeric:tabular-nums}.line{stroke:${LINE};stroke-width:1}.box{fill:#fff;stroke:${LINE};stroke-width:1.3}
      .section{fill:url(#sectionGradient)}.sectionText{fill:#fff;font-size:20px;font-weight:700;letter-spacing:2px}
    </style>
    <linearGradient id="sectionGradient" x1="0" x2="1"><stop stop-color="${BLUE_DARK}"/><stop offset="1" stop-color="#0050a9"/></linearGradient>
    <clipPath id="vehiclePhotoClip"><rect x="${salesEstimateSheetLayout.customer.imageX}" y="${salesEstimateSheetLayout.customer.y}" width="${salesEstimateSheetLayout.customer.imageWidthTotal}" height="${salesEstimateSheetLayout.imageCustomerHeight}" rx="5"/></clipPath>
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
    ['日付', formatSlashDate(document.issuedAt)],
    ['販売', document.details.salesCategory || '未設定'],
    ['担当', document.details.staffName || '未設定'],
    ['書類番号', document.number],
    ['ページ', '1 / 1'],
  ]
  const { title, metaTableX, metaTableY, metaTableWidth, metaHeaderHeight, metaTableHeight, metaColumnWidths } = salesEstimateSheetLayout
  const titleText = salesDocumentTitle(document.type)
  const metaRight = metaTableX + metaTableWidth
  let columnX = metaTableX
  return `
  <rect x="${title.x}" y="${title.y}" width="${title.width}" height="${title.height}" rx="5" class="box"/>
  ${brandMark(title.x + 16, title.y + 14)}
  ${brandMark(title.x + title.width - 37, title.y + 14, true)}
  ${text(title.x + title.width / 2, title.y + 36, titleText, 'blue heavy', 25, 'middle')}
  <rect x="${metaTableX}" y="${metaTableY}" width="${metaTableWidth}" height="${metaTableHeight}" rx="4" class="box"/>
  <line x1="${metaTableX}" y1="${metaTableY + metaHeaderHeight}" x2="${metaRight}" y2="${metaTableY + metaHeaderHeight}" class="line"/>
  ${rows.map(([label, value], index) => {
    const width = metaColumnWidths[index]
    const center = columnX + width / 2
    const result = `${index ? `<line x1="${columnX}" y1="${metaTableY}" x2="${columnX}" y2="${metaTableY + metaTableHeight}" class="line"/>` : ''}
      ${text(center, metaTableY + 20, label, 'label', 11, 'middle')}
      ${text(center, metaTableY + 52, value, '', 11, 'middle')}`
    columnX += width
    return result
  }).join('')}
  <line x1="${metaRight}" y1="${metaTableY}" x2="${metaRight}" y2="${metaTableY + metaTableHeight}" class="line"/>`
}

function imageCustomerBlock(document: SalesDocument, imageHref: string) {
  const customer = { ...document.customerDetails, ...document.details.customerOverride }
  const { x, y, imageX, imageWidthTotal } = salesEstimateSheetLayout.customer
  return `
  <rect x="${x}" y="${y}" width="${salesEstimateSheetLayout.customer.imageWidth}" height="${salesEstimateSheetLayout.imageCustomerHeight}" rx="5" class="box"/>
  <circle cx="57" cy="${y + 36}" r="19" fill="${BLUE}"/>
  <circle cx="57" cy="${y + 30}" r="7" fill="#fff"/><path d="M45 ${y + 50}c1-10 6-14 12-14s11 4 12 14" fill="#fff"/>
  ${text(89, y + 43, `${customer.name || document.customerName || '未設定'} ${document.details.customerHonorific || '様'}`, 'blue heavy', 20)}
  ${text(43, y + 82, customer.postalCode ? `〒${customer.postalCode}` : '〒 未登録', 'customerBody')}
  ${text(43, y + 114, customer.address || '住所未登録', 'customerBody')}
  ${text(43, y + 151, `TEL：${customer.phone || document.phone || '未登録'}`, 'customerBody')}
  <rect x="${imageX}" y="${y}" width="${imageWidthTotal}" height="${salesEstimateSheetLayout.imageCustomerHeight}" rx="5" fill="#f2f4f7" stroke="${LINE}" stroke-width="1"/>
  <image href="${escapeAttribute(imageHref)}" x="${imageX}" y="${y}" width="${imageWidthTotal}" height="${salesEstimateSheetLayout.imageCustomerHeight}" preserveAspectRatio="xMidYMid slice" clip-path="url(#vehiclePhotoClip)"/>`
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
  const { x, y, width, height } = salesEstimateSheetLayout.customer
  const leftLabelWidth = 109
  const leftContentEnd = x + 353
  const rightLabelWidth = 101
  const rightLabelX = leftContentEnd
  const rightValueX = rightLabelX + rightLabelWidth
  const rightRowHeight = height / rows.length
  return `
  <rect x="${x}" y="${y}" width="${width}" height="${height}" rx="5" class="box"/>
  <rect x="${x + 1}" y="${y + 1}" width="${leftLabelWidth - 2}" height="${height / 2 - 1}" fill="${PALE}"/>
  <rect x="${x + 1}" y="${y + height / 2}" width="${leftLabelWidth - 2}" height="${height / 2 - 1}" fill="${PALE}"/>
  <line x1="${x + leftLabelWidth}" y1="${y}" x2="${x + leftLabelWidth}" y2="${y + height}" class="line"/>
  <line x1="${leftContentEnd}" y1="${y}" x2="${leftContentEnd}" y2="${y + height}" class="line"/>
  <line x1="${rightValueX}" y1="${y}" x2="${rightValueX}" y2="${y + height}" class="line"/>
  <line x1="${x}" y1="${y + height / 2}" x2="${leftContentEnd}" y2="${y + height / 2}" class="line"/>
  ${rows.map((_, index) => index ? `<line x1="${rightLabelX}" y1="${y + index * rightRowHeight}" x2="${x + width}" y2="${y + index * rightRowHeight}" class="line"/>` : '').join('')}
  ${text(x + leftLabelWidth / 2, y + height / 4 + 5, 'お名前', 'label', 13, 'middle')}
  ${text(x + leftLabelWidth / 2, y + height * 3 / 4 + 5, 'ご住所', 'label', 13, 'middle')}
  ${text(x + 121, y + 37, `${customer.name || document.customerName || '未設定'} ${details.customerHonorific || '様'}`, 'blue heavy', 18)}
  ${text(x + 121, y + 60, customer.kana || 'ふりがな未登録', 'muted', 11)}
  ${text(x + 121, y + 115, customer.postalCode ? `〒${customer.postalCode}` : '〒 未登録', 'customerBody')}
  ${text(x + 121, y + 143, customer.address || '住所未登録', 'customerBody')}
  ${rows.map(([label, value], index) => {
    const rowY = y + index * rightRowHeight
    return `<rect x="${rightLabelX + 1}" y="${rowY + 1}" width="${rightLabelWidth - 2}" height="${rightRowHeight - 2}" fill="${PALE}"/>
      ${text(rightLabelX + rightLabelWidth / 2, rowY + rightRowHeight / 2 + 5, label, 'label', 12, 'middle')}
      ${text(rightValueX + 14, rowY + rightRowHeight / 2 + 5, value, 'customerValue', 12)}`
  }).join('')}`
}

function amountPanel(document: SalesDocument, totals: ReturnType<typeof calculateSalesEstimateTotals>) {
  const y = salesEstimateSheetLayout.amountPanelY
  const x = salesEstimateSheetLayout.amountPanelX
  return `
  <rect x="${x}" y="${y}" width="330" height="${salesEstimateSheetLayout.amountPanelHeight}" rx="5" class="box"/>
  <path d="M${x + 5} ${y}h320a5 5 0 015 5v45H${x}v-45a5 5 0 015-5z" class="section"/>
  ${text(x + 165, y + 34, salesDocumentAmountTitle(document.type), 'sectionText', 20, 'middle')}
  ${text(x + 165, y + 103, formatYen(totals.total), 'blue heavy amount', 46, 'middle')}
  <line x1="${x + 5}" y1="${y + 127}" x2="${x + 325}" y2="${y + 127}" class="line"/>
  ${amountLine(x + 15, y + 153, `課税対象額（${formatPercent(document.taxRate)}）`, totals.taxableSubtotal, x + 314)}
  ${amountLine(x + 15, y + 186, `消費税（${formatPercent(document.taxRate)}）`, totals.tax, x + 314)}
  ${amountLine(x + 15, y + 219, '非課税対象額', totals.nonTaxableSubtotal + totals.outOfScopeSubtotal, x + 314)}
  <line x1="${x + 11}" y1="${y + 236}" x2="${x + 319}" y2="${y + 236}" stroke="${LINE}" stroke-dasharray="2 2"/>
  ${text(x + 15, y + 269, `支払期限：${formatJapaneseDate(document.dueDate)}`, 'body')}
  ${text(x + 15, y + 301, `状態：${document.status}`, 'body')}`
}

function amountLine(x: number, y: number, label: string, amount: number, valueX = 1021) {
  return `${text(x, y, label, 'body')}${text(valueX, y, formatYen(amount), 'body amount', 16, 'end')}`
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
  return characters.map((character, index) => `<text x="${x}" y="${firstY + index * lineHeight}" class="${className}" font-size="${size}" text-anchor="middle" dominant-baseline="middle" writing-mode="tb" text-orientation="upright">${escapeXml(character)}</text>`).join('')
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

function formatSlashDate(value: string) {
  const match = value?.replaceAll('/', '-').match(/^(\d{4})-(\d{2})-(\d{2})$/)
  return match ? `${match[1]}/${match[2]}/${match[3]}` : value || '未設定'
}

function brandMark(x: number, y: number, reversed = false) {
  if (reversed) {
    return `<g fill="${BLUE}">
      <rect x="${x + 9}" y="${y}" width="9" height="9"/>
      <rect x="${x}" y="${y + 9}" width="9" height="9"/>
      <rect x="${x + 9}" y="${y + 18}" width="9" height="9"/>
    </g>`
  }

  return `<g fill="${BLUE}">
    <rect x="${x}" y="${y}" width="9" height="9"/>
    <rect x="${x + 9}" y="${y + 9}" width="9" height="9"/>
    <rect x="${x}" y="${y + 18}" width="9" height="9"/>
  </g>`
}

function escapeXml(value: string) {
  return String(value).replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;').replaceAll("'", '&apos;')
}

function escapeAttribute(value: string) {
  return escapeXml(value)
}
