import { PDFDocument, rgb, StandardFonts, type PDFFont, type PDFPage } from 'pdf-lib'
import fontkit from '@pdf-lib/fontkit'
import fontUrl from '../assets/fonts/NotoSansCJKjp-Regular.otf?url'
import type { MaintenanceDocument } from './maintenanceApi'
import type { AppSettings } from './settingsApi'
import type { SalesDocument } from './salesApi'

const PAGE_WIDTH = 595.28
const PAGE_HEIGHT = 841.89
const MARGIN = 42
const FOOTER_HEIGHT = 32
const CONTENT_WIDTH = PAGE_WIDTH - MARGIN * 2

const colors = {
  ink: rgb(0.12, 0.16, 0.22),
  muted: rgb(0.36, 0.42, 0.5),
  line: rgb(0.82, 0.85, 0.89),
  soft: rgb(0.96, 0.97, 0.98),
  primary: rgb(0.09, 0.3, 0.67),
  white: rgb(1, 1, 1),
}

type PageState = {
  pdf: PDFDocument
  font: PDFFont
  latinFont: PDFFont
  page: PDFPage
  pageNumber: number
  title: string
  number: string
  issuedAt: string
  settings: AppSettings
  salesDocument?: SalesDocument
  y: number
}

type TableColumn = {
  label: string
  width: number
  align?: 'left' | 'right' | 'center'
}

type Cell = string | number

export async function downloadSalesDocumentPdf(document: SalesDocument, settings: AppSettings) {
  const bytes = await createSalesDocumentPdf(document, settings)
  downloadPdf(bytes, `${document.number}-${document.type}.pdf`)
}

export async function previewSalesDocumentPdf(document: SalesDocument, settings: AppSettings) {
  await previewPdf(() => createSalesDocumentPdf(document, settings))
}

export async function downloadMaintenanceDocumentPdf(document: MaintenanceDocument, settings: AppSettings) {
  const bytes = await createMaintenanceDocumentPdf(document, settings)
  downloadPdf(bytes, `${document.number}-${document.type}.pdf`)
}

export async function previewMaintenanceDocumentPdf(document: MaintenanceDocument, settings: AppSettings) {
  await previewPdf(() => createMaintenanceDocumentPdf(document, settings))
}

export async function createSalesDocumentPdf(document: SalesDocument, settings: AppSettings) {
  const state = await createPageState(salesDocumentTitle(document.type), document.number, document.issuedAt, settings, document)
  drawSalesDocumentIntro(state, document)
  drawSectionTitle(state, '明細')
  const columns: TableColumn[] = [
    { label: 'No.', width: 22, align: 'center' },
    { label: '作業内容／部品名等', width: 195 },
    { label: '数量', width: 35, align: 'right' },
    { label: '単位', width: 28, align: 'center' },
    { label: '部品単価', width: 66, align: 'right' },
    { label: '部品金額', width: 66, align: 'right' },
    { label: '技術料・他', width: 62, align: 'right' },
    { label: '摘要・課税', width: 37, align: 'left' },
  ]
  drawTable(state, columns, document.items.map((item, index) => [
    String(index + 1),
    item.description || '（明細未入力）',
    formatNumber(item.quantity),
    item.unit,
    formatYen(item.unitPrice),
    formatYen(Math.round(item.quantity * item.unitPrice)),
    formatYen(item.otherAmount),
    [item.summary, item.taxCategory].filter(Boolean).join(' / '),
  ]), colors.soft)

  drawSalesDocumentBottom(state, document, settings)
  finishPage(state)
  return state.pdf.save()
}

async function createMaintenanceDocumentPdf(document: MaintenanceDocument, settings: AppSettings) {
  const state = await createPageState(document.type, document.number, document.issuedAt, settings)
  drawCustomerAndVehicle(state, document.customerName, document.phone, document.category, document.vehicle, `${document.plate}${document.mileage ? ` / ${document.mileage}` : ''}`)
  drawMetaGrid(state, [
    ['書類日付', document.issuedAt || '未設定'],
    ['入庫日', document.intakeDate || '未設定'],
    ['出庫予定日', document.plannedReleaseDate || '未設定'],
    ['完了日', document.completionDate || '未設定'],
    ['支払期限', document.dueDate || '未設定'],
    ['状態', document.status],
    ['消費税・端数', `${formatPercent(document.taxRate)} / ${settings.tax.rounding}`],
  ])

  drawSectionTitle(state, '作業・部品明細')
  const columns: TableColumn[] = [
    { label: '区分', width: 60 },
    { label: '作業内容・部品名', width: 208 },
    { label: '数量', width: 45, align: 'right' },
    { label: '単位', width: 42, align: 'center' },
    { label: '単価', width: 72, align: 'right' },
    { label: '金額', width: 84, align: 'right' },
  ]
  drawTable(state, columns, document.items.map((item) => [
    item.kind,
    item.description || '（明細未入力）',
    formatNumber(item.quantity),
    item.unit,
    formatYen(item.unitPrice),
    formatYen(item.quantity * item.unitPrice),
  ]))

  const itemsSubtotal = document.items.reduce((sum, item) => sum + item.quantity * item.unitPrice, 0)
  const fees = Object.values(document.fees).reduce((sum, fee) => sum + fee, 0)
  const tax = calculateTax(itemsSubtotal + document.adjustment, document.taxRate, settings.tax.rounding)
  drawTotals(state, [
    ['作業・部品小計', formatYen(itemsSubtotal)],
    ['法定費用等', formatYen(fees)],
    ['調整額', formatYen(document.adjustment)],
    [`消費税（${formatPercent(document.taxRate)}）`, formatYen(tax)],
    ['合計金額', formatYen(itemsSubtotal + fees + document.adjustment + tax)],
  ])
  drawDocumentNotes(state, document.note, settings)
  finishPage(state)
  return state.pdf.save()
}

async function createPageState(title: string, number: string, issuedAt: string, settings: AppSettings, salesDocument?: SalesDocument): Promise<PageState> {
  const pdf = await PDFDocument.create()
  pdf.registerFontkit(fontkit)
  const fontBytes = await fetch(fontUrl).then(async (response) => {
    if (!response.ok) throw new Error('帳票用フォントを読み込めませんでした。')
    return new Uint8Array(await response.arrayBuffer())
  })
  const font = await pdf.embedFont(fontBytes, { subset: false })
  const latinFont = await pdf.embedFont(StandardFonts.Helvetica)
  const state: PageState = { pdf, font, latinFont, page: pdf.addPage([PAGE_WIDTH, PAGE_HEIGHT]), pageNumber: 1, title, number, issuedAt, settings, salesDocument, y: PAGE_HEIGHT - MARGIN }
  if (salesDocument) drawSalesPageHeader(state, salesDocument)
  else drawPageHeader(state)
  return state
}

function drawPageHeader(state: PageState) {
  const { page, settings } = state
  const shop = settings.shop
  const shopLines = [
    shop.postalCode ? `〒${shop.postalCode}` : '',
    shop.address,
    shop.phone ? `TEL ${shop.phone}` : '',
    shop.representative ? `担当 ${shop.representative}` : '',
    shop.registrationNumber ? `登録番号 ${shop.registrationNumber}` : '',
  ].filter(Boolean)
  const top = PAGE_HEIGHT - MARGIN
  const rightX = PAGE_WIDTH - MARGIN - 205
  const rightWidth = 205
  drawTextTop(page, state.font, state.title, MARGIN, top + 2, 21, colors.ink, 190)
  const numberLabel = truncateText(`No. ${state.number}`, state.latinFont, 10, rightWidth)
  drawTextTop(page, state.latinFont, numberLabel, rightX + rightWidth - state.latinFont.widthOfTextAtSize(numberLabel, 10), top + 2, 10, colors.ink)
  drawTextTop(page, state.font, truncateText(shop.name || '店舗名未設定', state.font, 11, rightWidth), rightX, top - 16, 11, colors.ink, rightWidth, 'right')
  drawTextTop(page, state.font, `発行日 ${state.issuedAt || '未設定'}`, rightX, top - 32, 9, colors.muted, rightWidth, 'right')
  const detailLines = shopLines.slice(0, 4)
  const detailTop = top - 47
  detailLines.forEach((line, index) => drawTextTop(page, state.font, truncateText(line, state.font, 8.5, rightWidth), rightX, detailTop - index * 11, 8.5, colors.muted, rightWidth, 'right'))
  const ruleY = detailTop - detailLines.length * 11 - 4
  page.drawLine({ start: { x: MARGIN, y: ruleY }, end: { x: PAGE_WIDTH - MARGIN, y: ruleY }, thickness: 1, color: colors.line })
  state.y = ruleY - 20
}

function salesDocumentTitle(type: SalesDocument['type']) {
  return type === '見積書' ? 'お見積書' : type
}

function drawSalesPageHeader(state: PageState, document: SalesDocument) {
  const top = PAGE_HEIGHT - MARGIN
  const titleWidth = 190
  state.page.drawRectangle({ x: MARGIN, y: top - 28, width: titleWidth, height: 24, color: rgb(0.86, 0.93, 1), borderColor: colors.ink, borderWidth: 0.8 })
  drawTextTop(state.page, state.font, salesDocumentTitle(document.type), MARGIN + 4, top - 5, 14, colors.ink, titleWidth - 8, 'center')

  const metaX = PAGE_WIDTH - MARGIN - 280
  const metaWidths = [56, 56, 56, 70, 42]
  const metaLabels = ['日付', '販売区分', '担当', '見積番号', 'ページ']
  const metaValues = [document.issuedAt || '未設定', document.details.salesCategory || '未設定', document.details.staffName || '未設定', document.number || '未設定', String(state.pageNumber)]
  drawSalesGridRow(state, metaX, top, metaWidths, metaLabels.map((text) => ({ text, fill: rgb(0.86, 0.93, 1), bold: true, align: 'center', size: 7.6 })), 18)
  drawSalesGridRow(state, metaX, top - 18, metaWidths, metaValues.map((text) => ({ text, align: 'center', size: 7.5 })), 22)
  const ruleY = top - 50
  state.page.drawLine({ start: { x: MARGIN, y: ruleY }, end: { x: PAGE_WIDTH - MARGIN, y: ruleY }, thickness: 0.8, color: colors.line })
  state.y = ruleY - 14
}

function drawSalesDocumentIntro(state: PageState, document: SalesDocument) {
  const customer = document.customerDetails
  const vehicle = document.vehicleDetails
  const details = document.details
  const top = state.y
  const gap = 12
  const leftWidth = 302
  const rightWidth = CONTENT_WIDTH - leftWidth - gap
  ensureSpace(state, 72 + 12 + 90 + 12 + 44 + 12 + 52)

  drawSalesGridBlock(state, MARGIN, top, [68, 234], [
    [{ text: 'お名前', fill: rgb(0.86, 0.93, 1), bold: true, align: 'center' }, { text: `${customer.name || '未設定'} ${details.customerHonorific || '様'}\n${customer.kana || ''}`, size: 9.2 }],
    [{ text: 'ご住所', fill: rgb(0.86, 0.93, 1), bold: true, align: 'center' }, { text: `${customer.postalCode ? `〒${customer.postalCode}\n` : ''}${customer.address || '住所未登録'}`, size: 8.5 }],
  ], [32, 40])
  drawSalesGridBlock(state, MARGIN + leftWidth + gap, top, [72, rightWidth - 72], [
    [{ text: '生年月日', fill: rgb(0.86, 0.93, 1), bold: true, align: 'center', size: 7.6 }, { text: details.customerBirthDate || '未設定', size: 8 }],
    [{ text: '電話番号', fill: rgb(0.86, 0.93, 1), bold: true, align: 'center', size: 7.6 }, { text: customer.phone || '未登録', size: 8 }],
    [{ text: '勤務先等', fill: rgb(0.86, 0.93, 1), bold: true, align: 'center', size: 7.6 }, { text: details.customerEmployer || '未設定', size: 8 }],
    [{ text: '連絡先TEL', fill: rgb(0.86, 0.93, 1), bold: true, align: 'center', size: 7.6 }, { text: details.customerContactPhone || '未設定', size: 8 }],
  ], [18, 18, 18, 18])
  state.y = top - 72 - 12

  const vehicleValues = vehicle ?? emptySalesVehicleDetails()
  drawSalesGridBlock(state, MARGIN, state.y, [55, 174, 58, 70, 72, 82], [
    [{ text: 'メーカー', fill: rgb(0.86, 0.93, 1), bold: true, align: 'center', size: 7.5 }, { text: '車名・仕様', fill: rgb(0.86, 0.93, 1), bold: true, align: 'center', size: 7.5 }, { text: '年式', fill: rgb(0.86, 0.93, 1), bold: true, align: 'center', size: 7.5 }, { text: '排気量', fill: rgb(0.86, 0.93, 1), bold: true, align: 'center', size: 7.5 }, { text: 'ミッション', fill: rgb(0.86, 0.93, 1), bold: true, align: 'center', size: 7.5 }, { text: '車体色', fill: rgb(0.86, 0.93, 1), bold: true, align: 'center', size: 7.5 }],
    [{ text: vehicleValues.maker || '未設定' }, { text: [vehicleValues.name, vehicleValues.modelType].filter(Boolean).join(' / ') || '未設定' }, { text: vehicleValues.year || '未設定' }, { text: vehicleValues.displacement || '未設定' }, { text: vehicleValues.transmission || '未設定' }, { text: vehicleValues.color || '未設定' }],
    [{ text: '型式', fill: rgb(0.86, 0.93, 1), bold: true, align: 'center', size: 7.5 }, { text: '車台番号', fill: rgb(0.86, 0.93, 1), bold: true, align: 'center', size: 7.5 }, { text: '登録番号', fill: rgb(0.86, 0.93, 1), bold: true, align: 'center', size: 7.5 }, { text: '走行距離', fill: rgb(0.86, 0.93, 1), bold: true, align: 'center', size: 7.5 }, { text: '車検日', fill: rgb(0.86, 0.93, 1), bold: true, align: 'center', size: 7.5 }, { text: '記録簿', fill: rgb(0.86, 0.93, 1), bold: true, align: 'center', size: 7.5 }],
    [{ text: vehicleValues.modelType || '未設定' }, { text: vehicleValues.vin || '未設定' }, { text: vehicleValues.plate || '未設定' }, { text: vehicleValues.mileage || '未設定' }, { text: vehicleValues.inspectionDate || '未設定' }, { text: vehicleValues.inspectionRecordAvailable ? 'あり' : 'なし' }],
  ], [18, 24, 18, 24])
  state.y -= 90 + 12

  drawSalesGridBlock(state, MARGIN, state.y, [176, 63, 82, 92, 98], [
    [{ text: '下取車名（型式等）', fill: rgb(0.86, 0.93, 1), bold: true, align: 'center', size: 7.5 }, { text: '年式', fill: rgb(0.86, 0.93, 1), bold: true, align: 'center', size: 7.5 }, { text: '車検日', fill: rgb(0.86, 0.93, 1), bold: true, align: 'center', size: 7.5 }, { text: '走行距離', fill: rgb(0.86, 0.93, 1), bold: true, align: 'center', size: 7.5 }, { text: '車体色', fill: rgb(0.86, 0.93, 1), bold: true, align: 'center', size: 7.5 }],
    [{ text: details.tradeIn.name || 'なし' }, { text: details.tradeIn.modelYear || '-' }, { text: details.tradeIn.inspectionDate || '-' }, { text: details.tradeIn.mileage || '-' }, { text: details.tradeIn.color || '-' }],
  ], [20, 24])
  state.y -= 44 + 12

  const totals = calculateSalesTotals(document, state.settings.tax.rounding)
  const summaryTop = state.y
  drawSalesGridBlock(state, MARGIN, summaryTop, [112, 93], [[{ text: 'お見積金額', fill: rgb(0.86, 0.93, 1), bold: true, align: 'center', size: 8.5 }, { text: formatYen(totals.total), bold: true, align: 'right', size: 12 }]], [34])
  const taxX = MARGIN + 205 + 12
  drawSalesGridBlock(state, taxX, summaryTop, [98, 98, 98], [[{ text: '課税対象額', fill: rgb(0.86, 0.93, 1), bold: true, align: 'center', size: 7.5 }, { text: '消費税', fill: rgb(0.86, 0.93, 1), bold: true, align: 'center', size: 7.5 }, { text: '非課税・対象外', fill: rgb(0.86, 0.93, 1), bold: true, align: 'center', size: 7.5 }], [{ text: formatYen(totals.taxableSubtotal), align: 'right' }, { text: formatYen(totals.tax), align: 'right' }, { text: formatYen(totals.nonTaxableSubtotal + totals.outOfScopeSubtotal), align: 'right' }]], [18, 22])
  state.y = summaryTop - 34 - 14
  drawTextTop(state.page, state.font, `支払期限：${document.dueDate || '未設定'}　状態：${document.status}`, MARGIN, state.y, 8, colors.muted, CONTENT_WIDTH, 'right')
  state.y -= 14
}

function drawSalesDocumentBottom(state: PageState, document: SalesDocument, settings: AppSettings) {
  const details = document.details
  const totals = calculateSalesTotals(document, settings.tax.rounding)
  const blockHeight = 215
  ensureSpace(state, blockHeight)
  const top = state.y
  const leftWidth = 235
  const gap = 16
  const rightX = MARGIN + leftWidth + gap
  const rightWidth = CONTENT_WIDTH - leftWidth - gap
  drawSalesGridBlock(state, MARGIN, top, [130, 105], [[{ text: 'リサイクル料金（預託金）', fill: rgb(0.86, 0.93, 1), bold: true, size: 7.8 }, { text: formatYen(details.recycleFee), align: 'right', size: 8.5 }]], [22])
  drawSalesGridBlock(state, MARGIN, top - 28, [70, 48, 70, 47], [[{ text: '頭金・現金', fill: rgb(0.86, 0.93, 1), bold: true, align: 'center', size: 7.5 }, { text: formatYen(details.downPayment ?? 0), align: 'right', size: 7.8 }, { text: '残金・所要', fill: rgb(0.86, 0.93, 1), bold: true, align: 'center', size: 7.5 }, { text: formatYen(details.remainingPayment ?? 0), align: 'right', size: 7.8 }]], [22])
  const creditText = details.credit.enabled
    ? `${details.credit.paymentCount || '回数未設定'}　手数料 ${formatYen(details.credit.fee)}　月々 ${formatYen(details.credit.monthlyPayment)}\n初回 ${formatYen(details.credit.initialPayment)}　賞与 ${details.credit.bonusMonths || '月未設定'} / ${formatYen(details.credit.bonusPayment)}`
    : '利用なし'
  drawSalesGridBlock(state, MARGIN, top - 56, [235], [[{ text: 'クレジットお支払いプラン', fill: rgb(0.86, 0.93, 1), bold: true, align: 'center', size: 7.8 }], [{ text: creditText, size: 7.2 }]], [18, 34])
  drawSalesGridBlock(state, MARGIN, top - 116, [80, 155], [[{ text: '備考', fill: rgb(0.86, 0.93, 1), bold: true, align: 'center', size: 8 }, { text: document.note || '特になし', size: 7.8 }]], [40])
  const required = requiredSalesDocumentLabels(details).join(' ／ ') || '未確認'
  drawSalesGridBlock(state, MARGIN, top - 162, [80, 155], [[{ text: '必要書類', fill: rgb(0.86, 0.93, 1), bold: true, align: 'center', size: 8 }, { text: required, size: 7.5 }]], [38])

  drawTextTop(state.page, state.font, settings.shop.name || '店舗名未設定', rightX, top - 6, 12, colors.ink, rightWidth, 'right')
  const shopLines = [settings.shop.postalCode ? `〒${settings.shop.postalCode}` : '', settings.shop.address, settings.shop.phone ? `TEL ${settings.shop.phone}` : '', settings.shop.representative ? `担当 ${settings.shop.representative}` : ''].filter(Boolean)
  shopLines.forEach((line, index) => drawTextTop(state.page, state.font, line, rightX, top - 24 - index * 11, 8, colors.muted, rightWidth, 'right'))
  const paymentTop = top - 116
  drawTextTop(state.page, state.font, 'お支払いについて', rightX, paymentTop, 8, colors.muted, rightWidth)
  drawTextTop(state.page, state.font, settings.document.paymentNote || '店頭または指定口座へお支払いください。', rightX, paymentTop - 13, 7.8, colors.ink, rightWidth)
  drawTextTop(state.page, state.font, '振込先', rightX, paymentTop - 34, 8, colors.muted, rightWidth)
  drawTextTop(state.page, state.font, [settings.shop.bankName, settings.shop.bankAccount].filter(Boolean).join(' / ') || '未設定', rightX, paymentTop - 47, 7.8, colors.ink, rightWidth)
  drawTextTop(state.page, state.font, `課税対象 ${formatYen(totals.taxableSubtotal)}　消費税 ${formatYen(totals.tax)}`, rightX, top - 162, 7.8, colors.muted, rightWidth, 'right')
}

function drawSalesGridBlock(state: PageState, x: number, top: number, widths: number[], rows: SalesGridCell[][], rowHeights: number[]) {
  let rowTop = top
  rows.forEach((row, rowIndex) => {
    drawSalesGridRow(state, x, rowTop, widths, row, rowHeights[rowIndex] ?? 22)
    rowTop -= rowHeights[rowIndex] ?? 22
  })
  return rowTop
}

function drawSalesGridRow(state: PageState, x: number, top: number, widths: number[], cells: SalesGridCell[], height: number) {
  let cellX = x
  widths.forEach((width, index) => {
    const cell = cells[index] ?? { text: '' }
    state.page.drawRectangle({ x: cellX, y: top - height, width, height, color: cell.fill, borderColor: colors.line, borderWidth: 0.55 })
    const size = cell.size ?? (cell.fill ? 8 : 8.3)
    const font = cell.font ?? (/[\u3040-\u30ff\u3400-\u9fff]/.test(cell.text) ? state.font : state.latinFont)
    const lines = wrapText(cell.text, font, size, Math.max(width - 8, 10))
    const lineHeight = size + 2
    const visibleLines = Math.max(1, Math.floor((height - 5) / lineHeight))
    lines.slice(0, visibleLines).forEach((line, lineIndex) => drawTextTop(state.page, font, line, cellX + 4, top - 3 - lineIndex * lineHeight, size, cell.color ?? (cell.fill ? colors.ink : colors.ink), width - 8, cell.align ?? 'left'))
    cellX += width
  })
}

function requiredSalesDocumentLabels(details: SalesDocument['details']) {
  const labels = [
    details.requiredDocuments.sealCertificate ? '印鑑証明' : '',
    details.requiredDocuments.residentCard ? '住民票' : '',
    details.requiredDocuments.lightVehicleCertificate ? '軽自動車住所証明' : '',
    details.requiredDocuments.transferCertificate ? '譲渡証明' : '',
    details.requiredDocuments.taxPaymentCertificate ? '納税証明' : '',
    details.requiredDocuments.warrantyCertificate ? '保証書・承諾書' : '',
    details.requiredDocuments.other,
  ]
  return labels.filter(Boolean)
}

function emptySalesVehicleDetails(): NonNullable<SalesDocument['vehicleDetails']> {
  return { maker: '', name: '', modelType: '', plate: '', vin: '', year: '', inspectionDate: '', mileage: '', color: '', displacement: '', transmission: '', inspectionRecordAvailable: false }
}

type SalesGridCell = { text: string; fill?: ReturnType<typeof rgb>; color?: ReturnType<typeof rgb>; font?: PDFFont; bold?: boolean; align?: 'left' | 'right' | 'center'; size?: number }

function drawCustomerAndVehicle(state: PageState, customerName: string, phone: string, category: string, vehicle: string, vehicleExtra: string) {
  ensureSpace(state, 68)
  const top = state.y
  const gap = 12
  const boxWidth = (CONTENT_WIDTH - gap) / 2
  drawInfoBox(state, MARGIN, top, boxWidth, '顧客', [customerName || '未設定', phone || '電話番号未登録'])
  drawInfoBox(state, MARGIN + boxWidth + gap, top, boxWidth, category ? '対象車両・入庫区分' : '対象車両', [vehicle || '車両未指定', `${category ? `${category} / ` : ''}${vehicleExtra || '登録情報なし'}`])
  state.y -= 78
}

function drawInfoBox(state: PageState, x: number, top: number, width: number, label: string, values: string[]) {
  state.page.drawRectangle({ x, y: top - 58, width, height: 58, color: colors.soft, borderColor: colors.line, borderWidth: 0.8 })
  drawTextTop(state.page, state.font, label, x + 10, top - 9, 8.5, colors.primary)
  values.forEach((value, index) => {
    const size = index === 0 ? 11 : 8.5
    drawTextTop(state.page, state.font, truncateText(value, state.font, size, width - 20), x + 10, top - 25 - index * 14, size, index === 0 ? colors.ink : colors.muted, width - 20)
  })
}

function drawMetaGrid(state: PageState, entries: Array<[string, string]>) {
  const columns = 2
  const rowHeight = 22
  const rows = Math.ceil(entries.length / columns)
  ensureSpace(state, rows * rowHeight + 15)
  entries.forEach(([label, value], index) => {
    const row = Math.floor(index / columns)
    const column = index % columns
    const width = CONTENT_WIDTH / columns
    const x = MARGIN + column * width
    const top = state.y - row * rowHeight
    drawTextTop(state.page, state.font, label, x + 4, top, 8.5, colors.muted)
    drawTextTop(state.page, state.font, truncateText(value, state.font, 9, width / 2 - 6), x + width / 2, top, 9, colors.ink, width / 2 - 6, 'right')
    state.page.drawLine({ start: { x, y: top - 16 }, end: { x: x + width - 8, y: top - 16 }, thickness: 0.5, color: colors.line })
  })
  state.y -= rows * rowHeight + 14
}

function drawSectionTitle(state: PageState, title: string) {
  ensureSpace(state, 32)
  drawTextTop(state.page, state.font, title, MARGIN, state.y, 12, colors.ink)
  state.page.drawLine({ start: { x: MARGIN + 78, y: state.y - 7 }, end: { x: PAGE_WIDTH - MARGIN, y: state.y - 7 }, thickness: 1, color: colors.line })
  state.y -= 22
}

function drawTable(state: PageState, columns: TableColumn[], rows: Cell[][], headerColor = colors.soft) {
  const headerHeight = 27
  drawTableHeader(state, columns, headerHeight, headerColor)
  rows.forEach((row) => {
    const wrapped = row.map((cell, index) => wrapText(String(cell), state.font, 8.5, columns[index].width - 10))
    const rowHeight = Math.max(25, ...wrapped.map((lines) => lines.length * 11 + 8))
    if (state.y - rowHeight < FOOTER_HEIGHT + MARGIN) {
      nextPage(state)
      drawTableHeader(state, columns, headerHeight, headerColor)
    }
    const rowTop = state.y
    let x = MARGIN
    columns.forEach((column, index) => {
      state.page.drawRectangle({ x, y: rowTop - rowHeight, width: column.width, height: rowHeight, borderColor: colors.line, borderWidth: 0.5 })
      drawCellLines(state.page, state.font, wrapped[index], x, rowTop - 5, column.width, column.align ?? 'left')
      x += column.width
    })
    state.y -= rowHeight
  })
  if (!rows.length) {
    if (state.y - 25 < FOOTER_HEIGHT + MARGIN) nextPage(state)
    state.page.drawRectangle({ x: MARGIN, y: state.y - 25, width: CONTENT_WIDTH, height: 25, borderColor: colors.line, borderWidth: 0.5 })
    drawTextTop(state.page, state.font, '明細はありません。', MARGIN + 8, state.y - 7, 8.5, colors.muted)
    state.y -= 25
  }
  state.y -= 16
}

function drawTableHeader(state: PageState, columns: TableColumn[], headerHeight: number, headerColor = colors.soft) {
  if (state.y - headerHeight < FOOTER_HEIGHT + MARGIN) nextPage(state)
  const top = state.y
  let x = MARGIN
  columns.forEach((column) => {
    state.page.drawRectangle({ x, y: top - headerHeight, width: column.width, height: headerHeight, color: headerColor, borderColor: colors.line, borderWidth: 0.5 })
    drawTextTop(state.page, state.font, column.label, x + 5, top - 8, 8.5, colors.muted, column.width - 10, column.align ?? 'left')
    x += column.width
  })
  state.y -= headerHeight
}

function drawCellLines(page: PDFPage, font: PDFFont, lines: string[], x: number, top: number, width: number, align: 'left' | 'right' | 'center') {
  lines.forEach((line, index) => drawTextTop(page, font, line, x + 5, top - index * 11, 8.5, colors.ink, width - 10, align))
}

function drawTotals(state: PageState, entries: Array<[string, string]>) {
  const rowHeight = 21
  const height = entries.length * rowHeight + 14
  ensureSpace(state, height)
  const width = 235
  const x = PAGE_WIDTH - MARGIN - width
  const top = state.y
  state.page.drawRectangle({ x, y: top - height, width, height, borderColor: colors.line, borderWidth: 0.8 })
  entries.forEach(([label, value], index) => {
    const rowTop = top - 7 - index * rowHeight
    const isTotal = index === entries.length - 1
    if (isTotal) state.page.drawRectangle({ x: x + 0.5, y: rowTop - rowHeight + 1, width: width - 1, height: rowHeight, color: colors.primary })
    const labelSize = isTotal ? 10 : 8.5
    const valueSize = isTotal ? 11 : 9
    drawTextTop(state.page, state.font, truncateText(label, state.font, labelSize, width - 105), x + 10, rowTop, labelSize, isTotal ? colors.white : colors.muted, width - 105)
    drawTextTop(state.page, state.font, truncateText(value, state.font, valueSize, width - 112), x + width - 10, rowTop, valueSize, isTotal ? colors.white : colors.ink, width - 112, 'right')
  })
  state.y -= height + 14
}

function drawDocumentNotes(state: PageState, note: string, settings: AppSettings) {
  const notes = [
    ['備考', note || '特になし'],
    ['お支払いについて', settings.document.paymentNote || '店頭または指定口座へお支払いください。'],
    ['振込先', [settings.shop.bankName, settings.shop.bankAccount].filter(Boolean).join(' / ') || '未設定'],
  ] as Array<[string, string]>
  notes.forEach(([label, value]) => {
    const lines = wrapText(value, state.font, 8.5, CONTENT_WIDTH - 112)
    const height = Math.max(24, lines.length * 12 + 13)
    ensureSpace(state, height + 4)
    drawTextTop(state.page, state.font, label, MARGIN + 4, state.y, 8.5, colors.muted, 100)
    drawCellLines(state.page, state.font, lines, MARGIN + 105, state.y, CONTENT_WIDTH - 105, 'left')
    state.page.drawLine({ start: { x: MARGIN, y: state.y - height + 3 }, end: { x: PAGE_WIDTH - MARGIN, y: state.y - height + 3 }, thickness: 0.5, color: colors.line })
    state.y -= height
  })
  if (settings.document.footerNote) {
    const lines = wrapText(settings.document.footerNote, state.font, 8, CONTENT_WIDTH)
    ensureSpace(state, lines.length * 12 + 12)
    lines.forEach((line, index) => drawTextTop(state.page, state.font, line, MARGIN, state.y - index * 12, 8, colors.muted, CONTENT_WIDTH))
    state.y -= lines.length * 12 + 6
  }
}

function ensureSpace(state: PageState, height: number) {
  if (state.y - height < FOOTER_HEIGHT + MARGIN) nextPage(state)
}

function nextPage(state: PageState) {
  finishPage(state)
  state.page = state.pdf.addPage([PAGE_WIDTH, PAGE_HEIGHT])
  state.pageNumber += 1
  if (state.salesDocument) drawSalesPageHeader(state, state.salesDocument)
  else drawPageHeader(state)
}

function finishPage(state: PageState) {
  state.page.drawLine({ start: { x: MARGIN, y: MARGIN - 8 }, end: { x: PAGE_WIDTH - MARGIN, y: MARGIN - 8 }, thickness: 0.6, color: colors.line })
  drawTextTop(state.page, state.font, state.settings.document.footerNote || '車両管理システム', MARGIN, MARGIN - 19, 7.5, colors.muted, CONTENT_WIDTH - 70)
  drawTextTop(state.page, state.font, `ページ ${state.pageNumber}`, PAGE_WIDTH - MARGIN - 65, MARGIN - 19, 7.5, colors.muted, 65, 'right')
}

function drawTextTop(page: PDFPage, font: PDFFont, text: string, x: number, top: number, size: number, color: ReturnType<typeof rgb>, width?: number, align: 'left' | 'right' | 'center' = 'left') {
  const safeText = Array.from(text, (character) => {
    const code = character.charCodeAt(0)
    return code < 32 && code !== 9 && code !== 10 ? ' ' : character
  }).join('')
  const textWidth = font.widthOfTextAtSize(safeText, size)
  const boxWidth = width ?? textWidth
  const offset = align === 'right' ? boxWidth - textWidth : align === 'center' ? (boxWidth - textWidth) / 2 : 0
  page.drawText(safeText, { x: x + Math.max(0, offset), y: top - size, size, font, color, maxWidth: width })
}

function truncateText(value: string, font: PDFFont, size: number, maxWidth: number) {
  const text = Array.from(value, (character) => {
    const code = character.charCodeAt(0)
    return code < 32 && code !== 9 && code !== 10 ? ' ' : character
  }).join('')
  if (font.widthOfTextAtSize(text, size) <= maxWidth) return text
  let output = ''
  for (const character of Array.from(text)) {
    const next = `${output}${character}…`
    if (font.widthOfTextAtSize(next, size) > maxWidth) break
    output += character
  }
  return output ? `${output}…` : '…'
}

function wrapText(value: string, font: PDFFont, size: number, maxWidth: number) {
  const text = value.replaceAll('\r', '')
  const output: string[] = []
  text.split('\n').forEach((paragraph) => {
    let line = ''
    Array.from(paragraph).forEach((character) => {
      const next = line + character
      if (line && font.widthOfTextAtSize(next, size) > maxWidth) {
        output.push(line)
        line = character
      } else {
        line = next
      }
    })
    output.push(line || ' ')
  })
  return output
}

function calculateSalesTotals(document: SalesDocument, rounding: AppSettings['tax']['rounding']) {
  const amountOf = (item: SalesDocument['items'][number]) => Math.round(item.quantity * item.unitPrice + item.otherAmount)
  const subtotal = document.items.reduce((sum, item) => sum + amountOf(item), 0)
  const taxableSubtotal = document.items.filter((item) => item.taxCategory === '課税').reduce((sum, item) => sum + amountOf(item), 0)
  const nonTaxableSubtotal = document.items.filter((item) => item.taxCategory === '非課税').reduce((sum, item) => sum + amountOf(item), 0)
  const outOfScopeSubtotal = document.items.filter((item) => item.taxCategory === '対象外').reduce((sum, item) => sum + amountOf(item), 0)
  const taxValue = Math.max(0, taxableSubtotal) * document.taxRate
  const tax = rounding === '四捨五入' ? Math.round(taxValue) : Math.floor(taxValue)
  return { subtotal, taxableSubtotal, nonTaxableSubtotal, outOfScopeSubtotal, tax, total: subtotal + tax }
}

function calculateTax(base: number, rate: number, rounding: AppSettings['tax']['rounding']) {
  const value = Math.max(0, base) * rate
  return rounding === '四捨五入' ? Math.round(value) : Math.floor(value)
}

function formatYen(amount: number) {
  return `¥${new Intl.NumberFormat('ja-JP').format(Math.round(amount))}`
}

function formatNumber(value: number) {
  return new Intl.NumberFormat('ja-JP', { maximumFractionDigits: 2 }).format(value)
}

function formatPercent(value: number) {
  return `${Number.isInteger(value * 100) ? value * 100 : (value * 100).toFixed(2)}%`
}

function downloadPdf(bytes: Uint8Array, filename: string) {
  const url = URL.createObjectURL(createPdfBlob(bytes))
  const link = document.createElement('a')
  link.href = url
  link.download = filename
  link.click()
  window.setTimeout(() => URL.revokeObjectURL(url), 60_000)
}

async function previewPdf(create: () => Promise<Uint8Array>) {
  const previewWindow = window.open('', '_blank')
  try {
    const bytes = await create()
    const url = URL.createObjectURL(createPdfBlob(bytes))
    if (previewWindow) {
      previewWindow.location.href = url
      window.setTimeout(() => URL.revokeObjectURL(url), 60_000)
    } else {
      downloadPdf(bytes, '帳票プレビュー.pdf')
    }
  } catch (error) {
    previewWindow?.close()
    throw error
  }
}

function createPdfBlob(bytes: Uint8Array) {
  const buffer = new ArrayBuffer(bytes.byteLength)
  new Uint8Array(buffer).set(bytes)
  return new Blob([buffer], { type: 'application/pdf' })
}
