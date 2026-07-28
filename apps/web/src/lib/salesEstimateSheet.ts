import type { SalesDocument } from './salesApi'
import { buildSalesEstimateSections, calculateSalesEstimateTotals } from './salesEstimate'
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
  const legalLines = sections.legalNonTaxable.slice(0, 3)
  const taxableLines = sections.taxableFees.slice(0, 5)
  const actualLines = sections.nonTaxableFees.slice(0, 3)
  const accessories = sections.accessories.slice(0, 13)

  return `<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 0 ${WIDTH} ${HEIGHT}" role="img" aria-label="お見積書">
  <defs>
    <style>
      text{font-family:"Noto Sans JP","Yu Gothic","Meiryo",sans-serif;fill:#101820}
      .blue{fill:${BLUE}} .white{fill:#fff}.muted{fill:#334a6c}.bold{font-weight:700}.heavy{font-weight:800}
      .label{fill:${BLUE};font-weight:700}.small{font-size:13px}.body{font-size:16px}.value{font-size:17px}
      .amount{font-variant-numeric:tabular-nums}.line{stroke:${LINE};stroke-width:1}.box{fill:#fff;stroke:${LINE};stroke-width:1.3}
      .section{fill:url(#sectionGradient)}.sectionText{fill:#fff;font-size:20px;font-weight:700;letter-spacing:2px}
    </style>
    <linearGradient id="sectionGradient" x1="0" x2="1"><stop stop-color="${BLUE_DARK}"/><stop offset="1" stop-color="#0050a9"/></linearGradient>
    <clipPath id="vehiclePhotoClip"><rect x="392" y="106" width="293" height="194" rx="5"/></clipPath>
  </defs>
  <rect width="${WIDTH}" height="${HEIGHT}" fill="#fff"/>
  ${sheetTitle(document)}
  ${withImage ? imageCustomerBlock(document, imageHref) : expandedCustomerBlock(document)}
  ${amountPanel(document, totals)}
  ${vehicleBlock(vehicle, withImage ? 314 : 296)}
  ${tradeInBlock(details.tradeIn, withImage ? 557 : 543)}
  ${taxCaption(document, totals, 692)}
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
  return `
  <text x="28" y="73" class="blue heavy" font-size="50" letter-spacing="8">お見積書</text>
  <line x1="27" y1="86" x2="253" y2="86" stroke="${BLUE}" stroke-width="5"/>
  <rect x="731" y="13" width="306" height="130" rx="4" class="box"/>
  ${rows.map(([label, value], index) => {
    const y = 13 + index * 26
    return `${index ? `<line x1="731" y1="${y}" x2="1037" y2="${y}" class="line"/>` : ''}
      <line x1="844" y1="${y}" x2="844" y2="${y + 26}" class="line"/>
      ${text(744, y + 18, label, 'label body')}
      ${text(865, y + 18, value, 'body')}`
  }).join('')}`
}

function imageCustomerBlock(document: SalesDocument, imageHref: string) {
  const customer = { ...document.customerDetails, ...document.details.customerOverride }
  return `
  <rect x="24" y="108" width="338" height="186" rx="5" class="box"/>
  <circle cx="57" cy="144" r="19" fill="${BLUE}"/>
  <circle cx="57" cy="138" r="7" fill="#fff"/><path d="M45 158c1-10 6-14 12-14s11 4 12 14" fill="#fff"/>
  ${text(89, 151, `${customer.name || document.customerName || '未設定'} ${document.details.customerHonorific || '様'}`, 'blue heavy', 24)}
  ${text(43, 190, customer.postalCode ? `〒${customer.postalCode}` : '〒 未登録', 'body')}
  ${text(43, 222, customer.address || '住所未登録', 'body')}
  ${text(43, 259, `TEL：${customer.phone || document.phone || '未登録'}`, 'body')}
  <rect x="392" y="106" width="293" height="194" rx="5" fill="#f2f4f7" stroke="${LINE}" stroke-width="1"/>
  <image href="${escapeAttribute(imageHref)}" x="392" y="106" width="293" height="194" preserveAspectRatio="xMidYMid slice" clip-path="url(#vehiclePhotoClip)"/>`
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
  <rect x="27" y="103" width="650" height="179" rx="5" class="box"/>
  <line x1="377" y1="103" x2="377" y2="282" class="line"/>
  <circle cx="58" cy="139" r="19" fill="${BLUE}"/>
  <circle cx="58" cy="133" r="7" fill="#fff"/><path d="M46 153c1-10 6-14 12-14s11 4 12 14" fill="#fff"/>
  ${text(90, 147, `${customer.name || document.customerName || '未設定'} ${details.customerHonorific || '様'}`, 'blue heavy', 24)}
  ${text(44, 184, customer.postalCode ? `〒${customer.postalCode}` : '〒 未登録', 'body')}
  ${text(44, 216, customer.address || '住所未登録', 'body')}
  ${text(44, 252, `TEL：${customer.phone || document.phone || '未登録'}`, 'body')}
  ${rows.map(([label, value], index) => {
    const y = 103 + index * 44.75
    return `${index ? `<line x1="377" y1="${y}" x2="677" y2="${y}" class="line"/>` : ''}
      <rect x="377" y="${y}" width="101" height="44.75" fill="${PALE}"/>
      <line x1="478" y1="${y}" x2="478" y2="${y + 44.75}" class="line"/>
      ${text(397, y + 28, label, 'label body')}
      ${text(492, y + 28, value, 'body')}`
  }).join('')}`
}

function amountPanel(document: SalesDocument, totals: ReturnType<typeof calculateSalesEstimateTotals>) {
  return `
  <rect x="707" y="157" width="330" height="318" rx="5" class="box"/>
  <path d="M712 157h320a5 5 0 015 5v45H707v-45a5 5 0 015-5z" class="section"/>
  ${text(872, 191, 'お見積金額（税込）', 'sectionText', 20, 'middle')}
  ${text(872, 260, formatYen(totals.total), 'blue heavy amount', 46, 'middle')}
  <line x1="712" y1="284" x2="1032" y2="284" class="line"/>
  ${amountLine(722, 310, `課税対象額（${formatPercent(document.taxRate)}）`, totals.taxableSubtotal)}
  ${amountLine(722, 343, `消費税（${formatPercent(document.taxRate)}）`, totals.tax)}
  ${amountLine(722, 376, '非課税対象額', totals.nonTaxableSubtotal + totals.outOfScopeSubtotal)}
  <line x1="718" y1="393" x2="1026" y2="393" stroke="${LINE}" stroke-dasharray="2 2"/>
  ${text(722, 426, `支払期限：${formatJapaneseDate(document.dueDate)}`, 'body')}
  ${text(722, 458, `状態：${document.status}`, 'body')}`
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
  return `
  <rect x="707" y="489" width="330" height="162" rx="5" class="box"/>
  ${text(722, 520, '必要書類', 'blue bold', 20)}
  <line x1="707" y1="528" x2="1037" y2="528" class="line"/>
  ${rows.map(([label, checked], index) => {
    const col = index % 2
    const row = Math.floor(index / 2)
    const x = 725 + col * 156
    const y = 554 + row * 26
    return `<rect x="${x}" y="${y - 13}" width="11" height="11" fill="#fff" stroke="#222"/><text x="${x + 5.5}" y="${y - 3.5}" text-anchor="middle" font-size="12">${checked ? '✓' : ''}</text>${text(x + 20, y - 3, label, 'small')}`
  }).join('')}`
}

function noteBlock(note: string) {
  return `<rect x="707" y="664" width="330" height="65" rx="5" class="box"/>
  ${text(722, 692, '備考', 'blue bold', 19)}
  <line x1="707" y1="700" x2="1037" y2="700" class="line"/>
  ${text(722, 721, note || '特になし', 'small')}`
}

function vehiclePriceCard(document: SalesDocument, sections: ReturnType<typeof buildSalesEstimateSections>, totals: ReturnType<typeof calculateSalesEstimateTotals>) {
  const x = 23
  const y = 742
  const w = 324
  const rowH = 35
  const topY = y + 39
  const topHeight = 267
  const taxY = y + 316
  const paymentY = y + 470
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
  <rect x="${x}" y="${paymentY}" width="${w}" height="95" rx="5" fill="none" stroke="${LINE}" stroke-width="1.3"/>`
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
  const x = 363
  const y = 742
  const w = 299
  let cursor = y + 39
  const group = (title: string, rows: Array<{ label: string; amount: number }>, total: number, slots: number) => {
    const start = cursor
    const rowH = 26
    cursor += 27
    const visible = [...rows]
    while (visible.length < slots) visible.push({ label: '', amount: 0 })
    const body = `${text(x + 14, start + 22, title, 'label bold', 15)}
      ${visible.slice(0, slots).map((line, index) => valueRow(x, cursor + index * rowH, w, line.label, line.amount, rowH, '')).join('')}`
    cursor += slots * rowH
    const subtotal = valueRow(x, cursor, w, '小計', total, 30, 'bold pale')
    cursor += 32
    return body + subtotal
  }
  const groups = group('法定費用（非課税）', legal, totals.legalNonTaxable, 3)
    + group('手続代行費用（課税）', taxable, totals.taxableFeeTotal, 5)
    + group('実費・預託金（非課税）', actual, totals.nonTaxableFeeTotal, 3)
  return `${sectionHeader(x, y, w, '諸費用内訳')}
  <rect x="${x}" y="${y + 39}" width="${w}" height="526" class="box"/>
  ${groups}
  ${valueRow(x, y + 526, w, '諸費用合計', totals.feesTotal, 39, 'bold pale')}`
}

function accessoryCard(rows: Array<{ label: string; amount: number }>, total: number) {
  const x = 677
  const y = 742
  const w = 360
  const rowH = 34
  const visible = [...rows]
  while (visible.length < 13) visible.push({ label: '', amount: 0 })
  return `${sectionHeader(x, y, w, '付属品・特別仕様明細')}
  <rect x="${x}" y="${y + 39}" width="${w}" height="526" class="box"/>
  ${simpleColumns(x, y + 39, [210, 150], ['品名', '金額'], 36, true)}
  ${visible.slice(0, 13).map((line, index) => valueRow(x, y + 75 + index * rowH, w, line.label, line.amount, rowH, '', 210)).join('')}
  ${valueRow(x, y + 517, w, '付属品・特別仕様合計', total, 48, 'bold pale', 210)}`
}

function creditBlock(credit: SalesDocument['details']['credit']) {
  const values = [
    credit.paymentCount,
    credit.bonusPayment ? formatYen(credit.bonusPayment) : '',
    credit.fee ? String(credit.fee) : '',
    credit.bonusMonths,
  ]
  return `
  <rect x="23" y="1322" width="475" height="110" rx="5" class="box"/>
  ${text(48, 1354, '▣  クレジットお支払いプラン', 'blue bold', 21)}
  <line x1="23" y1="1366" x2="498" y2="1366" class="line"/>
  ${simpleColumns(23, 1366, [119, 119, 119, 118], ['回数', 'ボーナス払', '金利', '支払開始月'], 32, true)}
  ${simpleColumns(23, 1398, [119, 119, 119, 118], values, 34, false)}`
}

function shopBlock(settings: AppSettings) {
  return `
  <line x1="520" y1="1321" x2="1037" y2="1321" stroke="${BLUE}" stroke-width="1.5"/>
  ${text(659, 1361, '▦', 'blue bold', 30)}
  ${text(699, 1360, settings.shop.name || '店舗名未設定', 'blue heavy', 23)}
  ${text(699, 1385, '車両販売システム', 'blue bold small')}
  ${text(622, 1415, settings.shop.postalCode ? `〒${settings.shop.postalCode}　${settings.shop.address}` : settings.shop.address || '住所未登録', 'body')}
  ${text(622, 1442, `${settings.shop.phone ? `TEL ${settings.shop.phone}` : 'TEL 未登録'}　${settings.shop.registrationNumber ? `FAX ${settings.shop.registrationNumber}` : ''}`, 'body')}`
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
