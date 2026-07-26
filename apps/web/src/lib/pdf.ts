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

async function createSalesDocumentPdf(document: SalesDocument, settings: AppSettings) {
  const state = await createPageState(document.type, document.number, document.issuedAt, settings)
  drawCustomerAndVehicle(state, document.customerName, document.phone, '', document.vehicle, document.plate)
  drawMetaGrid(state, [
    ['書類日付', document.issuedAt || '未設定'],
    ['支払期限', document.dueDate || '未設定'],
    ['状態', document.status],
    ['消費税・端数', `${formatPercent(document.taxRate)} / ${settings.tax.rounding}`],
  ])

  drawSectionTitle(state, '販売明細')
  const columns: TableColumn[] = [
    { label: '区分', width: 94 },
    { label: '内容', width: 180 },
    { label: '数量', width: 45, align: 'right' },
    { label: '単位', width: 42, align: 'center' },
    { label: '単価', width: 72, align: 'right' },
    { label: '金額', width: 78, align: 'right' },
  ]
  drawTable(state, columns, document.items.map((item) => [
    item.itemType,
    item.description || '（明細未入力）',
    formatNumber(item.quantity),
    item.unit,
    formatYen(item.unitPrice),
    formatYen(item.quantity * item.unitPrice),
  ]))

  const subtotal = document.items.reduce((sum, item) => sum + item.quantity * item.unitPrice, 0)
  const tax = calculateTax(subtotal, document.taxRate, settings.tax.rounding)
  drawTotals(state, [
    ['小計', formatYen(subtotal)],
    [`消費税（${formatPercent(document.taxRate)}）`, formatYen(tax)],
    ['合計金額', formatYen(subtotal + tax)],
  ])
  drawDocumentNotes(state, document.note, settings)
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

async function createPageState(title: string, number: string, issuedAt: string, settings: AppSettings): Promise<PageState> {
  const pdf = await PDFDocument.create()
  pdf.registerFontkit(fontkit)
  const fontBytes = await fetch(fontUrl).then(async (response) => {
    if (!response.ok) throw new Error('帳票用フォントを読み込めませんでした。')
    return new Uint8Array(await response.arrayBuffer())
  })
  const font = await pdf.embedFont(fontBytes, { subset: false })
  const latinFont = await pdf.embedFont(StandardFonts.Helvetica)
  const state: PageState = { pdf, font, latinFont, page: pdf.addPage([PAGE_WIDTH, PAGE_HEIGHT]), pageNumber: 1, title, number, issuedAt, settings, y: PAGE_HEIGHT - MARGIN }
  drawPageHeader(state)
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

function drawTable(state: PageState, columns: TableColumn[], rows: Cell[][]) {
  const headerHeight = 27
  drawTableHeader(state, columns, headerHeight)
  rows.forEach((row) => {
    const wrapped = row.map((cell, index) => wrapText(String(cell), state.font, 8.5, columns[index].width - 10))
    const rowHeight = Math.max(25, ...wrapped.map((lines) => lines.length * 11 + 8))
    if (state.y - rowHeight < FOOTER_HEIGHT + MARGIN) {
      nextPage(state)
      drawTableHeader(state, columns, headerHeight)
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

function drawTableHeader(state: PageState, columns: TableColumn[], headerHeight: number) {
  if (state.y - headerHeight < FOOTER_HEIGHT + MARGIN) nextPage(state)
  const top = state.y
  let x = MARGIN
  columns.forEach((column) => {
    state.page.drawRectangle({ x, y: top - headerHeight, width: column.width, height: headerHeight, color: colors.soft, borderColor: colors.line, borderWidth: 0.5 })
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
  drawPageHeader(state)
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
