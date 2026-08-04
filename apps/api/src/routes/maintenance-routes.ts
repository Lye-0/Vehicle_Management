import { and, asc, desc, eq } from 'drizzle-orm'
import { customers, maintenanceItems, maintenanceDocuments, mileageHistories, vehicles } from '@vehicle-management/database'
import { UnauthorizedError } from '../auth/firebase'
import { requireOrganizationContext } from '../auth/organization'
import { createDatabase } from '../db/client'
import { restoreArchivedDocument } from '../document-archive'
import { archiveDocumentFromRoute } from './archive-routes'
import { nextDocumentNumber } from '../document-number'
import { HttpError, jsonResponse, readJson } from '../http'

const maintenanceDocumentTypes = new Set(['整備見積書', '整備請求書'])
const maintenanceStatuses = new Set(['下書き', '入金待ち', '完了', 'アーカイブ済み'])
const maintenanceCategories = new Set(['車検', '板金', '一般整備'])
const feeNames = ['自賠責', '重量税', '印紙代', 'リサイクル料金'] as const
type FeeName = typeof feeNames[number]

type MileageSync = {
  confirmed: true
  openedMileage: number
  inputMileage: number
}

function parseMileageSync(body: Record<string, unknown>): MileageSync | undefined {
  const raw = body.mileageSync
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return undefined
  const record = raw as Record<string, unknown>
  if (record.confirmed !== true) return undefined
  const openedMileage = integerNumber(record.openedMileage, -1)
  const inputMileage = integerNumber(record.inputMileage, -1)
  if (openedMileage < 0 || inputMileage < 0) return undefined
  return { confirmed: true, openedMileage, inputMileage }
}

function parseMileageValue(value: string | undefined | null): number | null {
  if (!value) return null
  const digits = value.replace(/[^0-9]/g, '')
  if (!digits) return null
  const parsed = Number(digits)
  return Number.isFinite(parsed) ? parsed : null
}

export async function handleMaintenanceRoutes(request: Request, env: Env): Promise<Response | null> {
  const pathname = new URL(request.url).pathname.replace(/\/$/, '') || '/'
  const isCollection = pathname === '/api/maintenance-documents'
  const restoreMatch = pathname.match(/^\/api\/maintenance-documents\/([^/]+)\/restore$/)
  const documentMatch = pathname.match(/^\/api\/maintenance-documents\/([^/]+)$/)
  if (!isCollection && !documentMatch && !restoreMatch) return null

  try {
    const database = createDatabase(env.DB)
    const context = await requireOrganizationContext(request, env, database)
    const organizationId = context.organization.organizationId

    if (isCollection) {
      if (request.method === 'GET') return await listMaintenanceDocuments(request, env, database, organizationId)
      if (request.method === 'POST') return await createMaintenanceDocument(request, env, database, organizationId)
      throw new HttpError(405, 'この操作には対応していません。')
    }

    if (restoreMatch) {
      if (request.method !== 'POST') throw new HttpError(405, 'この操作には対応していません。')
      return await restoreMaintenanceDocument(env, database, restoreMatch[1], organizationId)
    }

    if (request.method === 'PATCH') return await updateMaintenanceDocument(request, env, database, documentMatch![1], organizationId)
    if (request.method === 'DELETE') return await archiveMaintenanceDocument(env, database, documentMatch![1], organizationId, context.user.uid)
    throw new HttpError(405, 'この操作には対応していません。')
  } catch (error) {
    if (error instanceof UnauthorizedError) return jsonResponse({ error: error.message }, 401, env)
    if (error instanceof HttpError) return jsonResponse({ error: error.message }, error.status, env)
    console.error(error)
    return jsonResponse({ error: '整備書類の処理に失敗しました。' }, 500, env)
  }
}

async function listMaintenanceDocuments(request: Request, env: Env, database: ReturnType<typeof createDatabase>, organizationId: string) {
  return jsonResponse({ documents: await loadMaintenanceDocuments(database, organizationId, new URL(request.url).searchParams.get('includeArchived') === 'true') }, 200, env)
}

async function createMaintenanceDocument(request: Request, env: Env, database: ReturnType<typeof createDatabase>, organizationId: string) {
  const body = await readJson(request)
  const mileageSync = parseMileageSync(body)
  const input = await parseMaintenanceInput(body, database, organizationId)

  // Validate mileageSync for new documents
  if (mileageSync) {
    if (mileageSync.openedMileage === mileageSync.inputMileage) {
      throw new HttpError(400, '走行距離が変更されていないため記録できません。')
    }
    const overrideMileage = parseMileageValue(input.details.vehicleOverride?.mileage)
    if (mileageSync.inputMileage !== overrideMileage) {
      throw new HttpError(400, '走行距離が書類内容と一致しません。')
    }
    // For new documents, openedMileage should match the current vehicle mileage
    // If vehicle has no mileage (null), openedMileage can be 0 or null (both mean "no previous value")
  } else {
    // No mileageSync: verify the mileage on the document matches the vehicle (no change intended)
    const inputMileage = parseMileageValue(input.details.vehicleOverride?.mileage)
    const currentVehicleMileage = await getCurrentVehicleMileage(database, input.vehicleId, organizationId)
    if (inputMileage !== null && inputMileage !== currentVehicleMileage) {
      throw new HttpError(400, '走行距離が変更されていますが確認が送信されていません。')
    }
  }

  const id = crypto.randomUUID()
  const number = await nextDocumentNumber(env.DB, organizationId, 'M')
  await ensureMaintenanceDocumentNumberAvailable(database, number, organizationId)
  const totals = calculateMaintenanceTotals(input.items, input.fees, input.adjustment, input.taxRate, input.rounding)
  const now = new Date().toISOString()

  const itemRows = buildMaintenanceItemRows(input.items, input.fees, input.adjustment, id, organizationId)

  const statements: D1PreparedStatement[] = [
    env.DB.prepare(
      `INSERT INTO maintenance_documents
        (id, organization_id, number, type, category, status, customer_id, vehicle_id,
         intake_date, planned_release_date, completion_date, issued_at, due_date,
         tax_rate, tax_rounding, subtotal, tax, total, note, details_json)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`
    ).bind(
      id, organizationId, number, input.type, input.category, input.status,
      input.customerId, input.vehicleId, input.intakeDate, input.plannedReleaseDate,
      input.completionDate, input.issuedAt, input.dueDate,
      input.taxRate, input.rounding, totals.subtotal, totals.tax, totals.total,
      input.note, JSON.stringify(input.details)
    ),
    ...itemRows.map((item, index) =>
      env.DB.prepare(
        `INSERT INTO maintenance_items
          (id, organization_id, document_id, item_type, description, quantity, unit, unit_price, technical_fee, summary, amount, sort_order)
         VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`
      ).bind(
        item.id, organizationId, id, item.itemType, item.description,
        item.quantity, item.unit, item.unitPrice, item.technicalFee,
        item.summary, item.amount, index
      )
    ),
  ]

  if (mileageSync) {
    statements.push(
      env.DB.prepare(
        `INSERT INTO mileage_histories
          (id, organization_id, vehicle_id, maintenance_document_id, mileage, created_at, updated_at)
         VALUES (?, ?, ?, ?, ?, ?, ?)
         ON CONFLICT (organization_id, maintenance_document_id)
         DO UPDATE SET mileage = excluded.mileage, vehicle_id = excluded.vehicle_id, updated_at = excluded.updated_at`
      ).bind(crypto.randomUUID(), organizationId, input.vehicleId, id, mileageSync.inputMileage, now, now)
    )
    statements.push(
      env.DB.prepare(
        `UPDATE vehicles SET mileage = ?, updated_at = ?
         WHERE id = ? AND organization_id = ? AND (mileage IS NULL OR mileage < ?)`
      ).bind(mileageSync.inputMileage, now, input.vehicleId, organizationId, mileageSync.inputMileage)
    )
  }

  await env.DB.batch(statements)

  return jsonResponse({ document: await findMaintenanceDocument(database, id, organizationId) }, 201, env)
}

async function updateMaintenanceDocument(request: Request, env: Env, database: ReturnType<typeof createDatabase>, documentId: string, organizationId: string) {
  const current = await database.select().from(maintenanceDocuments).where(and(eq(maintenanceDocuments.id, documentId), eq(maintenanceDocuments.organizationId, organizationId))).get()
  if (!current) throw new HttpError(404, '整備書類が見つかりません。')

  const currentItems = await loadMaintenanceItems(database, documentId, organizationId)
  const body = await readJson(request)
  const mileageSync = parseMileageSync(body)

  // Compute persistedDocumentMileage using the same logic as the editor:
  //   vehicleOverride.mileage if present, otherwise the vehicle's current mileage
  const currentDetails = parseMaintenanceDetails(parseDetailsJson(current.detailsJson))
  const vehicleOverrideMileage = parseMileageValue(currentDetails.vehicleOverride?.mileage)
  const persistedDocumentMileage = vehicleOverrideMileage ?? await getCurrentVehicleMileage(database, current.vehicleId, organizationId)

  const input = await parseMaintenanceInput({
    ...body,
    type: body.type ?? current.type,
    status: body.status ?? normalizeMaintenanceStatus(current.status),
    category: body.category ?? current.category,
    number: body.number === undefined ? current.number : body.number,
    customerId: body.customerId ?? current.customerId,
    vehicleId: body.vehicleId ?? current.vehicleId,
    intakeDate: body.intakeDate === undefined ? current.intakeDate : body.intakeDate,
    plannedReleaseDate: body.plannedReleaseDate === undefined ? current.plannedReleaseDate ?? current.completionDate : body.plannedReleaseDate,
    completionDate: body.completionDate === undefined ? current.completionDate : body.completionDate,
    issuedAt: body.issuedAt ?? current.issuedAt,
    dueDate: body.dueDate === undefined ? current.dueDate : body.dueDate,
    taxRate: body.taxRate ?? current.taxRate,
    rounding: body.rounding ?? current.taxRounding,
    note: body.note === undefined ? current.note : body.note,
    details: body.details === undefined ? parseDetailsJson(current.detailsJson) : body.details,
    items: body.items === undefined ? currentItems.filter((item) => item.itemType === '作業' || item.itemType === '部品').map(toInputItem) : body.items,
    fees: body.fees === undefined ? extractFees(currentItems) : body.fees,
    adjustment: body.adjustment === undefined ? extractAdjustment(currentItems) : body.adjustment,
  }, database, organizationId)

  // Validate mileageSync for existing documents
  if (mileageSync) {
    if (mileageSync.openedMileage === mileageSync.inputMileage) {
      throw new HttpError(400, '走行距離が変更されていないため記録できません。')
    }
    const overrideMileage = parseMileageValue(input.details.vehicleOverride?.mileage)
    if (mileageSync.inputMileage !== overrideMileage) {
      throw new HttpError(400, '走行距離が書類内容と一致しません。')
    }
    // Compare openedMileage with persistedDocumentMileage, treating null/undefined and 0 as equivalent
    const openedForComparison = mileageSync.openedMileage ?? 0
    const persistedForComparison = persistedDocumentMileage ?? 0
    if (openedForComparison !== persistedForComparison) {
      throw new HttpError(409, '走行距離が開いた時点から変更されています。再読み込みしてください。')
    }
  } else {
    const inputMileage = parseMileageValue(input.details.vehicleOverride?.mileage)
    if (inputMileage !== null && inputMileage !== persistedDocumentMileage) {
      throw new HttpError(400, '走行距離が変更されていますが確認が送信されていません。')
    }
  }

  const totals = calculateMaintenanceTotals(input.items, input.fees, input.adjustment, input.taxRate, input.rounding)
  const number = input.number || current.number
  await ensureMaintenanceDocumentNumberAvailable(database, number, organizationId, documentId)
  const now = new Date().toISOString()

  const itemRows = buildMaintenanceItemRows(input.items, input.fees, input.adjustment, documentId, organizationId)

  const statements: D1PreparedStatement[] = [
    env.DB.prepare(
      `UPDATE maintenance_documents SET
         number = ?, type = ?, category = ?, status = ?, customer_id = ?, vehicle_id = ?,
         intake_date = ?, planned_release_date = ?, completion_date = ?, issued_at = ?, due_date = ?,
         tax_rate = ?, tax_rounding = ?, subtotal = ?, tax = ?, total = ?,
         note = ?, details_json = ?, updated_at = ?
       WHERE id = ? AND organization_id = ?`
    ).bind(
      number, input.type, input.category, input.status, input.customerId, input.vehicleId,
      input.intakeDate, input.plannedReleaseDate, input.completionDate, input.issuedAt, input.dueDate,
      input.taxRate, input.rounding, totals.subtotal, totals.tax, totals.total,
      input.note, JSON.stringify(input.details), now, documentId, organizationId
    ),
    env.DB.prepare(
      `DELETE FROM maintenance_items WHERE document_id = ? AND organization_id = ?`
    ).bind(documentId, organizationId),
    ...itemRows.map((item, index) =>
      env.DB.prepare(
        `INSERT INTO maintenance_items
          (id, organization_id, document_id, item_type, description, quantity, unit, unit_price, technical_fee, summary, amount, sort_order)
         VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`
      ).bind(
        item.id, organizationId, documentId, item.itemType, item.description,
        item.quantity, item.unit, item.unitPrice, item.technicalFee,
        item.summary, item.amount, index
      )
    ),
  ]

  if (mileageSync) {
    statements.push(
      env.DB.prepare(
        `INSERT INTO mileage_histories
          (id, organization_id, vehicle_id, maintenance_document_id, mileage, created_at, updated_at)
         VALUES (?, ?, ?, ?, ?, ?, ?)
         ON CONFLICT (organization_id, maintenance_document_id)
         DO UPDATE SET mileage = excluded.mileage, vehicle_id = excluded.vehicle_id, updated_at = excluded.updated_at`
      ).bind(crypto.randomUUID(), organizationId, input.vehicleId, documentId, mileageSync.inputMileage, now, now)
    )
    statements.push(
      env.DB.prepare(
        `UPDATE vehicles SET mileage = ?, updated_at = ?
         WHERE id = ? AND organization_id = ? AND (mileage IS NULL OR mileage < ?)`
      ).bind(mileageSync.inputMileage, now, input.vehicleId, organizationId, mileageSync.inputMileage)
    )
  }

  await env.DB.batch(statements)

  return jsonResponse({ document: await findMaintenanceDocument(database, documentId, organizationId) }, 200, env)
}

async function loadMaintenanceDocuments(database: ReturnType<typeof createDatabase>, organizationId: string, includeArchived = false) {
  const [documentRows, itemRows, customerRows, vehicleRows] = await Promise.all([
    database.select().from(maintenanceDocuments).where(eq(maintenanceDocuments.organizationId, organizationId)).orderBy(desc(maintenanceDocuments.issuedAt), desc(maintenanceDocuments.number)).all(),
    database.select().from(maintenanceItems).where(eq(maintenanceItems.organizationId, organizationId)).orderBy(asc(maintenanceItems.sortOrder)).all(),
    database.select().from(customers).where(eq(customers.organizationId, organizationId)).all(),
    database.select().from(vehicles).where(eq(vehicles.organizationId, organizationId)).all(),
  ])
  const itemsByDocument = groupBy(itemRows, (item) => item.documentId)
  const customersById = new Map(customerRows.map((customer) => [customer.id, customer]))
  const vehiclesById = new Map(vehicleRows.map((vehicle) => [vehicle.id, vehicle]))

  return documentRows.filter((document) => includeArchived || !document.archivedAt).map((document) => serializeMaintenanceDocument(
    document,
    customersById.get(document.customerId),
    vehiclesById.get(document.vehicleId),
    itemsByDocument.get(document.id) ?? [],
  ))
}

async function findMaintenanceDocument(database: ReturnType<typeof createDatabase>, documentId: string, organizationId: string) {
  const documents = await loadMaintenanceDocuments(database, organizationId)
  return documents.find((document) => document.id === documentId) ?? null
}

async function loadMaintenanceItems(database: ReturnType<typeof createDatabase>, documentId: string, organizationId: string) {
  return database.select().from(maintenanceItems).where(and(eq(maintenanceItems.documentId, documentId), eq(maintenanceItems.organizationId, organizationId))).orderBy(asc(maintenanceItems.sortOrder)).all()
}

async function getCurrentVehicleMileage(database: ReturnType<typeof createDatabase>, vehicleId: string | null, organizationId: string): Promise<number | null> {
  if (!vehicleId) return null
  const result = await database.select({ mileage: vehicles.mileage })
    .from(vehicles)
    .where(and(eq(vehicles.id, vehicleId), eq(vehicles.organizationId, organizationId)))
    .get()
  return result?.mileage ?? null
}

type MaintenanceItemRow = {
  id: string
  organizationId: string
  documentId: string
  itemType: string
  description: string
  quantity: number
  unit: string
  unitPrice: number
  technicalFee: number
  summary: string
  amount: number
}

function buildMaintenanceItemRows(items: MaintenanceItemInput[], fees: Record<FeeName, number>, adjustment: number, documentId: string, organizationId: string): MaintenanceItemRow[] {
  return [
    ...items.map((item) => ({
      id: crypto.randomUUID(),
      organizationId,
      documentId,
      itemType: item.kind,
      description: item.description,
      quantity: item.quantity,
      unit: item.unit,
      unitPrice: item.unitPrice,
      technicalFee: item.technicalFee,
      summary: item.summary,
      amount: item.amount,
    })),
    ...feeNames.map((name) => ({
      id: crypto.randomUUID(),
      organizationId,
      documentId,
      itemType: '法定費用',
      description: name,
      quantity: 1,
      unit: '式',
      unitPrice: fees[name],
      technicalFee: 0,
      summary: '',
      amount: fees[name],
    })),
    ...(adjustment === 0 ? [] : [{
      id: crypto.randomUUID(),
      organizationId,
      documentId,
      itemType: '調整',
      description: '調整額',
      quantity: 1,
      unit: '式',
      unitPrice: adjustment,
      technicalFee: 0,
      summary: '',
      amount: adjustment,
    }]),
  ]
}

function parseMaintenanceInput(body: Record<string, unknown>, database: ReturnType<typeof createDatabase>, organizationId: string): Promise<MaintenanceInput> {
  return parseMaintenanceInputAsync(body, database, organizationId)
}

async function parseMaintenanceInputAsync(body: Record<string, unknown>, database: ReturnType<typeof createDatabase>, organizationId: string): Promise<MaintenanceInput> {
  const type = stringValue(body, 'type') || '整備請求書'
  if (!maintenanceDocumentTypes.has(type)) throw new HttpError(400, '書類種別が不正です。')
  const status = stringValue(body, 'status') || '下書き'
  if (!maintenanceStatuses.has(status)) throw new HttpError(400, '整備書類ステータスが不正です。')
  const category = stringValue(body, 'category')
  if (!maintenanceCategories.has(category)) throw new HttpError(400, '入庫区分が不正です。')

  const customerId = stringValue(body, 'customerId')
  const customer = customerId ? await database.select({ id: customers.id }).from(customers).where(and(eq(customers.id, customerId), eq(customers.organizationId, organizationId))).get() : null
  if (!customer) throw new HttpError(400, '顧客を選択してください。')
  const vehicleId = stringValue(body, 'vehicleId')
  const vehicle = vehicleId ? await database.select({ id: vehicles.id, customerId: vehicles.customerId }).from(vehicles).where(and(eq(vehicles.id, vehicleId), eq(vehicles.organizationId, organizationId))).get() : null
  if (!vehicle || vehicle.customerId !== customerId) throw new HttpError(400, '選択した車両が顧客と一致しません。')

  const items = parseItems(body.items)
  const fees = parseFees(body.fees)
  const adjustment = integerNumber(body.adjustment, 0)
  const taxRate = parseTaxRate(body.taxRate)
  const rounding = body.rounding === '四捨五入' ? '四捨五入' : '切り捨て'
  return {
    number: nullableString(body, 'number'),
    type,
    status,
    category,
    customerId,
    vehicleId,
    intakeDate: nullableDate(body.intakeDate),
    plannedReleaseDate: nullableDate(body.plannedReleaseDate),
    completionDate: nullableDate(body.completionDate),
    issuedAt: dateValue(body.issuedAt) || today(),
    dueDate: nullableDate(body.dueDate),
    taxRate,
    rounding,
    note: nullableString(body, 'note'),
    details: parseMaintenanceDetails(body.details),
    items,
    fees,
    adjustment,
  }
}

function serializeMaintenanceDocument(document: typeof maintenanceDocuments.$inferSelect, customer: typeof customers.$inferSelect | undefined, vehicle: typeof vehicles.$inferSelect | undefined, rows: Array<typeof maintenanceItems.$inferSelect>) {
  const fees = extractFees(rows)
  const adjustment = extractAdjustment(rows)
  const items = rows.filter((item) => item.itemType === '作業' || item.itemType === '部品')
  return {
    id: document.id,
    number: document.number,
    type: document.type,
    status: normalizeMaintenanceStatus(document.status),
    category: document.category,
    customerId: document.customerId,
    customerName: customer?.name ?? '',
    phone: customer?.phone ?? '',
    customerDetails: {
      name: customer?.name ?? '',
      kana: customer?.nameKana ?? '',
      phone: customer?.phone ?? '',
      postalCode: customer?.postalCode ?? '',
      address: customer?.address ?? '',
    },
    vehicleId: document.vehicleId,
    vehicle: vehicle ? [vehicle.maker, vehicle.name].filter(Boolean).join(' ') : '',
    plate: vehicle?.registrationNumber ?? '',
    mileage: vehicle?.mileage === null || vehicle?.mileage === undefined ? '' : `${vehicle.mileage.toLocaleString('ja-JP')} km`,
    vehicleDetails: {
      maker: vehicle?.maker ?? '',
      name: vehicle?.name ?? '',
      modelType: vehicle?.model ?? '',
      plate: vehicle?.registrationNumber ?? '',
      vin: vehicle?.chassisNumber ?? '',
      year: vehicle?.modelYear === null || vehicle?.modelYear === undefined ? '' : String(vehicle.modelYear),
      inspectionDate: vehicle?.inspectionDate ?? '',
      mileage: vehicle?.mileage === null || vehicle?.mileage === undefined ? '' : `${vehicle.mileage.toLocaleString('ja-JP')}km`,
      color: vehicle?.bodyColor ?? '',
      displacement: vehicle?.displacement === null || vehicle?.displacement === undefined ? '' : String(vehicle.displacement),
      transmission: vehicle?.transmission ?? '',
      inspectionRecordAvailable: vehicle?.inspectionRecordAvailable ?? false,
    },
    intakeDate: document.intakeDate,
    plannedReleaseDate: document.plannedReleaseDate ?? document.completionDate,
    completionDate: document.completionDate,
    issuedAt: document.issuedAt,
    dueDate: document.dueDate,
    taxRate: document.taxRate,
    taxRounding: normalizeTaxRounding(document.taxRounding),
    subtotal: document.subtotal,
    tax: document.tax,
    total: document.total,
    fees,
    adjustment,
    note: document.note ?? '',
    details: parseMaintenanceDetails(parseDetailsJson(document.detailsJson)),
    archivedAt: document.archivedAt,
    archivedPreviousStatus: document.archivedPreviousStatus,
    archivedBy: document.archivedBy,
    purgeAt: document.purgeAt,
    keepForever: document.keepForever,
    items: items.map((item) => ({ id: item.id, kind: item.itemType === '部品' ? '部品' : '作業', description: item.description, quantity: item.quantity, unit: item.unit, unitPrice: item.unitPrice, technicalFee: item.technicalFee, summary: item.summary })),
  }
}

export function calculateMaintenanceTotals(items: MaintenanceItemInput[], fees: Record<FeeName, number>, adjustment: number, taxRate: number, rounding: '切り捨て' | '四捨五入') {
  const subtotal = items.reduce((sum, item) => sum + item.amount, 0)
  const taxableAmount = Math.max(0, subtotal)
  const taxValue = taxableAmount * taxRate / 100
  const tax = rounding === '四捨五入' ? Math.round(taxValue) : Math.floor(taxValue)
  const feesTotal = Object.values(fees).reduce((sum, fee) => sum + fee, 0) + adjustment
  return { subtotal, tax, total: subtotal + feesTotal + tax }
}

async function ensureMaintenanceDocumentNumberAvailable(database: ReturnType<typeof createDatabase>, number: string, organizationId: string, exceptId?: string) {
  const duplicate = await database.select({ id: maintenanceDocuments.id }).from(maintenanceDocuments).where(and(eq(maintenanceDocuments.organizationId, organizationId), eq(maintenanceDocuments.number, number))).get()
  if (duplicate && duplicate.id !== exceptId) throw new HttpError(409, '同じ書類番号の整備書類がすでに存在します。')
}

async function archiveMaintenanceDocument(env: Env, database: ReturnType<typeof createDatabase>, documentId: string, organizationId: string, userId: string) {
  const archived = await archiveDocumentFromRoute(database, 'maintenance', documentId, organizationId, userId)
  if (!archived) throw new HttpError(404, '整備書類が見つかりません。')
  return jsonResponse({ archived: true }, 200, env)
}

async function restoreMaintenanceDocument(env: Env, database: ReturnType<typeof createDatabase>, documentId: string, organizationId: string) {
  const restored = await restoreArchivedDocument(database, 'maintenance', documentId, organizationId)
  if (!restored) throw new HttpError(404, '整備書類が見つかりません。')
  return jsonResponse({ restored: true }, 200, env)
}

function normalizeMaintenanceStatus(status: string) {
  return status === '受付中' || status === '作業中' ? '下書き' : status
}

function normalizeTaxRounding(rounding: string | null | undefined): '切り捨て' | '四捨五入' {
  return rounding === '四捨五入' ? '四捨五入' : '切り捨て'
}

function parseItems(value: unknown): MaintenanceItemInput[] {
  if (!Array.isArray(value)) return []
  return value.filter((item): item is Record<string, unknown> => Boolean(item) && typeof item === 'object' && !Array.isArray(item)).map((item) => {
    const quantity = nonNegativeNumber(item.quantity, 1)
    const unitPrice = integerNumber(item.unitPrice, 0)
    const technicalFee = integerNumber(item.technicalFee, 0)
    return { kind: item.kind === '部品' ? '部品' : '作業', description: stringValue(item, 'description'), quantity, unit: stringValue(item, 'unit') || '式', unitPrice, technicalFee, summary: stringValue(item, 'summary'), amount: Math.round(quantity * unitPrice) + technicalFee }
  })
}

function parseFees(value: unknown): Record<FeeName, number> {
  const source = value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : {}
  return { 自賠責: nonNegativeInteger(source.自賠責, 0), 重量税: nonNegativeInteger(source.重量税, 0), 印紙代: nonNegativeInteger(source.印紙代, 0), リサイクル料金: nonNegativeInteger(source.リサイクル料金, 0) }
}

function extractFees(rows: Array<{ itemType: string; description: string; amount: number }>): Record<FeeName, number> {
  const fees = { 自賠責: 0, 重量税: 0, 印紙代: 0, リサイクル料金: 0 }
  for (const row of rows) if (row.itemType === '法定費用' && feeNames.includes(row.description as FeeName)) fees[row.description as FeeName] = row.amount
  return fees
}

function extractAdjustment(rows: Array<{ itemType: string; amount: number }>) {
  return rows.find((row) => row.itemType === '調整')?.amount ?? 0
}

function toInputItem(item: typeof maintenanceItems.$inferSelect): Record<string, unknown> {
  return { kind: item.itemType, description: item.description, quantity: item.quantity, unit: item.unit, unitPrice: item.unitPrice, technicalFee: item.technicalFee, summary: item.summary }
}

function groupBy<T>(items: T[], getKey: (item: T) => string) {
  const grouped = new Map<string, T[]>()
  for (const item of items) grouped.set(getKey(item), [...(grouped.get(getKey(item)) ?? []), item])
  return grouped
}

function stringValue(body: Record<string, unknown>, key: string) { return typeof body[key] === 'string' ? body[key].trim() : '' }
function nullableString(body: Record<string, unknown>, key: string) { const value = stringValue(body, key); return value || null }
function dateValue(value: unknown) { return typeof value === 'string' && /^\d{4}[-/]\d{2}[-/]\d{2}$/.test(value.trim()) ? value.trim().replaceAll('/', '-') : '' }
function nullableDate(value: unknown) { return dateValue(value) || null }
function parseTaxRate(value: unknown) { const number = typeof value === 'number' ? value : Number(value); const normalized = number > 0 && number < 1 ? number * 100 : number; return Number.isFinite(normalized) && normalized >= 0 && normalized <= 100 ? Math.round(normalized) : 10 }
function nonNegativeNumber(value: unknown, fallback: number) { const number = typeof value === 'number' ? value : Number(value); return Number.isFinite(number) && number >= 0 ? number : fallback }
function integerNumber(value: unknown, fallback: number) { const number = typeof value === 'number' ? value : Number(value); return Number.isFinite(number) ? Math.round(number) : fallback }
function nonNegativeInteger(value: unknown, fallback: number) { return Math.max(0, integerNumber(value, fallback)) }
function today() { return new Date().toISOString().slice(0, 10) }

function parseDetailsJson(value: string | null) {
  if (!value) return {}
  try {
    const parsed = JSON.parse(value)
    return parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? parsed : {}
  } catch {
    return {}
  }
}

function parseMaintenanceDetails(value: unknown): MaintenanceDetails {
  const source = recordValue(value)
  const customerOverride = recordValue(source.customerOverride)
  const vehicleOverride = recordValue(source.vehicleOverride)
  const labels = recordValue(source.labels)
  const normalizedCustomerOverride = {
    name: stringValue(customerOverride, 'name'),
    kana: stringValue(customerOverride, 'kana'),
    phone: stringValue(customerOverride, 'phone'),
    postalCode: stringValue(customerOverride, 'postalCode'),
    address: stringValue(customerOverride, 'address'),
  }
  const normalizedVehicleOverride = {
    maker: stringValue(vehicleOverride, 'maker'),
    name: stringValue(vehicleOverride, 'name'),
    modelType: stringValue(vehicleOverride, 'modelType'),
    plate: stringValue(vehicleOverride, 'plate'),
    vin: stringValue(vehicleOverride, 'vin'),
    year: stringValue(vehicleOverride, 'year'),
    inspectionDate: stringValue(vehicleOverride, 'inspectionDate'),
    mileage: stringValue(vehicleOverride, 'mileage'),
    color: stringValue(vehicleOverride, 'color'),
    displacement: stringValue(vehicleOverride, 'displacement'),
    transmission: stringValue(vehicleOverride, 'transmission'),
    inspectionRecordAvailable: typeof vehicleOverride.inspectionRecordAvailable === 'boolean' ? vehicleOverride.inspectionRecordAvailable : false,
  }
  return {
    staffName: stringValue(source, 'staffName'),
    customerHonorific: stringValue(source, 'customerHonorific') || '様',
    customerBirthDate: stringValue(source, 'customerBirthDate'),
    customerEmployer: stringValue(source, 'customerEmployer'),
    customerContactPhone: stringValue(source, 'customerContactPhone'),
    bankName: stringValue(source, 'bankName'),
    bankAccount: stringValue(source, 'bankAccount'),
    bankAccountHolder: stringValue(source, 'bankAccountHolder'),
    customerOverride: hasOverrideValue(normalizedCustomerOverride) ? normalizedCustomerOverride : null,
    vehicleOverride: hasOverrideValue(normalizedVehicleOverride) ? normalizedVehicleOverride : null,
    labels: {
      documentTitle: '',
      amountTitle: 'お見積金額（税込）',
      vehicleSectionTitle: stringValue(labels, 'vehicleSectionTitle') || '車両情報',
      workSectionTitle: '作業内容／部品名等',
      bankTitle: stringValue(labels, 'bankTitle') || 'お振込先',
      otherFee: stringValue(labels, 'otherFee') || 'その他',
    },
  }
}

function hasOverrideValue(value: Record<string, string | boolean>) {
  return Object.values(value).some((field) => typeof field === 'string' ? field.length > 0 : field)
}

function recordValue(value: unknown): Record<string, unknown> {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : {}
}

export type MaintenanceItemInput = { kind: '作業' | '部品'; description: string; quantity: number; unit: string; unitPrice: number; technicalFee: number; summary: string; amount: number }
type MaintenanceDetails = {
  staffName: string
  customerHonorific: string
  customerBirthDate: string
  customerEmployer: string
  customerContactPhone: string
  bankName: string
  bankAccount: string
  bankAccountHolder: string
  customerOverride: Record<string, string> | null
  vehicleOverride: Record<string, string | boolean> | null
  labels: Record<string, string>
}
type MaintenanceInput = { number: string | null; type: string; status: string; category: string; customerId: string; vehicleId: string; intakeDate: string | null; plannedReleaseDate: string | null; completionDate: string | null; issuedAt: string; dueDate: string | null; taxRate: number; rounding: '切り捨て' | '四捨五入'; note: string | null; details: MaintenanceDetails; items: MaintenanceItemInput[]; fees: Record<FeeName, number>; adjustment: number }
