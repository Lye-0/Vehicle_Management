import { env, SELF } from 'cloudflare:test'
import { afterAll, beforeAll, describe, expect, it } from 'vitest'

const testOrganizationId = 'org-abacus-registration-test'
const testUid = 'abacus-registration-test-user'
const customerId = 'abacus-customer-group-registrationtest'
const vehicleId = 'abacus-vehicle-registrationtest'

beforeAll(async () => {
  await env.DB.prepare('INSERT OR IGNORE INTO organizations (id, name) VALUES (?, ?)').bind(testOrganizationId, 'ABACUS registration test').run()
  await env.DB.prepare('INSERT OR IGNORE INTO staff_profiles (uid, display_name, role) VALUES (?, ?, ?)').bind(testUid, 'ABACUS registration test', 'owner').run()
  await env.DB.prepare('INSERT OR IGNORE INTO organization_memberships (id, organization_id, uid, role, status) VALUES (?, ?, ?, ?, ?)').bind(`mem-${testUid}`, testOrganizationId, testUid, 'owner', 'active').run()
})

afterAll(async () => {
  await env.DB.prepare('DELETE FROM sales_document_items WHERE organization_id = ?').bind(testOrganizationId).run()
  await env.DB.prepare('DELETE FROM maintenance_items WHERE organization_id = ?').bind(testOrganizationId).run()
  await env.DB.prepare('DELETE FROM sales_documents WHERE organization_id = ?').bind(testOrganizationId).run()
  await env.DB.prepare('DELETE FROM maintenance_documents WHERE organization_id = ?').bind(testOrganizationId).run()
  await env.DB.prepare('DELETE FROM vehicle_files WHERE organization_id = ?').bind(testOrganizationId).run()
  await env.DB.prepare('DELETE FROM vehicles WHERE organization_id = ?').bind(testOrganizationId).run()
  await env.DB.prepare('DELETE FROM customers WHERE organization_id = ?').bind(testOrganizationId).run()
  await env.DB.prepare('DELETE FROM organization_memberships WHERE organization_id = ?').bind(testOrganizationId).run()
  await env.DB.prepare('DELETE FROM staff_profiles WHERE uid = ?').bind(testUid).run()
  await env.DB.prepare('DELETE FROM organizations WHERE id = ?').bind(testOrganizationId).run()
})

describe('ABACUS registration', () => {
  it('requires the explicit confirmation string and does not write on rejection', async () => {
    const packageFiles = await createPackage({ vehicleCount: '1' })
    const response = await postRegistration(packageFiles, '確認')

    expect(response.status).toBe(400)
    expect(await countRows('customers')).toBe(0)
    expect(await countRows('vehicles')).toBe(0)
  })

  it('validates vehicle counts and commits customers and vehicles together', async () => {
    const invalidPackage = await createPackage({ vehicleCount: '2' })
    const invalidResponse = await postRegistration(invalidPackage, 'ABACUS登録を実行')
    expect(invalidResponse.status).toBe(409)
    expect(await countRows('customers')).toBe(0)

    const validPackage = await createPackage({ vehicleCount: '1' })
    const validResponse = await postRegistration(validPackage, 'ABACUS登録を実行')
    expect(validResponse.status).toBe(200)
    await expect(validResponse.json()).resolves.toMatchObject({
      status: 'committed',
      customerCount: 1,
      vehicleCount: 1,
      imageCount: 0,
      customers: { imported: 1, updated: 0 },
      vehicles: { imported: 1, updated: 0 },
    })
    expect(await countRows('customers')).toBe(1)
    expect(await countRows('vehicles')).toBe(1)
  })

  it('commits the graph-final package, preserves vehicleless documents, and normalizes duplicate numbers', async () => {
    const packageFiles = await createGraphFinalPackage()
    const response = await postGraphFinalRegistration(packageFiles, 'ABACUS登録を実行')
    expect(response.status).toBe(200)
    const responseBody = await response.json()
    expect(responseBody).toMatchObject({
      status: 'committed',
      customerCount: 1,
      vehicleCount: 0,
      salesCount: 1,
      maintenanceCount: 2,
      vehiclelessDocumentCount: 3,
      excludedDocumentCount: 0,
      numberAdjustedDocumentCount: 1,
      amountDefaultedDocumentCount: 1,
    })
    const maintenance = await env.DB.prepare('SELECT vehicle_id AS vehicleId FROM maintenance_documents WHERE id = ? AND organization_id = ?').bind('abacus-maintenance-finaltest', testOrganizationId).first<{ vehicleId: string | null }>()
    expect(maintenance?.vehicleId ?? null).toBeNull()
    const duplicateMaintenance = await env.DB.prepare('SELECT number, subtotal, total, details_json AS detailsJson FROM maintenance_documents WHERE id = ? AND organization_id = ?').bind('abacus-maintenance-finaltest-2', testOrganizationId).first<{ number: string; subtotal: number; total: number; detailsJson: string }>()
    expect(duplicateMaintenance?.number).toBe('9002-2')
    expect(duplicateMaintenance?.subtotal).toBe(0)
    expect(duplicateMaintenance?.total).toBe(0)
    expect(duplicateMaintenance?.detailsJson).toContain('ABACUS金額未設定')
    expect(await countRows('customers')).toBe(1)

    const salesResponse = await SELF.fetch(new Request('https://example.com/api/sales-documents', { headers: authHeaders() }))
    expect(salesResponse.status).toBe(200)
    const salesBody = await salesResponse.json() as { documents: Array<{ number: string; vehicle: string; vehicleId: string | null; abacusImport?: { vehicleless: boolean; sourceLocation: string } | null }> }
    expect(salesBody.documents.find((document) => document.number === '9001')).toMatchObject({ vehicle: 'なし', vehicleId: null, abacusImport: { vehicleless: true, sourceLocation: 'hanbai.csv #1' } })

    const maintenanceResponse = await SELF.fetch(new Request('https://example.com/api/maintenance-documents', { headers: authHeaders() }))
    expect(maintenanceResponse.status).toBe(200)
    const maintenanceBody = await maintenanceResponse.json() as { documents: Array<{ number: string; vehicle: string; vehicleId: string | null; abacusImport?: { vehicleless: boolean; sourceLocation: string } | null }> }
    expect(maintenanceBody.documents.find((document) => document.number === '9002')).toMatchObject({ vehicle: 'なし', vehicleId: null, abacusImport: { vehicleless: true } })
  })

  it('registers Gate19 sales financial lines and preserves ABACUS amounts', async () => {
    const detailJson = JSON.stringify({
      version: 1,
      kind: 'abacus-detail-lines',
      sourceFile: 'abx-cs-hb.ucs',
      recordIdHex: '92A0',
      documentNumber: '9003',
      customerName: '最終登録テスト顧客',
      vehicleName: '',
      registrationNumber: '',
      chassisNumber: '',
      lines: [{ description: 'フロアマット', quantity: null, unit: null, unitPrice: null, partAmount: 4263, technicalFees: null, summary: null, sourceRowIndex: 1 }],
      financialLines: [
        { description: '車両本体価格', itemType: '車両本体価格', taxCategory: '課税', amount: 1160060, sourceRowIndex: 91 },
        { description: '諸費用（課税）', itemType: '手続代行費用', taxCategory: '課税', amount: 60060, sourceRowIndex: 93 },
        { description: '諸費用（非課税）', itemType: '法定費用', taxCategory: '非課税', amount: 18310, sourceRowIndex: 94 },
      ],
      partsSubtotal: 4263,
      technicalSubtotal: null,
      abacusSubtotal: 1184390,
      abacusTotal: 1242693,
      abacusTax: 58303,
      abacusTaxRate: 5,
      detailAmount: 1242693,
      excludedDetailCount: 11,
      amountOnlyRowCount: 0,
      matchStatus: 'matched',
      warning: '',
    })
    const packageFiles = await createGraphFinalPackage('gate19-detail', detailJson)
    const response = await postGraphFinalRegistration(packageFiles, 'ABACUS登録を実行')
    expect(response.status).toBe(200)

    const document = await env.DB.prepare('SELECT subtotal, tax, total FROM sales_documents WHERE id = ? AND organization_id = ?').bind(packageFiles.salesId, testOrganizationId).first<{ subtotal: number; tax: number; total: number }>()
    expect(document).toEqual({ subtotal: 1184390, tax: 58303, total: 1242693 })
    const items = await env.DB.prepare('SELECT item_type AS itemType, description, tax_category AS taxCategory, amount FROM sales_document_items WHERE document_id = ? AND organization_id = ? ORDER BY sort_order').bind(packageFiles.salesId, testOrganizationId).all<{ itemType: string; description: string; taxCategory: string; amount: number }>()
    expect(items.results).toEqual([
      { itemType: '付属品・特別仕様', description: 'フロアマット', taxCategory: '課税', amount: 4263 },
      { itemType: '車両本体価格', description: '車両本体価格', taxCategory: '課税', amount: 1160060 },
      { itemType: '手続代行費用', description: '諸費用(課税)', taxCategory: '課税', amount: 60060 },
      { itemType: '法定費用', description: '諸費用(非課税)', taxCategory: '非課税', amount: 18310 },
    ])
  })

  it('re-runs the same graph-final package idempotently without duplicating rows', async () => {
    const packageFiles = await createGraphFinalPackage('gate18-idempotency')
    const firstResponse = await postGraphFinalRegistration(packageFiles, 'ABACUS登録を実行')
    expect(firstResponse.status).toBe(200)
    await expect(firstResponse.json()).resolves.toMatchObject({
      status: 'committed',
      customers: { imported: 1, updated: 0 },
      vehicles: { imported: 0, updated: 0 },
      documents: { imported: 3, existing: 0 },
    })

    const firstCounts = await countGraphFinalRows(packageFiles)
    const secondResponse = await postGraphFinalRegistration(packageFiles, 'ABACUS登録を実行')
    expect(secondResponse.status).toBe(200)
    await expect(secondResponse.json()).resolves.toMatchObject({
      status: 'committed',
      customers: { imported: 0, updated: 1 },
      vehicles: { imported: 0, updated: 0 },
      documents: { imported: 0, existing: 3 },
    })

    expect(await countGraphFinalRows(packageFiles)).toEqual(firstCounts)
  })

  it('revalidates a Gate 17 ready envelope in preview without writing', async () => {
    const packageFiles = await createGraphFinalPackage()
    const ready = await createReadyEnvelope(packageFiles)
    const beforeCustomers = await countRows('customers')
    const response = await postGraphFinalPreview(packageFiles, ready)

    expect(response.status).toBe(200)
    await expect(response.json()).resolves.toMatchObject({
      status: 'preview',
      customerCount: 1,
      vehicleCount: 0,
      salesCount: 1,
      maintenanceCount: 2,
      vehiclelessDocumentCount: 3,
      excludedDocumentCount: 0,
      imageCount: 0,
      errors: [],
    })
    expect(await countRows('customers')).toBe(beforeCustomers)
  })

  it('chunks graph-final existing-row lookups below the D1 bound-variable limit', async () => {
    const packageFiles = await createLargeGraphFinalPackage(120)
    const response = await postGraphFinalRegistration(packageFiles, 'ABACUS登録を実行')

    expect(response.status).toBe(200)
    await expect(response.json()).resolves.toMatchObject({ status: 'committed', customerCount: 120, vehicleCount: 0, salesCount: 0, maintenanceCount: 0 })
    expect(await countRows('customers')).toBeGreaterThanOrEqual(120)
  })
})

async function createPackage(options: { vehicleCount: string }) {
  const customersCsv = [
    '顧客ID,顧客番号,顧客名,ふりがな,電話番号,メールアドレス,郵便番号,住所,メモ,車両台数',
    [customerId, 'ABACUS-REGISTRATION-TEST', '登録テスト顧客', '', '', '', '', '', '', options.vehicleCount].join(','),
  ].join('\n')
  const vehiclesCsv = [
    '車両ID,顧客ID,顧客名,メーカー,車名,型式,登録番号,車台番号,年式,車検満了日,走行距離,車体色,排気量,ミッション,記録簿,備考',
    [vehicleId, customerId, '登録テスト顧客', 'テストメーカー', 'テスト車', '', '', '', '', '', '', '', '', '', '', ''].join(','),
  ].join('\n')
  const attachmentsJson = JSON.stringify({ version: 1, kind: 'abacus-web-import-image-attachments', status: 'manual-upload-required', attachments: [] })
  const data = [
    { path: 'customers.csv', content: customersCsv },
    { path: 'vehicles.csv', content: vehiclesCsv },
    { path: 'image-attachments.json', content: attachmentsJson },
  ]
  const dataFiles = await Promise.all(data.map(async ({ path, content }) => ({ relativePath: path, sizeBytes: byteLength(content), sha256: await sha256(content) })))
  const manifest = JSON.stringify({
    version: 1,
    kind: 'abacus-web-import-registration-package',
    status: 'registration-preview',
    summary: { candidateCount: 1, customerRowCount: 1, vehicleRowCount: 1, imageCount: 0 },
    dataFiles,
    imageFiles: [],
  })
  return { manifest, manifestSha256: await sha256(manifest), customersCsv, vehiclesCsv, attachmentsJson }
}

async function postRegistration(packageFiles: Awaited<ReturnType<typeof createPackage>>, confirmation: string) {
  const formData = new FormData()
  formData.append('manifest', new File([packageFiles.manifest], 'manifest.json', { type: 'application/json' }))
  formData.append('customers', new File([packageFiles.customersCsv], 'customers.csv', { type: 'text/csv' }))
  formData.append('vehicles', new File([packageFiles.vehiclesCsv], 'vehicles.csv', { type: 'text/csv' }))
  formData.append('attachments', new File([packageFiles.attachmentsJson], 'image-attachments.json', { type: 'application/json' }))
  formData.append('manifestSha256', packageFiles.manifestSha256)
  formData.append('confirmation', confirmation)
  const probe = new Request('https://example.com/api/import/abacus-registration/commit', { method: 'POST', headers: authHeaders(), body: formData })
  const body = await probe.arrayBuffer()
  const headers = new Headers(probe.headers)
  headers.set('Content-Length', String(body.byteLength))
  return SELF.fetch(new Request(probe.url, { method: 'POST', headers, body }))
}

async function createGraphFinalPackage(suffix = 'finaltest', salesDetailJson = '') {
  const customer = suffix === 'finaltest' ? 'merge-preview:same-name:最終登録テスト' : `merge-preview:same-name:最終登録テスト-${suffix}`
  const customerName = '最終登録テスト顧客'
  const salesId = suffix === 'finaltest' ? 'abacus-sales-finaltest' : `abacus-sales-${suffix}`
  const maintenanceId = suffix === 'finaltest' ? 'abacus-maintenance-finaltest' : `abacus-maintenance-${suffix}`
  const duplicateMaintenanceId = suffix === 'finaltest' ? 'abacus-maintenance-finaltest-2' : `abacus-maintenance-${suffix}-2`
  const customersCsv = ['顧客ID,顧客番号,顧客名,ふりがな,電話番号,メールアドレス,郵便番号,住所,メモ,車両台数', [customer, 'ABACUS-CUSTOMER-NUMBER-', customerName, '', '', '', '', '', '', '0'].join(',')].join('\n')
  const vehiclesCsv = '車両ID,顧客ID,顧客名,メーカー,車名,型式,登録番号,車台番号,年式,車検満了日,走行距離,車体色,排気量,ミッション,記録簿,備考'
  const salesNumber = salesDetailJson ? '9003' : '9001'
  const salesCsv = ['書類ID,書類番号,書類種別,ステータス,顧客名,車名,登録番号,発行日,支払期限,税率,小計,消費税,合計,明細,備考,明細詳細', [salesId, salesNumber, '請求書', '下書き', customerName, '', '', '2026-01-02', '', '10', salesDetailJson ? '1184390' : '1000', salesDetailJson ? '58303' : '0', salesDetailJson ? '1242693' : '1000', '移行販売', 'ABACUSテスト', csvCell(salesDetailJson)].join(',')].join('\n')
  const maintenanceCsv = ['書類ID,書類番号,書類種別,入庫区分,ステータス,顧客名,車名,登録番号,入庫日,出庫予定日,支払期限,税率,小計,消費税,合計,明細,備考,明細詳細', [maintenanceId, '9002', '整備請求書', '一般整備', '下書き', customerName, '', '', '', '', '', '10', '2000', '0', '2000', '移行整備', 'ABACUSテスト', ''].join(','), [duplicateMaintenanceId, '9002', '整備請求書', '一般整備', '下書き', customerName, '', '', '', '', '', '10', '', '0', '', '移行整備2', 'ABACUSテスト', ''].join(',')].join('\n')
  const linksJson = JSON.stringify({ version: 1, kind: 'abacus-export-import-document-links', status: 'finalization-preview', documents: [
    { documentKey: `販売書類|final|${salesNumber}`, documentId: salesId, documentKind: '販売書類', documentNumber: salesNumber, customerId: customer, customerName, vehicleId: null, vehicleName: null, vehicleless: true, sourceLocation: 'hanbai.csv #1', warning: '' },
    { documentKey: '整備書類|final|9002', documentId: maintenanceId, documentKind: '整備書類', documentNumber: '9002', customerId: customer, customerName, vehicleId: null, vehicleName: null, vehicleless: true, sourceLocation: 'seibi.csv #1', warning: '' },
    { documentKey: '整備書類|final|9002|2', documentId: duplicateMaintenanceId, documentKind: '整備書類', documentNumber: '9002', customerId: customer, customerName, vehicleId: null, vehicleName: null, vehicleless: true, sourceLocation: 'seibi.csv #2', warning: '' },
  ], excludedDocumentKeys: [] })
  const files = [
    ['customers.csv', customersCsv],
    ['vehicles.csv', vehiclesCsv],
    ['sales.csv', salesCsv],
    ['maintenance.csv', maintenanceCsv],
    ['document-links.json', linksJson],
  ] as const
  const dataFiles = await Promise.all(files.map(async ([fileName, content]) => ({ fileName, sizeBytes: byteLength(content), sha256: await sha256(content) })))
  const manifest = JSON.stringify({ version: 1, kind: 'abacus-export-import-final-package', status: 'registration-preview', summary: { customerRowCount: 1, vehicleRowCount: 0, salesRowCount: 1, maintenanceRowCount: 2, vehiclelessDocumentCount: 3, excludedDocumentCount: 0, imageCount: 0 }, dataFiles, warnings: [], groups: [{ groupKey: 'same-name:test', origin: 'same-name', approved: true, sourceCustomerIds: ['source-test'], customerId: customer, customerName }], documents: [
    { documentKey: `販売書類|final|${salesNumber}`, documentId: salesId, kind: '販売書類', customerId: customer, vehicleId: null, sourceLocation: 'hanbai.csv #1', vehicleless: true },
    { documentKey: '整備書類|final|9002', documentId: maintenanceId, kind: '整備書類', customerId: customer, vehicleId: null, sourceLocation: 'seibi.csv #1', vehicleless: true },
    { documentKey: '整備書類|final|9002|2', documentId: duplicateMaintenanceId, kind: '整備書類', customerId: customer, vehicleId: null, sourceLocation: 'seibi.csv #2', vehicleless: true },
  ], excludedDocumentKeys: [] })
  return { manifest, manifestSha256: await sha256(manifest), files, customerId: customer, salesId, maintenanceId, duplicateMaintenanceId }
}

async function createLargeGraphFinalPackage(customerCount: number) {
  const customerRows = Array.from({ length: customerCount }, (_, index) => {
    const id = `abacus-customer-large-${index + 1}`
    return { id, number: `ABACUS-LARGE-${index + 1}`, name: `大規模登録テスト顧客${index + 1}`, sourceId: `source-large-${index + 1}` }
  })
  const customersCsv = ['顧客ID,顧客番号,顧客名,ふりがな,電話番号,メールアドレス,郵便番号,住所,メモ,車両台数', ...customerRows.map((row) => [row.id, row.number, row.name, '', '', '', '', '', '', '0'].join(','))].join('\n')
  const files = [
    ['customers.csv', customersCsv],
    ['vehicles.csv', '車両ID,顧客ID,顧客名,メーカー,車名,型式,登録番号,車台番号,年式,車検満了日,走行距離,車体色,排気量,ミッション,記録簿,備考'],
    ['sales.csv', '書類ID,書類番号,書類種別,ステータス,顧客名,車名,登録番号,発行日,支払期限,税率,小計,消費税,合計,明細,備考,明細詳細'],
    ['maintenance.csv', '書類ID,書類番号,書類種別,入庫区分,ステータス,顧客名,車名,登録番号,入庫日,出庫予定日,支払期限,税率,小計,消費税,合計,明細,備考,明細詳細'],
    ['document-links.json', JSON.stringify({ version: 1, kind: 'abacus-export-import-document-links', status: 'finalization-preview', documents: [], excludedDocumentKeys: [] })],
  ] as const
  const dataFiles = await Promise.all(files.map(async ([fileName, content]) => ({ fileName, sizeBytes: byteLength(content), sha256: await sha256(content) })))
  const manifest = JSON.stringify({ version: 1, kind: 'abacus-export-import-final-package', status: 'registration-preview', summary: { customerRowCount: customerCount, vehicleRowCount: 0, salesRowCount: 0, maintenanceRowCount: 0, vehiclelessDocumentCount: 0, excludedDocumentCount: 0 }, dataFiles, warnings: [], groups: customerRows.map((row) => ({ groupKey: row.id, origin: 'single', approved: true, sourceCustomerIds: [row.sourceId], customerId: row.id, customerName: row.name })), documents: [], excludedDocumentKeys: [] })
  return { manifest, manifestSha256: await sha256(manifest), files }
}

async function postGraphFinalRegistration(packageFiles: Awaited<ReturnType<typeof createGraphFinalPackage>>, confirmation: string) {
  const formData = new FormData()
  formData.append('manifest', new File([packageFiles.manifest], 'manifest.json', { type: 'application/json' }))
  for (const [fileName, content] of packageFiles.files) formData.append(fileName === 'document-links.json' ? 'documentLinks' : fileName.replace('.csv', ''), new File([content], fileName, { type: 'text/plain' }))
  formData.append('manifestSha256', packageFiles.manifestSha256)
  formData.append('confirmation', confirmation)
  const probe = new Request('https://example.com/api/import/abacus-registration/commit', { method: 'POST', headers: authHeaders(), body: formData })
  const body = await probe.arrayBuffer()
  const headers = new Headers(probe.headers)
  headers.set('Content-Length', String(body.byteLength))
  return SELF.fetch(new Request(probe.url, { method: 'POST', headers, body }))
}

async function createReadyEnvelope(packageFiles: Awaited<ReturnType<typeof createGraphFinalPackage>>) {
  const manifest = JSON.parse(packageFiles.manifest) as { summary: Record<string, number> }
  const files = new Map<string, string>([
    ['data/customers.csv', packageFiles.files.find(([fileName]) => fileName === 'customers.csv')?.[1] ?? ''],
    ['data/vehicles.csv', packageFiles.files.find(([fileName]) => fileName === 'vehicles.csv')?.[1] ?? ''],
    ['data/sales-documents.csv', packageFiles.files.find(([fileName]) => fileName === 'sales.csv')?.[1] ?? ''],
    ['data/maintenance-documents.csv', packageFiles.files.find(([fileName]) => fileName === 'maintenance.csv')?.[1] ?? ''],
    ['mappings/document-links.json', packageFiles.files.find(([fileName]) => fileName === 'document-links.json')?.[1] ?? ''],
    ['mappings/customer-merges.json', JSON.stringify([{ groupKey: 'same-name:test', origin: 'same-name', approved: true, sourceCustomerIds: ['source-test'], customerId: 'merge-preview:same-name:最終登録テスト', customerName: '最終登録テスト顧客' }])],
    ['reports/excluded-documents.json', '[]'],
    ['reports/unresolved-items.json', '[]'],
    ['reports/image-acquisition-report.json', JSON.stringify({ method: 'fp5-vehicle-record', status: 'completed', imageCount: 0 })],
  ])
  const descriptors = await Promise.all([...files.entries()].map(async ([path, content]) => ({ path, sizeBytes: byteLength(content), sha256: await sha256(content) })))
  const rootManifest = JSON.stringify({ version: 1, kind: 'abacus-import', status: 'ready', packageId: 'test-package', readyPath: 'ready', readyManifest: 'ready/manifest.json', imageAcquisitionMethod: 'fp5-vehicle-record' })
  const readyManifest = JSON.stringify({ version: 1, kind: 'abacus-import-ready', status: 'ready', packageId: 'test-package', imageAcquisitionMethod: 'fp5-vehicle-record', summary: { customerCount: manifest.summary.customerRowCount, vehicleCount: manifest.summary.vehicleRowCount, salesDocumentCount: manifest.summary.salesRowCount, maintenanceDocumentCount: manifest.summary.maintenanceRowCount, vehiclelessDocumentCount: manifest.summary.vehiclelessDocumentCount, excludedDocumentCount: manifest.summary.excludedDocumentCount, imageCount: 0 }, files: descriptors })
  return { rootManifest, readyManifest, files, descriptors, rootManifestSha256: await sha256(rootManifest), readyManifestSha256: await sha256(readyManifest) }
}

async function postGraphFinalPreview(packageFiles: Awaited<ReturnType<typeof createGraphFinalPackage>>, ready: Awaited<ReturnType<typeof createReadyEnvelope>>) {
  const formData = new FormData()
  formData.append('manifest', new File([packageFiles.manifest], 'manifest.json', { type: 'application/json' }))
  for (const [fileName, content] of packageFiles.files) formData.append(fileName === 'document-links.json' ? 'documentLinks' : fileName.replace('.csv', ''), new File([content], fileName, { type: 'text/plain' }))
  formData.append('manifestSha256', packageFiles.manifestSha256)
  formData.append('packageManifest', new File([ready.rootManifest], 'abacus-import.json', { type: 'application/json' }))
  formData.append('readyManifest', new File([ready.readyManifest], 'manifest.json', { type: 'application/json' }))
  formData.append('packageManifestSha256', ready.rootManifestSha256)
  formData.append('readyManifestSha256', ready.readyManifestSha256)
  const readyFields: Record<string, string> = {
    'readyCustomers': 'data/customers.csv',
    'readyVehicles': 'data/vehicles.csv',
    'readySales': 'data/sales-documents.csv',
    'readyMaintenance': 'data/maintenance-documents.csv',
    'readyDocumentLinks': 'mappings/document-links.json',
    'readyCustomerMerges': 'mappings/customer-merges.json',
    'readyExcludedDocuments': 'reports/excluded-documents.json',
    'readyUnresolvedItems': 'reports/unresolved-items.json',
    'readyImageAcquisitionReport': 'reports/image-acquisition-report.json',
  }
  for (const [field, path] of Object.entries(readyFields)) formData.append(field, new File([ready.files.get(path) ?? ''], path, { type: 'text/plain' }))
  formData.append('readyImageDescriptors', '[]')
  const probe = new Request('https://example.com/api/import/abacus-registration/preview', { method: 'POST', headers: authHeaders(), body: formData })
  const body = await probe.arrayBuffer()
  const headers = new Headers(probe.headers)
  headers.set('Content-Length', String(body.byteLength))
  return SELF.fetch(new Request(probe.url, { method: 'POST', headers, body }))
}

function authHeaders() {
  return new Headers({
    Authorization: `Bearer ${emulatorToken(testUid)}`,
    'X-Organization-Id': testOrganizationId,
  })
}

function emulatorToken(uid: string) {
  const projectId = env.FIREBASE_PROJECT_ID ?? 'vehicle-management-64'
  const payload = { sub: uid, aud: projectId, iss: `https://securetoken.google.com/${projectId}`, exp: Math.floor(Date.now() / 1000) + 3600, email: `${uid}@example.com`, email_verified: true, name: uid }
  return `${base64Url(JSON.stringify({ alg: 'none', typ: 'JWT' }))}.${base64Url(JSON.stringify(payload))}.cli-test`
}

function base64Url(value: string) {
  return btoa(value).replaceAll('+', '-').replaceAll('/', '_').replace(/=+$/u, '')
}

async function sha256(value: string) {
  return Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(value))), (byte) => byte.toString(16).padStart(2, '0')).join('').toUpperCase()
}

function byteLength(value: string) {
  return new TextEncoder().encode(value).byteLength
}

function csvCell(value: string) {
  return value.includes(',') || value.includes('"') || value.includes('\n') ? `"${value.replaceAll('"', '""')}"` : value
}

async function countRows(table: 'customers' | 'vehicles') {
  const column = table === 'customers' ? 'organization_id' : 'organization_id'
  const result = await env.DB.prepare(`SELECT COUNT(*) AS count FROM ${table} WHERE ${column} = ?`).bind(testOrganizationId).first<{ count: number }>()
  return Number(result?.count ?? 0)
}

async function countGraphFinalRows(packageFiles: Awaited<ReturnType<typeof createGraphFinalPackage>>) {
  const [customers, sales, maintenance] = await Promise.all([
    env.DB.prepare('SELECT COUNT(*) AS count FROM customers WHERE organization_id = ? AND id = ?').bind(testOrganizationId, packageFiles.customerId).first<{ count: number }>(),
    env.DB.prepare('SELECT COUNT(*) AS count FROM sales_documents WHERE organization_id = ? AND id = ?').bind(testOrganizationId, packageFiles.salesId).first<{ count: number }>(),
    env.DB.prepare('SELECT COUNT(*) AS count FROM maintenance_documents WHERE organization_id = ? AND id IN (?, ?)').bind(testOrganizationId, packageFiles.maintenanceId, packageFiles.duplicateMaintenanceId).first<{ count: number }>(),
  ])
  return {
    customers: Number(customers?.count ?? 0),
    sales: Number(sales?.count ?? 0),
    maintenance: Number(maintenance?.count ?? 0),
  }
}
