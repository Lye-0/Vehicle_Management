import { and, asc, desc, eq, gt, isNull, like, lt, or, sql } from 'drizzle-orm'
import { customers, maintenanceItems, maintenanceDocuments, mileageHistories, vehicles } from '@vehicle-management/database'
import { normalizeDisplacement, normalizeMileage, normalizeModelYear, normalizePhone, normalizePostalCode } from '@vehicle-management/shared'
import { UnauthorizedError } from '../auth/firebase'
import { requireOrganizationContext } from '../auth/organization'
import { createDatabase } from '../db/client'
import { restoreArchivedDocument } from '../document-archive'
import { archiveDocumentFromRoute } from './archive-routes'
import { nextDocumentNumber } from '../document-number'
import { HttpError, jsonResponse, readJson } from '../http'
import { normalizeCalendarDate } from '../lib/date-utils'
import { parseAbacusDocumentImportMetadata } from '../lib/abacus-document-metadata'
import { parseAbacusDetailEnvelope } from '../lib/abacus-detail-metadata'
import { assertArrayLength, assertD1BatchStatementCount, maximumDocumentItemCount } from '../lib/resource-limits'
import {
  CUSTOMER_FIELD_TO_DB_COLUMN,
  CUSTOMER_SYNC_ALLOWLIST,
  VEHICLE_FIELD_TO_DB_COLUMN,
  VEHICLE_SYNC_ALLOWLIST,
  buildCustomerUpdateValues,
  buildVehicleUpdateValues,
  computeActualCustomerDiffFields,
  computeActualVehicleDiffFields,
  findDuplicateCustomers,
  findDuplicateVehicles,
  normalizeCustomerBirthDateForStorage,
  validateCombination,
  validateMasterSyncInput,
  type CustomerSyncField,
  type NewCustomerInput,
  type NewVehicleInput,
  type VehicleSyncField,
} from '../lib/master-sync-helpers'

const maintenanceDocumentTypes = new Set(['整備見積書', '整備請求書'])
const maintenanceStatuses = new Set(['下書き', '入金待ち', '完了', 'アーカイブ済み'])
const maintenanceCategories = new Set(['車検', '板金', '一般整備'])
const feeNames = ['自賠責', '重量税', '印紙代', 'リサイクル料金'] as const
type FeeName = typeof feeNames[number]

type MileageSync = {
  confirmed: true
  openedMileage: number | null
  inputMileage: number
}

function parseMileageSync(body: Record<string, unknown>): MileageSync | undefined {
  const raw = body.mileageSync
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return undefined
  const record = raw as Record<string, unknown>
  if (record.confirmed !== true) return undefined
  const openedMileageRaw = record.openedMileage
  const openedMileage = openedMileageRaw === null || openedMileageRaw === undefined ? null : integerNumber(openedMileageRaw, -1)
  const inputMileage = integerNumber(record.inputMileage, -1)
  if (openedMileage !== null && openedMileage < 0) return undefined
  if (inputMileage < 0) return undefined
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

    if (request.method === 'GET') return await getMaintenanceDocument(env, database, documentMatch![1], organizationId)
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
  const url = new URL(request.url)
  if (url.searchParams.get('view') === 'summary') return await listMaintenanceDocumentSummaries(url, env, database, organizationId)
  return jsonResponse({ documents: await loadMaintenanceDocuments(database, organizationId, new URL(request.url).searchParams.get('includeArchived') === 'true') }, 200, env)
}

async function listMaintenanceDocumentSummaries(url: URL, env: Env, database: ReturnType<typeof createDatabase>, organizationId: string) {
  const limit = Math.min(Math.max(Number(url.searchParams.get('limit') ?? 50) || 50, 1), 100)
  const q = url.searchParams.get('q')?.trim() ?? ''
  const type = url.searchParams.get('type')?.trim() ?? ''
  const category = url.searchParams.get('category')?.trim() ?? ''
  const status = url.searchParams.get('status')?.trim() ?? ''
  const includeArchived = url.searchParams.get('includeArchived') === 'true'
  const sortKey = normalizeSummarySortKey(url.searchParams.get('sortKey'))
  const sortDirection = normalizeSummarySortDirection(url.searchParams.get('sortDirection'))
  const cursor = decodeMaintenanceCursor(url.searchParams.get('cursor'))
  const conditions = [eq(maintenanceDocuments.organizationId, organizationId)]
  if (!includeArchived) conditions.push(isNull(maintenanceDocuments.archivedAt))
  if (type && type !== 'すべて') conditions.push(eq(maintenanceDocuments.type, type))
  if (category && category !== 'すべて') conditions.push(eq(maintenanceDocuments.category, category === '法定点検' ? '一般整備' : category))
  if (status && status !== 'すべて') conditions.push(eq(maintenanceDocuments.status, status))
  const sortExpression = maintenanceSortExpression(sortKey, sortDirection)
  if (cursor && cursor.sortKey === sortKey && cursor.sortDirection === sortDirection) {
    const after = sortDirection === 'asc' ? gt(sortExpression, cursor.value) : lt(sortExpression, cursor.value)
    const same = and(eq(sortExpression, cursor.value), sortDirection === 'asc' ? gt(maintenanceDocuments.id, cursor.id) : lt(maintenanceDocuments.id, cursor.id))
    conditions.push(or(after, same)!)
  }
  if (q) {
    const pattern = `%${q.replace(/[\\%_]/g, '\\$&')}%`
    conditions.push(or(like(maintenanceDocuments.number, pattern), like(customers.name, pattern), like(vehicles.name, pattern), like(vehicles.registrationNumber, pattern))!)
  }
  const rows = await database.select({ document: maintenanceDocuments, customerName: customers.name, customerPhone: customers.phone, vehicleMaker: vehicles.maker, vehicleName: vehicles.name, plate: vehicles.registrationNumber })
    .from(maintenanceDocuments)
    .leftJoin(customers, and(eq(customers.id, maintenanceDocuments.customerId), eq(customers.organizationId, organizationId)))
    .leftJoin(vehicles, and(eq(vehicles.id, maintenanceDocuments.vehicleId), eq(vehicles.organizationId, organizationId)))
    .where(and(...conditions))
    .orderBy(sortDirection === 'asc' ? asc(sortExpression) : desc(sortExpression), sortDirection === 'asc' ? asc(maintenanceDocuments.id) : desc(maintenanceDocuments.id))
    .limit(limit + 1)
    .all()
  const hasMore = rows.length > limit
  const items = rows.slice(0, limit).map(({ document, customerName, customerPhone, vehicleMaker, vehicleName, plate }) => serializeMaintenanceDocumentSummary(document, customerName ?? '', customerPhone ?? '', vehicleMaker, vehicleName, plate))
  const lastRow = rows[Math.min(rows.length, limit) - 1]
  return jsonResponse({ documents: items, nextCursor: hasMore && lastRow ? encodeMaintenanceCursor({ sortKey, sortDirection, value: maintenanceSortValue(lastRow.document, lastRow.customerName, lastRow.vehicleMaker, lastRow.vehicleName, sortKey, sortDirection), id: lastRow.document.id }) : null, hasMore }, 200, env)
}

type SummarySortKey = 'issuedAt' | 'dueDate' | 'customerName' | 'vehicle'
type SummarySortDirection = 'asc' | 'desc'
type MaintenanceCursor = { sortKey: SummarySortKey; sortDirection: SummarySortDirection; value: string; id: string }

function normalizeSummarySortKey(value: string | null): SummarySortKey {
  return value === 'dueDate' || value === 'customerName' || value === 'vehicle' ? value : 'issuedAt'
}

function normalizeSummarySortDirection(value: string | null): SummarySortDirection {
  return value === 'asc' ? 'asc' : 'desc'
}

function maintenanceSortExpression(sortKey: SummarySortKey, sortDirection: SummarySortDirection) {
  const emptyValue = sortDirection === 'asc' ? '\uffff' : ''
  if (sortKey === 'dueDate') return sql`COALESCE(${maintenanceDocuments.dueDate}, ${emptyValue})`
  if (sortKey === 'customerName') return sql`COALESCE(${customers.name}, ${emptyValue})`
  if (sortKey === 'vehicle') return sql`COALESCE(NULLIF(TRIM(COALESCE(${vehicles.maker}, '') || ' ' || COALESCE(${vehicles.name}, '')), ''), ${emptyValue})`
  return sql`COALESCE(${maintenanceDocuments.issuedAt}, ${emptyValue})`
}

function maintenanceSortValue(document: typeof maintenanceDocuments.$inferSelect, customerName: string | null | undefined, vehicleMaker: string | null | undefined, vehicleName: string | null | undefined, sortKey: SummarySortKey, sortDirection: SummarySortDirection) {
  const raw = sortKey === 'dueDate' ? document.dueDate : sortKey === 'customerName' ? customerName : sortKey === 'vehicle' ? [vehicleMaker, vehicleName].filter(Boolean).join(' ') : document.issuedAt
  return raw || (sortDirection === 'asc' ? '\uffff' : '')
}

function encodeMaintenanceCursor(value: MaintenanceCursor) {
  return btoa(JSON.stringify(value)).replaceAll('+', '-').replaceAll('/', '_').replace(/=+$/, '')
}

function decodeMaintenanceCursor(value: string | null): MaintenanceCursor | null {
  if (!value) return null
  try {
    const padded = value.replaceAll('-', '+').replaceAll('_', '/') + '='.repeat((4 - value.length % 4) % 4)
    const parsed = JSON.parse(atob(padded)) as { sortKey?: unknown; sortDirection?: unknown; value?: unknown; id?: unknown; issuedAt?: unknown; number?: unknown }
    if (typeof parsed.sortKey === 'string' && typeof parsed.sortDirection === 'string' && typeof parsed.value === 'string' && typeof parsed.id === 'string') {
      const sortKey = normalizeSummarySortKey(parsed.sortKey)
      const sortDirection = normalizeSummarySortDirection(parsed.sortDirection)
      return { sortKey, sortDirection, value: parsed.value, id: parsed.id }
    }
    if (typeof parsed.issuedAt === 'string' && typeof parsed.number === 'string') return { sortKey: 'issuedAt', sortDirection: 'desc', value: parsed.issuedAt, id: parsed.number }
    return null
  } catch { return null }
}

function serializeMaintenanceDocumentSummary(document: typeof maintenanceDocuments.$inferSelect, customerName: string, phone: string, vehicleMaker: string | null | undefined, vehicleName: string | null | undefined, plate: string | null | undefined) {
  const abacusImport = parseAbacusDocumentImportMetadata(document.detailsJson)
  const abacusDetails = parseAbacusDetailEnvelope(document.detailsJson)
  return {
    id: document.id,
    updatedAt: document.updatedAt,
    number: document.number,
    type: document.type,
    status: normalizeMaintenanceStatus(document.status),
    category: document.category,
    customerId: document.customerId,
    customerName,
    phone,
    customerDetails: null,
    vehicleId: document.vehicleId,
    vehicle: vehicleMaker || vehicleName ? [vehicleMaker, vehicleName].filter(Boolean).join(' ') : 'なし',
    plate: plate ?? '',
    abacusImport,
    isAbacusMigration: abacusDetails?.isAbacusMigration ?? false,
    mileage: '',
    vehicleDetails: null,
    details: null,
    intakeDate: document.intakeDate,
    plannedReleaseDate: document.plannedReleaseDate,
    completionDate: document.completionDate,
    issuedAt: document.issuedAt,
    dueDate: document.dueDate,
    taxRate: document.taxRate,
    taxRounding: document.taxRounding,
    fees: { 自賠責: 0, 重量税: 0, 印紙代: 0, リサイクル料金: 0 },
    adjustment: 0,
    note: document.note,
    archivedAt: document.archivedAt,
    archivedPreviousStatus: document.archivedPreviousStatus,
    archivedBy: document.archivedBy,
    purgeAt: document.purgeAt,
    keepForever: document.keepForever,
    subtotal: document.subtotal,
    tax: document.tax,
    total: document.total,
    items: [],
    summary: true,
  }
}

async function getMaintenanceDocument(env: Env, database: ReturnType<typeof createDatabase>, documentId: string, organizationId: string) {
  const document = await findMaintenanceDocument(database, decodeURIComponent(documentId), organizationId)
  if (!document) throw new HttpError(404, '整備書類が見つかりません。')
  return jsonResponse({ document }, 200, env)
}

// ===================== POST (新規作成) =====================

async function createMaintenanceDocument(request: Request, env: Env, database: ReturnType<typeof createDatabase>, organizationId: string) {
  const body = await readJson(request)
  const mileageSync = parseMileageSync(body)
  const masterSyncRaw = body.masterSync

  // 新規顧客・新規車両の解析
  const newCustomer = parseNewCustomer(body.newCustomer)
  const newVehicle = parseNewVehicle(body.newVehicle)
  const customerId = stringValue(body, 'customerId') || undefined
  const vehicleId = stringValue(body, 'vehicleId') || undefined

  // 排他的入力検証
  const combinationError = validateCombination({ customerId, newCustomer, vehicleId, newVehicle, documentType: 'maintenance' })
  if (combinationError) throw new HttpError(combinationError.status, combinationError.message)

  // masterSyncの検証
  let masterSync: ReturnType<typeof validateMasterSyncInput> | undefined
  if (masterSyncRaw !== undefined) {
    const result = validateMasterSyncInput(masterSyncRaw)
    if ('error' in result) throw new HttpError(400, result.error)
    masterSync = result

    // 新規顧客にcustomerFields指定は拒否
    if (newCustomer && masterSync.customerFields.length > 0) {
      throw new HttpError(400, '新規顧客にはcustomerFieldsを指定できません。')
    }
    // 新規車両にvehicleFields指定は拒否
    if (newVehicle && masterSync.vehicleFields.length > 0) {
      throw new HttpError(400, '新規車両にはvehicleFieldsを指定できません。')
    }
  }

  // 重複検出（新規車両）
  if (newVehicle) {
    const allVehicles = await database
      .select({ id: vehicles.id, maker: vehicles.maker, name: vehicles.name, registrationNumber: vehicles.registrationNumber, chassisNumber: vehicles.chassisNumber })
      .from(vehicles)
      .where(and(eq(vehicles.organizationId, organizationId), isNull(vehicles.deletedAt)))
      .all()
    const duplicateVehicles = findDuplicateVehicles(allVehicles, newVehicle)

    // 車台番号完全一致 → 拒否
    const vinDuplicate = duplicateVehicles.find((d) => d.matchReason === 'chassis_number')
    if (vinDuplicate) {
      throw new HttpError(409, `車台番号が既存車両（${vinDuplicate.maker ?? ''} ${vinDuplicate.name}）と一致します。既存車両を選択してください。`)
    }

    // 登録番号一致 → 確認が必要
    const plateDuplicates = duplicateVehicles.filter((d) => d.matchReason === 'registration_number')
    if (plateDuplicates.length > 0) {
      const dupConfirm = body.duplicateConfirmation
      if (!dupConfirm || typeof dupConfirm !== 'object' || Array.isArray(dupConfirm)) {
        throw new HttpError(409, '登録番号が既存車両と一致します。重複確認が必要です。')
      }
      const confirmObj = dupConfirm as Record<string, unknown>
      if (confirmObj.registrationNumberConfirmed !== true) {
        throw new HttpError(409, '登録番号の重複確認が完了していません。')
      }
      // 確認した候補IDが実際に検出された候補と一致するか検証
      const confirmedId = typeof confirmObj.confirmedVehicleId === 'string' ? confirmObj.confirmedVehicleId : undefined
      if (!confirmedId || !plateDuplicates.some((candidate) => candidate.id === confirmedId)) {
        throw new HttpError(400, '確認された車両IDが現在の重複候補と一致しません。')
      }
    }
  }

  // 重複検出（新規顧客）— 警告のみ、作成は許可
  if (newCustomer) {
    const allCustomers = await database
      .select({ id: customers.id, name: customers.name, phone: customers.phone, email: customers.email })
      .from(customers)
      .where(and(eq(customers.organizationId, organizationId), isNull(customers.deletedAt)))
      .all()
    // 結果はレスポンスには含めない（作成をブロックしない）
    void findDuplicateCustomers(allCustomers, newCustomer)
  }

  const input = await parseMaintenanceInput(body, database, organizationId, customerId, vehicleId, newCustomer, newVehicle)

  // mileageSync検証
  if (mileageSync) {
    if (mileageSync.openedMileage === mileageSync.inputMileage) {
      throw new HttpError(400, '走行距離が変更されていないため記録できません。')
    }
    const overrideMileage = parseMileageValue(input.details.vehicleOverride?.mileage)
    if (mileageSync.inputMileage !== overrideMileage) {
      throw new HttpError(400, '走行距離が書類内容と一致しません。')
    }
  } else if (!newVehicle) {
    const inputMileage = parseMileageValue(input.details.vehicleOverride?.mileage)
    const currentVehicleMileage = await getCurrentVehicleMileage(database, input.vehicleId, organizationId)
    if (inputMileage !== null && inputMileage !== currentVehicleMileage) {
      throw new HttpError(400, '走行距離が変更されていますが確認が送信されていません。')
    }
  }

  // masterSyncの実差分再計算（既存顧客・既存車両の場合）
  let actualCustomerDiffFields: Set<CustomerSyncField> | undefined
  let actualVehicleDiffFields: Set<VehicleSyncField> | undefined
  let currentCustomerForSync: typeof customers.$inferSelect | null = null
  let currentVehicleForSync: typeof vehicles.$inferSelect | null = null

  if (masterSync && !newCustomer) {
    currentCustomerForSync = await database.select().from(customers).where(and(eq(customers.id, input.customerId), eq(customers.organizationId, organizationId), isNull(customers.deletedAt))).get() ?? null
    actualCustomerDiffFields = computeActualCustomerDiffFields(currentCustomerForSync, input.details.customerOverride ?? null)
    // 部分集合チェック
    for (const f of masterSync.customerFields) {
      if (!actualCustomerDiffFields.has(f)) {
        throw new HttpError(400, `顧客フィールド「${f}」は現在値と差分がないか、空欄です。`)
      }
    }
  }

  if (masterSync && !newVehicle) {
    currentVehicleForSync = await database.select().from(vehicles).where(and(eq(vehicles.id, input.vehicleId), eq(vehicles.organizationId, organizationId), isNull(vehicles.deletedAt))).get() ?? null
    actualVehicleDiffFields = computeActualVehicleDiffFields(currentVehicleForSync, input.details.vehicleOverride ?? null)
    for (const f of masterSync.vehicleFields) {
      if (!actualVehicleDiffFields.has(f)) {
        throw new HttpError(400, `車両フィールド「${f}」は現在値と差分がないか、空欄です。`)
      }
    }
  }

  // updatedAt競合検証
  if (masterSync) {
    if (masterSync.expectedCustomerUpdatedAt && currentCustomerForSync) {
      if (currentCustomerForSync.updatedAt !== masterSync.expectedCustomerUpdatedAt) {
        throw new HttpError(409, '顧客の情報が更新されました。再読み込みしてください。')
      }
    }
    if (masterSync.expectedVehicleUpdatedAt && currentVehicleForSync) {
      if (currentVehicleForSync.updatedAt !== masterSync.expectedVehicleUpdatedAt) {
        throw new HttpError(409, '車両の情報が更新されました。再読み込みしてください。')
      }
    }
  }

  const docId = crypto.randomUUID()
  const number = await nextDocumentNumber(env.DB, organizationId, 'M')
  await ensureMaintenanceDocumentNumberAvailable(database, number, organizationId)
  const totals = calculateMaintenanceTotals(input.items, input.fees, input.adjustment, input.taxRate, input.rounding)
  const now = new Date().toISOString()

  const itemRows = buildMaintenanceItemRows(input.items, input.fees, input.adjustment, docId, organizationId)

  const statements: D1PreparedStatement[] = []

  // 新規顧客INSERT
  let resolvedCustomerId = input.customerId
  if (newCustomer) {
    const newCustId = crypto.randomUUID()
    resolvedCustomerId = newCustId
    statements.push(env.DB.prepare(
      `INSERT INTO customers (id, organization_id, customer_number, name, name_kana, postal_code, address, phone, email, birth_date, employer)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`
    ).bind(newCustId, organizationId, `C-${crypto.randomUUID().slice(0, 8).toUpperCase()}`,
      newCustomer.name, newCustomer.nameKana || null, newCustomer.postalCode || null,
      newCustomer.address || null, newCustomer.phone || null, newCustomer.email || null,
      newCustomer.birthDate || null, newCustomer.employer || null))
  }

  // 新規車両INSERT
  let resolvedVehicleId = input.vehicleId
  if (newVehicle) {
    const newVehId = crypto.randomUUID()
    resolvedVehicleId = newVehId
    statements.push(env.DB.prepare(
      `INSERT INTO vehicles (id, organization_id, customer_id, maker, name, model, registration_number, chassis_number, model_year, inspection_date, mileage, body_color, displacement, transmission)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`
    ).bind(newVehId, organizationId, resolvedCustomerId, newVehicle.maker, newVehicle.name,
      newVehicle.model || null, newVehicle.registrationNumber || null, newVehicle.chassisNumber || null,
      newVehicle.modelYear || null, newVehicle.inspectionDate || null, newVehicle.mileage != null ? newVehicle.mileage : null,
      newVehicle.bodyColor || null, newVehicle.displacement || null, newVehicle.transmission || null))
  }

  // 既存顧客マスタUPDATE
  if (masterSync && masterSync.customerFields.length > 0 && !newCustomer && currentCustomerForSync) {
    const updateValues = buildCustomerUpdateValues(masterSync.customerFields, input.details.customerOverride ?? null)
    const setEntries = Object.entries(updateValues)
    if (setEntries.length > 0) {
      const setClause = setEntries.map(([col]) => `${col} = ?`).join(', ')
      const bindValues = [...setEntries.map(([, v]) => v), now, input.customerId, organizationId]
      statements.push(env.DB.prepare(
        `UPDATE customers SET ${setClause}, updated_at = ? WHERE id = ? AND organization_id = ?`
      ).bind(...bindValues))
    }
  }

  // 既存車両マスタUPDATE（mileage除く）
  if (masterSync && masterSync.vehicleFields.length > 0 && !newVehicle && currentVehicleForSync) {
    const updateValues = buildVehicleUpdateValues(masterSync.vehicleFields, input.details.vehicleOverride ?? null)
    const setEntries = Object.entries(updateValues)
    if (setEntries.length > 0) {
      const setClause = setEntries.map(([col]) => `${col} = ?`).join(', ')
      const bindValues = [...setEntries.map(([, v]) => v), now, input.vehicleId, organizationId]
      statements.push(env.DB.prepare(
        `UPDATE vehicles SET ${setClause}, updated_at = ? WHERE id = ? AND organization_id = ?`
      ).bind(...bindValues))
    }
  }

  // 書類INSERT
  statements.push(env.DB.prepare(
    `INSERT INTO maintenance_documents
      (id, organization_id, number, type, category, status, customer_id, vehicle_id,
       intake_date, planned_release_date, completion_date, issued_at, due_date,
       tax_rate, tax_rounding, subtotal, tax, total, note, details_json)
     VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`
  ).bind(
    docId, organizationId, number, input.type, input.category, input.status,
    resolvedCustomerId, resolvedVehicleId, input.intakeDate, input.plannedReleaseDate,
    input.completionDate, input.issuedAt, input.dueDate,
    input.taxRate, input.rounding, totals.subtotal, totals.tax, totals.total,
    input.note, JSON.stringify(input.details)
  ))

  // 明細INSERT
  for (const item of itemRows) {
    statements.push(env.DB.prepare(
      `INSERT INTO maintenance_items
        (id, organization_id, document_id, item_type, description, quantity, unit, unit_price, technical_fee, summary, amount, sort_order)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`
    ).bind(item.id, organizationId, docId, item.itemType, item.description, item.quantity, item.unit, item.unitPrice, item.technicalFee, item.summary, item.amount, itemRows.indexOf(item)))
  }

  // mileageSync
  if (mileageSync) {
    statements.push(env.DB.prepare(
      `INSERT INTO mileage_histories
        (id, organization_id, vehicle_id, maintenance_document_id, mileage, created_at, updated_at)
       VALUES (?, ?, ?, ?, ?, ?, ?)
       ON CONFLICT (organization_id, maintenance_document_id)
       DO UPDATE SET mileage = excluded.mileage, vehicle_id = excluded.vehicle_id, updated_at = excluded.updated_at`
    ).bind(crypto.randomUUID(), organizationId, resolvedVehicleId, docId, mileageSync.inputMileage, now, now))
    statements.push(env.DB.prepare(
      `UPDATE vehicles SET mileage = ?, updated_at = ?
       WHERE id = ? AND organization_id = ? AND (mileage IS NULL OR mileage < ?)`
    ).bind(mileageSync.inputMileage, now, resolvedVehicleId, organizationId, mileageSync.inputMileage))
  }

  assertD1BatchStatementCount(statements.length)
  await env.DB.batch(statements)

  return jsonResponse({ document: await findMaintenanceDocument(database, docId, organizationId) }, 201, env)
}

// ===================== PATCH (更新) =====================

async function updateMaintenanceDocument(request: Request, env: Env, database: ReturnType<typeof createDatabase>, documentId: string, organizationId: string) {
  const current = await database.select().from(maintenanceDocuments).where(and(eq(maintenanceDocuments.id, documentId), eq(maintenanceDocuments.organizationId, organizationId))).get()
  if (!current) throw new HttpError(404, '整備書類が見つかりません。')

  const currentItems = await loadMaintenanceItems(database, documentId, organizationId)
  const body = await readJson(request)
  if (typeof body.expectedUpdatedAt === 'string' && body.expectedUpdatedAt !== current.updatedAt) throw new HttpError(409, '整備書類が他の端末で更新されています。再読み込みしてください。')
  const requestedVehicleId = body.vehicleId === undefined ? current.vehicleId : nullableString(body, 'vehicleId')
  if (current.vehicleId && !requestedVehicleId) {
    throw new HttpError(400, '通常の整備書類から車両を外すことはできません。車両なしはABACUS互換書類だけに対応しています。')
  }
  const mileageSync = parseMileageSync(body)
  const masterSyncRaw = body.masterSync

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

  // mileageSync検証
  if (mileageSync) {
    if (mileageSync.openedMileage === mileageSync.inputMileage) {
      throw new HttpError(400, '走行距離が変更されていないため記録できません。')
    }
    const overrideMileage = parseMileageValue(input.details.vehicleOverride?.mileage)
    if (mileageSync.inputMileage !== overrideMileage) {
      throw new HttpError(400, '走行距離が書類内容と一致しません。')
    }
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

  // masterSync検証
  let masterSync: ReturnType<typeof validateMasterSyncInput> | undefined
  if (masterSyncRaw !== undefined) {
    const result = validateMasterSyncInput(masterSyncRaw)
    if ('error' in result) throw new HttpError(400, result.error)
    masterSync = result
  }

  // 実差分再計算
  let actualCustomerDiffFields: Set<CustomerSyncField> | undefined
  let actualVehicleDiffFields: Set<VehicleSyncField> | undefined
  let currentCustomerForSync: typeof customers.$inferSelect | null = null
  let currentVehicleForSync: typeof vehicles.$inferSelect | null = null

  if (masterSync && masterSync.customerFields.length > 0) {
    currentCustomerForSync = await database.select().from(customers).where(and(eq(customers.id, input.customerId), eq(customers.organizationId, organizationId), isNull(customers.deletedAt))).get() ?? null
    actualCustomerDiffFields = computeActualCustomerDiffFields(currentCustomerForSync, input.details.customerOverride ?? null)
    for (const f of masterSync.customerFields) {
      if (!actualCustomerDiffFields.has(f)) {
        throw new HttpError(400, `顧客フィールド「${f}」は現在値と差分がないか、空欄です。`)
      }
    }
  }

  if (masterSync && masterSync.vehicleFields.length > 0) {
    currentVehicleForSync = await database.select().from(vehicles).where(and(eq(vehicles.id, input.vehicleId), eq(vehicles.organizationId, organizationId), isNull(vehicles.deletedAt))).get() ?? null
    actualVehicleDiffFields = computeActualVehicleDiffFields(currentVehicleForSync, input.details.vehicleOverride ?? null)
    for (const f of masterSync.vehicleFields) {
      if (!actualVehicleDiffFields.has(f)) {
        throw new HttpError(400, `車両フィールド「${f}」は現在値と差分がないか、空欄です。`)
      }
    }
  }

  // updatedAt競合検証
  if (masterSync) {
    if (masterSync.expectedCustomerUpdatedAt && currentCustomerForSync) {
      if (currentCustomerForSync.updatedAt !== masterSync.expectedCustomerUpdatedAt) {
        throw new HttpError(409, '顧客の情報が更新されました。再読み込みしてください。')
      }
    }
    if (masterSync.expectedVehicleUpdatedAt && currentVehicleForSync) {
      if (currentVehicleForSync.updatedAt !== masterSync.expectedVehicleUpdatedAt) {
        throw new HttpError(409, '車両の情報が更新されました。再読み込みしてください。')
      }
    }
  }

  const totals = calculateMaintenanceTotals(input.items, input.fees, input.adjustment, input.taxRate, input.rounding)
  const number = input.number || current.number
  await ensureMaintenanceDocumentNumberAvailable(database, number, organizationId, documentId)
  const now = new Date().toISOString()

  const itemRows = buildMaintenanceItemRows(input.items, input.fees, input.adjustment, documentId, organizationId)

  const statements: D1PreparedStatement[] = []

  // 顧客マスタUPDATE
  if (masterSync && masterSync.customerFields.length > 0 && currentCustomerForSync) {
    const updateValues = buildCustomerUpdateValues(masterSync.customerFields, input.details.customerOverride ?? null)
    const setEntries = Object.entries(updateValues)
    if (setEntries.length > 0) {
      const setClause = setEntries.map(([col]) => `${col} = ?`).join(', ')
      const bindValues = [...setEntries.map(([, v]) => v), now, input.customerId, organizationId]
      statements.push(env.DB.prepare(
        `UPDATE customers SET ${setClause}, updated_at = ? WHERE id = ? AND organization_id = ?`
      ).bind(...bindValues))
    }
  }

  // 車両マスタUPDATE（mileage除く）
  if (masterSync && masterSync.vehicleFields.length > 0 && currentVehicleForSync) {
    const updateValues = buildVehicleUpdateValues(masterSync.vehicleFields, input.details.vehicleOverride ?? null)
    const setEntries = Object.entries(updateValues)
    if (setEntries.length > 0) {
      const setClause = setEntries.map(([col]) => `${col} = ?`).join(', ')
      const bindValues = [...setEntries.map(([, v]) => v), now, input.vehicleId, organizationId]
      statements.push(env.DB.prepare(
        `UPDATE vehicles SET ${setClause}, updated_at = ? WHERE id = ? AND organization_id = ?`
      ).bind(...bindValues))
    }
  }

  // 書類UPDATE
  statements.push(env.DB.prepare(
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
  ))

  // 明細DELETE + INSERT
  statements.push(env.DB.prepare(
    `DELETE FROM maintenance_items WHERE document_id = ? AND organization_id = ?`
  ).bind(documentId, organizationId))
  for (const item of itemRows) {
    statements.push(env.DB.prepare(
      `INSERT INTO maintenance_items
        (id, organization_id, document_id, item_type, description, quantity, unit, unit_price, technical_fee, summary, amount, sort_order)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`
    ).bind(item.id, organizationId, documentId, item.itemType, item.description, item.quantity, item.unit, item.unitPrice, item.technicalFee, item.summary, item.amount, itemRows.indexOf(item)))
  }

  // mileageSync
  if (mileageSync) {
    statements.push(env.DB.prepare(
      `INSERT INTO mileage_histories
        (id, organization_id, vehicle_id, maintenance_document_id, mileage, created_at, updated_at)
       VALUES (?, ?, ?, ?, ?, ?, ?)
       ON CONFLICT (organization_id, maintenance_document_id)
       DO UPDATE SET mileage = excluded.mileage, vehicle_id = excluded.vehicle_id, updated_at = excluded.updated_at`
    ).bind(crypto.randomUUID(), organizationId, input.vehicleId, documentId, mileageSync.inputMileage, now, now))
    statements.push(env.DB.prepare(
      `UPDATE vehicles SET mileage = ?, updated_at = ?
       WHERE id = ? AND organization_id = ? AND (mileage IS NULL OR mileage < ?)`
    ).bind(mileageSync.inputMileage, now, input.vehicleId, organizationId, mileageSync.inputMileage))
  }

  assertD1BatchStatementCount(statements.length)
  await env.DB.batch(statements)

  return jsonResponse({ document: await findMaintenanceDocument(database, documentId, organizationId) }, 200, env)
}

// ===================== 共通ヘルパー =====================

function parseNewCustomer(raw: unknown): NewCustomerInput | undefined {
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return undefined
  const obj = raw as Record<string, unknown>
  const name = typeof obj.name === 'string' ? obj.name.trim() : ''
  if (!name) return undefined
  return {
    name,
    nameKana: typeof obj.nameKana === 'string' ? obj.nameKana.trim() || undefined : undefined,
    phone: typeof obj.phone === 'string' ? normalizePhone(obj.phone) || undefined : undefined,
    email: typeof obj.email === 'string' ? obj.email.trim() || undefined : undefined,
    postalCode: typeof obj.postalCode === 'string' ? normalizePostalCode(obj.postalCode) || undefined : undefined,
    address: typeof obj.address === 'string' ? obj.address.trim() || undefined : undefined,
    birthDate: typeof obj.birthDate === 'string' ? normalizeCustomerBirthDateForStorage(obj.birthDate) || undefined : undefined,
    employer: typeof obj.employer === 'string' ? normalizeCustomerEmployerValue(obj.employer) || undefined : undefined,
  }
}

function parseNewVehicle(raw: unknown): NewVehicleInput | undefined {
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return undefined
  const obj = raw as Record<string, unknown>
  const maker = typeof obj.maker === 'string' ? obj.maker.trim() : ''
  const name = typeof obj.name === 'string' ? obj.name.trim() : ''
  if (!maker || !name) return undefined
  return {
    maker,
    name,
    model: typeof obj.model === 'string' ? obj.model.trim() || undefined : undefined,
    registrationNumber: typeof obj.registrationNumber === 'string' ? obj.registrationNumber.trim() || undefined : undefined,
    chassisNumber: typeof obj.chassisNumber === 'string' ? obj.chassisNumber.trim() || undefined : undefined,
    modelYear: typeof obj.modelYear === 'number' ? obj.modelYear : undefined,
    inspectionDate: typeof obj.inspectionDate === 'string' ? obj.inspectionDate.trim() || undefined : undefined,
    mileage: typeof obj.mileage === 'number' ? obj.mileage : undefined,
    bodyColor: typeof obj.bodyColor === 'string' ? obj.bodyColor.trim() || undefined : undefined,
    displacement: typeof obj.displacement === 'number' ? obj.displacement : undefined,
    transmission: typeof obj.transmission === 'string' ? obj.transmission.trim() || undefined : undefined,
  }
}

async function parseMaintenanceInput(
  body: Record<string, unknown>,
  database: ReturnType<typeof createDatabase>,
  organizationId: string,
  overrideCustomerId?: string,
  overrideVehicleId?: string,
  newCustomer?: NewCustomerInput,
  _newVehicle?: NewVehicleInput,
): Promise<MaintenanceInput> {
  const type = stringValue(body, 'type') || '整備請求書'
  if (!maintenanceDocumentTypes.has(type)) throw new HttpError(400, '書類種別が不正です。')
  const status = stringValue(body, 'status') || '下書き'
  if (!maintenanceStatuses.has(status)) throw new HttpError(400, '整備書類ステータスが不正です。')
  const category = stringValue(body, 'category')
  if (!maintenanceCategories.has(category)) throw new HttpError(400, '入庫区分が不正です。')

  const customerId = overrideCustomerId || stringValue(body, 'customerId')
  if (!customerId && !newCustomer) throw new HttpError(400, '顧客を選択してください。')

  const vehicleId = overrideVehicleId || stringValue(body, 'vehicleId')

  // 既存車両の所有関係検証（新規車両の場合はスキップ）
  if (vehicleId && customerId && !newCustomer) {
    const vehicle = await database.select({ id: vehicles.id, customerId: vehicles.customerId }).from(vehicles).where(and(eq(vehicles.id, vehicleId), eq(vehicles.organizationId, organizationId), isNull(vehicles.deletedAt))).get()
    if (!vehicle || vehicle.customerId !== customerId) throw new HttpError(400, '選択した車両が顧客と一致しません。')
  }

  const items = parseMaintenanceItems(body.items)
  const fees = parseFees(body.fees)
  const adjustment = integerNumber(body.adjustment, 0)
  const taxRate = parseTaxRate(body.taxRate)
  const rounding = body.rounding === '四捨五入' ? '四捨五入' : '切り捨て'
  return {
    number: nullableString(body, 'number'),
    type,
    status,
    category,
    customerId: customerId || '',
    vehicleId: vehicleId || '',
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

async function loadMaintenanceDocuments(database: ReturnType<typeof createDatabase>, organizationId: string, includeArchived = false) {
  const [documentRows, itemRows, customerRows, vehicleRows] = await Promise.all([
    database.select().from(maintenanceDocuments).where(eq(maintenanceDocuments.organizationId, organizationId)).orderBy(desc(maintenanceDocuments.issuedAt), desc(maintenanceDocuments.number)).all(),
    database.select().from(maintenanceItems).where(eq(maintenanceItems.organizationId, organizationId)).orderBy(asc(maintenanceItems.sortOrder)).all(),
    database.select().from(customers).where(and(eq(customers.organizationId, organizationId), isNull(customers.deletedAt))).all(),
    database.select().from(vehicles).where(and(eq(vehicles.organizationId, organizationId), isNull(vehicles.deletedAt))).all(),
  ])
  const itemsByDocument = groupBy(itemRows, (item) => item.documentId)
  const customersById = new Map(customerRows.map((customer) => [customer.id, customer]))
  const vehiclesById = new Map(vehicleRows.map((vehicle) => [vehicle.id, vehicle]))

  return documentRows.filter((document) => includeArchived || !document.archivedAt).map((document) => serializeMaintenanceDocument(
    document,
    customersById.get(document.customerId),
    document.vehicleId ? vehiclesById.get(document.vehicleId) : undefined,
    itemsByDocument.get(document.id) ?? [],
  ))
}

async function findMaintenanceDocument(database: ReturnType<typeof createDatabase>, documentId: string, organizationId: string) {
  const document = await database.select().from(maintenanceDocuments).where(and(eq(maintenanceDocuments.id, documentId), eq(maintenanceDocuments.organizationId, organizationId), isNull(maintenanceDocuments.archivedAt))).get()
  if (!document) return null
  const [items, customer, vehicle] = await Promise.all([
    loadMaintenanceItems(database, documentId, organizationId),
    database.select().from(customers).where(and(eq(customers.id, document.customerId), eq(customers.organizationId, organizationId), isNull(customers.deletedAt))).get(),
    document.vehicleId ? database.select().from(vehicles).where(and(eq(vehicles.id, document.vehicleId), eq(vehicles.organizationId, organizationId), isNull(vehicles.deletedAt))).get() : Promise.resolve(undefined),
  ])
  return serializeMaintenanceDocument(document, customer, vehicle, items)
}

async function loadMaintenanceItems(database: ReturnType<typeof createDatabase>, documentId: string, organizationId: string) {
  return database.select().from(maintenanceItems).where(and(eq(maintenanceItems.documentId, documentId), eq(maintenanceItems.organizationId, organizationId))).orderBy(asc(maintenanceItems.sortOrder)).all()
}

async function getCurrentVehicleMileage(database: ReturnType<typeof createDatabase>, vehicleId: string | null, organizationId: string): Promise<number | null> {
  if (!vehicleId) return null
  const result = await database.select({ mileage: vehicles.mileage })
    .from(vehicles)
    .where(and(eq(vehicles.id, vehicleId), eq(vehicles.organizationId, organizationId), isNull(vehicles.deletedAt)))
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

function serializeMaintenanceDocument(document: typeof maintenanceDocuments.$inferSelect, customer: typeof customers.$inferSelect | undefined, vehicle: typeof vehicles.$inferSelect | undefined, rows: Array<typeof maintenanceItems.$inferSelect>) {
  const fees = extractFees(rows)
  const adjustment = extractAdjustment(rows)
  const items = rows.filter((item) => item.itemType === '作業' || item.itemType === '部品')
  const details = parseMaintenanceDetails(parseDetailsJson(document.detailsJson))
  const abacusImport = parseAbacusDocumentImportMetadata(document.detailsJson)
  const abacusDetails = parseAbacusDetailEnvelope(document.detailsJson)
  const abacusLines = new Map(abacusDetails?.lines.map((line) => [line.sourceRowIndex, line]) ?? [])
  const customerBirthDate = hasOwnRecordField(details.customerOverride, 'birthDate')
    ? normalizeCustomerBirthDateForStorage(details.customerOverride?.birthDate)
    : details.customerBirthDate || normalizeCustomerBirthDateForStorage(customer?.birthDate)
  const customerEmployer = hasOwnRecordField(details.customerOverride, 'employer')
    ? normalizeCustomerEmployerValue(details.customerOverride?.employer)
    : details.customerEmployer || normalizeCustomerEmployerValue(customer?.employer)
  return {
    id: document.id,
    updatedAt: document.updatedAt,
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
      email: customer?.email ?? '',
      postalCode: customer?.postalCode ?? '',
      address: customer?.address ?? '',
      birthDate: customerBirthDate,
      employer: customerEmployer,
    },
    vehicleId: document.vehicleId,
    vehicle: vehicle ? [vehicle.maker, vehicle.name].filter(Boolean).join(' ') : 'なし',
    abacusImport,
    isAbacusMigration: abacusDetails?.isAbacusMigration ?? false,
    abacusDetailReport: abacusDetails?.report ?? null,
    abacusAmounts: abacusDetails?.amounts ?? null,
    plate: vehicle?.registrationNumber ?? '',
    mileage: vehicle ? normalizeMileage(vehicle.mileage) : '',
    vehicleDetails: vehicle ? {
      maker: vehicle?.maker ?? '',
      name: vehicle?.name ?? '',
      modelType: vehicle?.model ?? '',
      plate: vehicle?.registrationNumber ?? '',
      vin: vehicle?.chassisNumber ?? '',
      year: normalizeModelYear(vehicle?.modelYear),
      inspectionDate: vehicle?.inspectionDate ?? '',
      mileage: normalizeMileage(vehicle?.mileage),
      color: vehicle?.bodyColor ?? '',
      displacement: normalizeDisplacement(vehicle?.displacement),
      transmission: vehicle?.transmission ?? '',
      inspectionRecordAvailable: vehicle?.inspectionRecordAvailable ?? false,
    } : null,
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
    details: { ...details, customerBirthDate, customerEmployer },
    archivedAt: document.archivedAt,
    archivedPreviousStatus: document.archivedPreviousStatus,
    archivedBy: document.archivedBy,
    purgeAt: document.purgeAt,
    keepForever: document.keepForever,
    items: items.map((item) => ({ id: item.id, kind: item.itemType === '部品' ? '部品' : '作業', description: item.description, quantity: item.quantity, unit: item.unit, unitPrice: item.unitPrice, technicalFee: item.technicalFee, summary: item.summary, sourceRowIndex: item.sortOrder, abacusDetail: abacusLines.get(item.sortOrder) ?? null, isAbacusMigration: Boolean(abacusDetails) })),
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

export function parseMaintenanceItems(value: unknown): MaintenanceItemInput[] {
  if (!Array.isArray(value)) return []
  assertArrayLength(value, maximumDocumentItemCount, `整備明細は${maximumDocumentItemCount}件以内で入力してください。`)
  return value.filter((item): item is Record<string, unknown> => Boolean(item) && typeof item === 'object' && !Array.isArray(item)).map((item) => {
    const quantity = nonNegativeNumber(item.quantity, 1)
    const unitPrice = integerNumber(item.unitPrice, 0)
    const technicalFee = integerNumber(item.technicalFee, 0)
    const unit = typeof item.unit === 'string' ? item.unit.trim() : '式'
    return { kind: item.kind === '部品' ? '部品' : '作業', description: stringValue(item, 'description'), quantity, unit, unitPrice, technicalFee, summary: stringValue(item, 'summary'), amount: Math.round(quantity * unitPrice) + technicalFee }
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
function normalizeCustomerEmployerValue(value: unknown) {
  const normalized = typeof value === 'string' ? value.normalize('NFKC').trim().slice(0, 200) : ''
  return normalized === 'employer' ? '' : normalized
}
function nullableString(body: Record<string, unknown>, key: string) { const value = stringValue(body, key); return value || null }
function dateValue(value: unknown) { return normalizeCalendarDate(value) ?? '' }
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
    phone: normalizePhone(stringValue(customerOverride, 'phone')),
    postalCode: normalizePostalCode(stringValue(customerOverride, 'postalCode')),
    address: stringValue(customerOverride, 'address'),
    ...(hasOwnRecordField(customerOverride, 'birthDate') ? { birthDate: normalizeCustomerBirthDateForStorage(stringValue(customerOverride, 'birthDate')) } : {}),
    ...(hasOwnRecordField(customerOverride, 'employer') ? { employer: normalizeCustomerEmployerValue(stringValue(customerOverride, 'employer')) } : {}),
  }
  const normalizedVehicleOverride = {
    maker: stringValue(vehicleOverride, 'maker'),
    name: stringValue(vehicleOverride, 'name'),
    modelType: stringValue(vehicleOverride, 'modelType'),
    plate: stringValue(vehicleOverride, 'plate'),
    vin: stringValue(vehicleOverride, 'vin'),
    year: normalizeModelYear(stringValue(vehicleOverride, 'year')),
    inspectionDate: stringValue(vehicleOverride, 'inspectionDate'),
    mileage: normalizeMileage(stringValue(vehicleOverride, 'mileage')),
    color: stringValue(vehicleOverride, 'color'),
    displacement: normalizeDisplacement(stringValue(vehicleOverride, 'displacement')),
    transmission: stringValue(vehicleOverride, 'transmission'),
    inspectionRecordAvailable: typeof vehicleOverride.inspectionRecordAvailable === 'boolean' ? vehicleOverride.inspectionRecordAvailable : false,
  }
  return {
    staffName: stringValue(source, 'staffName'),
    customerHonorific: stringValue(source, 'customerHonorific') || '様',
    customerBirthDate: normalizeCustomerBirthDateForStorage(stringValue(source, 'customerBirthDate')),
    customerEmployer: normalizeCustomerEmployerValue(stringValue(source, 'customerEmployer')),
    customerContactPhone: stringValue(source, 'customerContactPhone'),
    bankName: stringValue(source, 'bankName'),
    bankAccount: stringValue(source, 'bankAccount'),
    bankAccountHolder: stringValue(source, 'bankAccountHolder'),
    customerOverride: (hasOverrideValue(normalizedCustomerOverride) || hasOwnRecordField(customerOverride, 'birthDate') || hasOwnRecordField(customerOverride, 'employer')) ? normalizedCustomerOverride as Record<string, string> : null,
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

function hasOwnRecordField(record: object | null | undefined, field: string) {
  return record !== null && record !== undefined && Object.prototype.hasOwnProperty.call(record, field)
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
