import { and, desc, eq, isNull, ne } from 'drizzle-orm'
import {
  customers,
  maintenanceDocuments,
  salesDocuments,
  vehicles,
} from '@vehicle-management/database'
import { UnauthorizedError } from '../auth/firebase'
import { requireOrganizationContext } from '../auth/organization'
import { createDatabase } from '../db/client'
import { HttpError, jsonResponse, readJson } from '../http'
import { parseAbacusDocumentImportMetadata } from '../lib/abacus-document-metadata'
import {
  CUSTOMER_FIELD_LABELS,
  CUSTOMER_SYNC_ALLOWLIST,
  VEHICLE_FIELD_LABELS,
  VEHICLE_SYNC_ALLOWLIST,
  computeCustomerDiffs,
  computeVehicleDiffs,
  extractCustomerFieldsFromOverride,
  extractVehicleFieldsFromOverride,
  findDuplicateCustomers,
  findDuplicateVehicles,
  validateCombination,
  type CustomerSyncField,
  type DuplicateCustomer,
  type DuplicateVehicle,
  type NewCustomerInput,
  type NewVehicleInput,
  type VehicleSyncField,
} from '../lib/master-sync-helpers'

type MileageDiff = {
  currentValue: number | null
  openedValue: number | null
  documentValue: number
  isChanged: boolean
  willUpdateVehicle: boolean
}

type CustomerDiffItem = {
  field: CustomerSyncField
  label: string
  currentValue: string
  documentValue: string
  isConflict: boolean
  isAttention: boolean
}

type VehicleDiffItem = {
  field: VehicleSyncField
  label: string
  currentValue: string
  documentValue: string
  isConflict: boolean
  isAttention: boolean
}

type SyncPreviewResponse = {
  hasDifferences: boolean
  isOlderThanLatestDocument: boolean
  customerDiffs: CustomerDiffItem[]
  vehicleDiffs: VehicleDiffItem[]
  mileageDiff?: MileageDiff
  expectedCustomerUpdatedAt: string | null
  expectedVehicleUpdatedAt: string | null
  duplicateCustomers?: DuplicateCustomer[]
  duplicateVehicles?: DuplicateVehicle[]
  resolvedCustomerId?: string
  resolvedVehicleId?: string | null
}

function parseMileageValue(value: string | undefined | null): number | null {
  if (!value) return null
  const digits = value.replace(/[^0-9]/g, '')
  if (!digits) return null
  const parsed = Number(digits)
  return Number.isFinite(parsed) ? parsed : null
}

export async function handleSyncPreviewRoutes(request: Request, env: Env): Promise<Response | null> {
  const pathname = new URL(request.url).pathname.replace(/\/$/, '') || '/'
  if (pathname !== '/api/sync-preview') return null
  if (request.method !== 'POST') return jsonResponse({ error: 'この操作には対応していません。' }, 405, env)

  try {
    const database = createDatabase(env.DB)
    const context = await requireOrganizationContext(request, env, database)
    const organizationId = context.organization.organizationId

    const body = await readJson(request)
    const result = await computeSyncPreview(body, database, organizationId)
    return jsonResponse(result, 200, env)
  } catch (error) {
    if (error instanceof UnauthorizedError) return jsonResponse({ error: error.message }, 401, env)
    if (error instanceof HttpError) return jsonResponse({ error: error.message }, error.status, env)
    console.error(error)
    return jsonResponse({ error: '同期プレビューの処理に失敗しました。' }, 500, env)
  }
}

async function computeSyncPreview(
  body: Record<string, unknown>,
  database: ReturnType<typeof createDatabase>,
  organizationId: string,
): Promise<SyncPreviewResponse> {
  const documentType = typeof body.documentType === 'string' && (body.documentType === 'sales' || body.documentType === 'maintenance')
    ? body.documentType
    : null
  if (!documentType) throw new HttpError(400, 'documentTypeはsalesまたはmaintenanceを指定してください。')

  const documentId = typeof body.documentId === 'string' ? body.documentId : undefined
  const customerId = typeof body.customerId === 'string' ? body.customerId : undefined
  const vehicleId = typeof body.vehicleId === 'string' ? body.vehicleId : undefined
  const newCustomer = body.newCustomer && typeof body.newCustomer === 'object' && !Array.isArray(body.newCustomer)
    ? body.newCustomer as NewCustomerInput
    : undefined
  const newVehicle = body.newVehicle && typeof body.newVehicle === 'object' && !Array.isArray(body.newVehicle)
    ? body.newVehicle as NewVehicleInput
    : undefined
  const customerOverride = body.customerOverride && typeof body.customerOverride === 'object' && !Array.isArray(body.customerOverride)
    ? body.customerOverride as Record<string, unknown>
    : undefined
  const vehicleOverride = body.vehicleOverride && typeof body.vehicleOverride === 'object' && !Array.isArray(body.vehicleOverride)
    ? body.vehicleOverride as Record<string, unknown>
    : undefined
  const issuedAt = typeof body.issuedAt === 'string' ? body.issuedAt : undefined
  const openedCustomerUpdatedAt = typeof body.openedCustomerUpdatedAt === 'string' ? body.openedCustomerUpdatedAt : undefined
  const openedVehicleUpdatedAt = typeof body.openedVehicleUpdatedAt === 'string' ? body.openedVehicleUpdatedAt : undefined
  const mileageContext = body.mileageContext && typeof body.mileageContext === 'object' && !Array.isArray(body.mileageContext)
    ? body.mileageContext as { openedMileage?: number | null }
    : undefined
  let allowVehicleless = false

  // 1. documentId/documentTypeの整合性検証
  if (documentId && !documentType) {
    throw new HttpError(400, 'documentId指定時はdocumentTypeが必須です。')
  }
  if (documentId) {
    const docTable = documentType === 'sales' ? salesDocuments : maintenanceDocuments
    const doc = await database
      .select({ id: docTable.id, customerId: docTable.customerId, vehicleId: docTable.vehicleId, detailsJson: docTable.detailsJson })
      .from(docTable)
      .where(and(eq(docTable.id, documentId), eq(docTable.organizationId, organizationId)))
      .get()
    if (!doc) throw new HttpError(404, '書類が見つかりません。')

    // 過去の販売書類と、ABACUS移行で作成された車両なし整備書類は、
    // 既存レコードの通常編集に限ってsync-previewの車両必須検証を緩和する。
    // 新規POST（documentIdなし）や通常の車両なし整備書類では車両を要求する。
    const isAbacusVehiclelessMaintenance = documentType === 'maintenance'
      && doc.vehicleId === null
      && parseAbacusDocumentImportMetadata(doc.detailsJson)?.vehicleless === true
    allowVehicleless = (
      documentType === 'sales'
      || isAbacusVehiclelessMaintenance
    )
      && doc.vehicleId === null
      && !vehicleId
      && !newVehicle
      && !newCustomer

    // 既存書類の顧客・車両変更は画面上で許可しているため、旧レコードとの
    // ID一致は要求しない。指定先の組織・削除状態・顧客と車両の所有関係は、
    // 下の顧客・車両解決処理で検証する。
  }

  // 2. 排他的入力検証
  const combinationError = validateCombination({
    customerId,
    newCustomer,
    vehicleId,
    newVehicle,
    documentType,
    allowVehicleless,
  })
  if (combinationError) throw new HttpError(combinationError.status, combinationError.message)

  // 3. 顧客・車両の解決
  let resolvedCustomerId: string | null = null
  let resolvedVehicleId: string | null = null
  let currentCustomer: typeof customers.$inferSelect | null = null
  let currentVehicle: typeof vehicles.$inferSelect | null = null

  if (customerId) {
    const customer = await database
      .select()
      .from(customers)
      .where(and(eq(customers.id, customerId), eq(customers.organizationId, organizationId), isNull(customers.deletedAt)))
      .get()
    if (!customer) throw new HttpError(404, '顧客が見つかりません。')
    resolvedCustomerId = customer.id
    currentCustomer = customer
  }

  if (vehicleId) {
    const vehicle = await database
      .select()
      .from(vehicles)
      .where(and(eq(vehicles.id, vehicleId), eq(vehicles.organizationId, organizationId), isNull(vehicles.deletedAt)))
      .get()
    if (!vehicle) throw new HttpError(404, '車両が見つかりません。')
    // 所有関係検証
    if (resolvedCustomerId && vehicle.customerId !== resolvedCustomerId) {
      throw new HttpError(400, '選択した車両が顧客と一致しません。')
    }
    resolvedVehicleId = vehicle.id
    currentVehicle = vehicle
  }

  // 新規顧客の場合: customerIdはnullのままでresolvedCustomerIdもnull
  // 新規車両の場合: vehicleIdはnullのままでresolvedVehicleIdもnull

  // 4. 過去日付判定
  const effectiveVehicleIdForSearch = resolvedVehicleId ?? vehicleId ?? null
  let isOlderThanLatestDocument = false
  if (effectiveVehicleIdForSearch && issuedAt) {
    const latestIssuedAt = await getLatestIssuedAtExcludingSelf(
      database,
      organizationId,
      effectiveVehicleIdForSearch,
      documentId,
      documentType,
    )
    if (latestIssuedAt && issuedAt < latestIssuedAt) {
      isOlderThanLatestDocument = true
    }
  }

  // 5. 顧客・車両の差分計算（既存の場合のみ）
  let customerDiffs: CustomerDiffItem[] = []
  let vehicleDiffs: VehicleDiffItem[] = []
  let expectedCustomerUpdatedAt: string | null = null
  let expectedVehicleUpdatedAt: string | null = null

  if (currentCustomer) {
    expectedCustomerUpdatedAt = currentCustomer.updatedAt
    const docCustomerValues = extractCustomerFieldsFromOverride(customerOverride)
    const rawDiffs = computeCustomerDiffs(currentCustomer, docCustomerValues)
    const customerConflict = openedCustomerUpdatedAt && currentCustomer.updatedAt !== openedCustomerUpdatedAt
    customerDiffs = rawDiffs.map((d) => ({
      ...d,
      isConflict: customerConflict,
    }))
  }

  if (currentVehicle) {
    expectedVehicleUpdatedAt = currentVehicle.updatedAt
    const docVehicleValues = extractVehicleFieldsFromOverride(vehicleOverride)
    const rawDiffs = computeVehicleDiffs(currentVehicle, docVehicleValues)
    const vehicleConflict = openedVehicleUpdatedAt && currentVehicle.updatedAt !== openedVehicleUpdatedAt
    vehicleDiffs = rawDiffs.map((d) => ({
      ...d,
      isConflict: vehicleConflict,
    }))
  }

  // 6. 走行距離差分
  let mileageDiff: MileageDiff | undefined
  if (currentVehicle) {
    const openedMileage = mileageContext?.openedMileage ?? null
    const documentMileage = parseMileageValue((vehicleOverride?.mileage as string | undefined) ?? null)
    const currentMileage = currentVehicle.mileage

    if (documentMileage !== null && openedMileage !== documentMileage) {
      mileageDiff = {
        currentValue: currentMileage,
        openedValue: openedMileage,
        documentValue: documentMileage,
        isChanged: true,
        willUpdateVehicle: currentMileage === null || documentMileage > currentMileage,
      }
    }
  }

  // 7. 重複検出（新規作成時のみ）
  let duplicateCustomers: DuplicateCustomer[] | undefined
  let duplicateVehicles: DuplicateVehicle[] | undefined

  if (newCustomer) {
    const allCustomers = await database
      .select({ id: customers.id, name: customers.name, phone: customers.phone, email: customers.email })
      .from(customers)
      .where(eq(customers.organizationId, organizationId))
      .all()
    duplicateCustomers = findDuplicateCustomers(allCustomers, newCustomer, resolvedCustomerId ?? undefined)
  }

  if (newVehicle) {
    const allVehicles = await database
      .select({
        id: vehicles.id,
        maker: vehicles.maker,
        name: vehicles.name,
        registrationNumber: vehicles.registrationNumber,
        chassisNumber: vehicles.chassisNumber,
      })
      .from(vehicles)
      .where(eq(vehicles.organizationId, organizationId))
      .all()
    duplicateVehicles = findDuplicateVehicles(allVehicles, newVehicle, resolvedVehicleId ?? undefined)
  }

  const hasDifferences = customerDiffs.length > 0 || vehicleDiffs.length > 0 || Boolean(mileageDiff)

  return {
    hasDifferences,
    isOlderThanLatestDocument,
    customerDiffs,
    vehicleDiffs,
    mileageDiff,
    expectedCustomerUpdatedAt,
    expectedVehicleUpdatedAt,
    duplicateCustomers,
    duplicateVehicles,
    resolvedCustomerId: resolvedCustomerId ?? undefined,
    resolvedVehicleId,
  }
}

async function getLatestIssuedAtExcludingSelf(
  database: ReturnType<typeof createDatabase>,
  organizationId: string,
  vehicleId: string,
  documentId: string | undefined,
  documentType: 'sales' | 'maintenance' | undefined,
): Promise<string | null> {
  const salesConditions = [
    eq(salesDocuments.vehicleId, vehicleId),
    eq(salesDocuments.organizationId, organizationId),
  ]
  if (documentType === 'sales' && documentId) {
    salesConditions.push(ne(salesDocuments.id, documentId))
  }

  const maintenanceConditions = [
    eq(maintenanceDocuments.vehicleId, vehicleId),
    eq(maintenanceDocuments.organizationId, organizationId),
  ]
  if (documentType === 'maintenance' && documentId) {
    maintenanceConditions.push(ne(maintenanceDocuments.id, documentId))
  }

  const [latestSales, latestMaintenance] = await Promise.all([
    database
      .select({ issuedAt: salesDocuments.issuedAt })
      .from(salesDocuments)
      .where(and(...salesConditions))
      .orderBy(desc(salesDocuments.issuedAt))
      .limit(1)
      .get(),
    database
      .select({ issuedAt: maintenanceDocuments.issuedAt })
      .from(maintenanceDocuments)
      .where(and(...maintenanceConditions))
      .orderBy(desc(maintenanceDocuments.issuedAt))
      .limit(1)
      .get(),
  ])

  const dates = [latestSales?.issuedAt, latestMaintenance?.issuedAt].filter(Boolean) as string[]
  if (dates.length === 0) return null
  return dates.sort().reverse()[0]
}
