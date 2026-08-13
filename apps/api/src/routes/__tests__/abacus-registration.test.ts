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

    await env.DB.prepare('INSERT INTO sales_documents (id, organization_id, number, type, status, customer_id, vehicle_id, issued_at, details_json) VALUES (?, ?, ?, ?, ?, ?, NULL, ?, ?)').bind('abacus-unverified-vehicleless', testOrganizationId, 'UNVERIFIED-VEHICLELESS', '請求書', '下書き', packageFiles.customerId, '2026-01-01', '{}').run()
    const vehiclelessResponse = await SELF.fetch(new Request(`https://example.com/api/customers/${encodeURIComponent(packageFiles.customerId)}/vehicleless-documents`, { headers: authHeaders() }))
    expect(vehiclelessResponse.status).toBe(200)
    const vehiclelessBody = await vehiclelessResponse.json() as { customerId: string; salesCount: number; maintenanceCount: number; documents: Array<{ kind: string; number: string; sourceLocation: string }> }
    expect(vehiclelessBody).toMatchObject({
      customerId: packageFiles.customerId,
      salesCount: 1,
      maintenanceCount: 2,
      documents: expect.arrayContaining([
        expect.objectContaining({ kind: 'sales', number: '9001', sourceLocation: 'hanbai.csv #1' }),
        expect.objectContaining({ kind: 'maintenance', number: '9002', sourceLocation: 'seibi.csv #1' }),
      ]),
    })
    expect(vehiclelessBody.documents).toHaveLength(3)
    expect(vehiclelessBody.documents.some((document) => document.number === 'UNVERIFIED-VEHICLELESS')).toBe(false)
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

  it('registers Gate19 maintenance detail lines, amount-only rows, and source dates', async () => {
    const detailJson = JSON.stringify({
      version: 1,
      kind: 'abacus-detail-lines',
      sourceFile: 'abx-cs-sb.ucs',
      recordIdHex: 'C10121',
      documentNumber: '9004',
      customerName: '整備明細登録テスト顧客',
      vehicleName: 'ストリーム RSZ',
      registrationNumber: '大阪536ね1227',
      chassisNumber: 'RN6-3118934',
      lines: [
        { description: null, quantity: 19, unit: null, unitPrice: 3000, partAmount: 57000, technicalFees: null, summary: '12/9～12/27', sourceRowIndex: 2 },
        { description: 'マット', quantity: 1, unit: null, unitPrice: 2000, partAmount: 2000, technicalFees: null, summary: null, sourceRowIndex: 3 },
        { description: 'ガラス', quantity: 1, unit: null, unitPrice: 6000, partAmount: 6000, technicalFees: null, summary: null, sourceRowIndex: 4 },
      ],
      financialLines: [],
      partsSubtotal: 65000,
      technicalSubtotal: null,
      abacusSubtotal: 65000,
      abacusTotal: null,
      abacusTax: null,
      abacusTaxRate: null,
      detailAmount: 65000,
      excludedDetailCount: 15,
      amountOnlyRowCount: 1,
      matchStatus: 'matched',
      warning: '',
    })
    const packageFiles = await createGraphFinalPackage('gate19-maintenance', '', detailJson)
    const response = await postGraphFinalRegistration(packageFiles, 'ABACUS登録を実行')
    expect(response.status).toBe(200)

    const document = await env.DB.prepare('SELECT intake_date AS intakeDate, planned_release_date AS plannedReleaseDate, issued_at AS issuedAt, subtotal, tax, total FROM maintenance_documents WHERE id = ? AND organization_id = ?').bind(packageFiles.maintenanceId, testOrganizationId).first<{ intakeDate: string; plannedReleaseDate: string | null; issuedAt: string; subtotal: number; tax: number; total: number }>()
    expect(document).toEqual({ intakeDate: '2014-01-17', plannedReleaseDate: null, issuedAt: '2014-01-17', subtotal: 61905, tax: 3095, total: 65000 })
    const items = await env.DB.prepare('SELECT description, quantity, unit, unit_price AS unitPrice, technical_fee AS technicalFee, summary, amount FROM maintenance_items WHERE document_id = ? AND organization_id = ? ORDER BY sort_order').bind(packageFiles.maintenanceId, testOrganizationId).all<{ description: string; quantity: number; unit: string; unitPrice: number; technicalFee: number; summary: string; amount: number }>()
    expect(items.results).toEqual([
      { description: '', quantity: 19, unit: '式', unitPrice: 3000, technicalFee: 0, summary: '12/9~12/27', amount: 57000 },
      { description: 'マット', quantity: 1, unit: '式', unitPrice: 2000, technicalFee: 0, summary: '', amount: 2000 },
      { description: 'ガラス', quantity: 1, unit: '式', unitPrice: 6000, technicalFee: 0, summary: '', amount: 6000 },
    ])
  })

  it('registers Gate19 maintenance technical fees and planned release date', async () => {
    const detailJson = JSON.stringify({
      version: 1,
      kind: 'abacus-detail-lines',
      sourceFile: 'abx-cs-sb.ucs',
      recordIdHex: 'C103D1',
      documentNumber: '9006',
      customerName: '整備技術料登録テスト顧客',
      vehicleName: 'ヴェゼルハイブリッド',
      registrationNumber: '大阪343た1227',
      chassisNumber: 'RU3-1206673',
      lines: [{ description: 'エンジンS/W　交換', quantity: 1, unit: null, unitPrice: 13800, partAmount: 13800, technicalFees: 2000, summary: null, sourceRowIndex: 2 }],
      financialLines: [],
      partsSubtotal: 13800,
      technicalSubtotal: 2000,
      abacusSubtotal: 15800,
      abacusTotal: null,
      abacusTax: null,
      abacusTaxRate: null,
      detailAmount: 15800,
      excludedDetailCount: 17,
      amountOnlyRowCount: 0,
      matchStatus: 'matched',
      warning: '',
    })
    const packageFiles = await createGraphFinalPackage('gate19-maintenance-technical', '', detailJson)
    const response = await postGraphFinalRegistration(packageFiles, 'ABACUS登録を実行')
    expect(response.status).toBe(200)

    const document = await env.DB.prepare('SELECT intake_date AS intakeDate, planned_release_date AS plannedReleaseDate, issued_at AS issuedAt, subtotal, tax, total FROM maintenance_documents WHERE id = ? AND organization_id = ?').bind(packageFiles.maintenanceId, testOrganizationId).first<{ intakeDate: string; plannedReleaseDate: string | null; issuedAt: string; subtotal: number; tax: number; total: number }>()
    expect(document).toEqual({ intakeDate: '2019-10-09', plannedReleaseDate: '2019-10-11', issuedAt: '2019-10-09', subtotal: 15800, tax: 1580, total: 17380 })
    const items = await env.DB.prepare('SELECT description, quantity, unit, unit_price AS unitPrice, technical_fee AS technicalFee, amount FROM maintenance_items WHERE document_id = ? AND organization_id = ? ORDER BY sort_order').bind(packageFiles.maintenanceId, testOrganizationId).all<{ description: string; quantity: number; unit: string; unitPrice: number; technicalFee: number; amount: number }>()
    expect(items.results).toEqual([{ description: 'エンジンS/W 交換', quantity: 1, unit: '式', unitPrice: 13800, technicalFee: 2000, amount: 15800 }])
  })

  it('restores Gate24 document types, point-inspection category, and date-based status', async () => {
    const packageFiles = await createGate24Package()
    const response = await postGraphFinalRegistration(packageFiles, 'ABACUS登録を実行')
    expect(response.status).toBe(200)
    await expect(response.json()).resolves.toMatchObject({
      status: 'committed',
      importedCompletedDocumentCount: 2,
      importedDraftDocumentCount: 1,
      importedDateWarningDocumentCount: 1,
      importedClassificationWarningDocumentCount: 0,
    })

    const sales = await env.DB.prepare('SELECT type, status FROM sales_documents WHERE id = ? AND organization_id = ?').bind(packageFiles.salesId, testOrganizationId).first<{ type: string; status: string }>()
    expect(sales).toEqual({ type: '見積書', status: '完了' })
    const maintenance = await env.DB.prepare('SELECT type, category, status FROM maintenance_documents WHERE id = ? AND organization_id = ?').bind(packageFiles.maintenanceId, testOrganizationId).first<{ type: string; category: string; status: string }>()
    expect(maintenance).toEqual({ type: '整備見積書', category: '一般整備', status: '完了' })
    const draftMaintenance = await env.DB.prepare('SELECT status FROM maintenance_documents WHERE id = ? AND organization_id = ?').bind(packageFiles.duplicateMaintenanceId, testOrganizationId).first<{ status: string }>()
    expect(draftMaintenance).toEqual({ status: '下書き' })
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

async function createGraphFinalPackage(suffix = 'finaltest', salesDetailJson = '', maintenanceDetailJson = '') {
  const customer = suffix === 'finaltest' ? 'merge-preview:same-name:最終登録テスト' : `merge-preview:same-name:最終登録テスト-${suffix}`
  const customerName = '最終登録テスト顧客'
  const salesId = suffix === 'finaltest' ? 'abacus-sales-finaltest' : `abacus-sales-${suffix}`
  const maintenanceId = suffix === 'finaltest' ? 'abacus-maintenance-finaltest' : `abacus-maintenance-${suffix}`
  const duplicateMaintenanceId = suffix === 'finaltest' ? 'abacus-maintenance-finaltest-2' : `abacus-maintenance-${suffix}-2`
  const technicalMaintenance = maintenanceDetailJson.includes('"documentNumber":"9006"')
  const maintenanceNumber = maintenanceDetailJson ? (technicalMaintenance ? '9006' : '9004') : '9002'
  const duplicateMaintenanceNumber = maintenanceDetailJson ? (technicalMaintenance ? '9007' : '9005') : '9002'
  const maintenanceIntakeDate = technicalMaintenance ? '2019-10-09' : '2014-01-17'
  const maintenanceCompletionDate = technicalMaintenance ? '2019-10-11' : ''
  const maintenanceTaxRate = technicalMaintenance ? '10' : '5'
  const maintenanceSubtotal = technicalMaintenance ? '15800' : '61905'
  const maintenanceTax = technicalMaintenance ? '1580' : '3095'
  const maintenanceTotal = technicalMaintenance ? '17380' : '65000'
  const customersCsv = ['顧客ID,顧客番号,顧客名,ふりがな,電話番号,メールアドレス,郵便番号,住所,メモ,車両台数', [customer, 'ABACUS-CUSTOMER-NUMBER-', customerName, '', '', '', '', '', '', '0'].join(',')].join('\n')
  const vehiclesCsv = '車両ID,顧客ID,顧客名,メーカー,車名,型式,登録番号,車台番号,年式,車検満了日,走行距離,車体色,排気量,ミッション,記録簿,備考'
  const salesNumber = salesDetailJson ? '9003' : '9001'
  const salesCsv = ['書類ID,書類番号,書類種別,ステータス,顧客名,車名,登録番号,発行日,支払期限,税率,小計,消費税,合計,明細,備考,明細詳細', [salesId, salesNumber, '請求書', '下書き', customerName, '', '', '2026-01-02', '', '10', salesDetailJson ? '1184390' : '1000', salesDetailJson ? '58303' : '0', salesDetailJson ? '1242693' : '1000', '移行販売', 'ABACUSテスト', csvCell(salesDetailJson)].join(',')].join('\n')
  const maintenanceCsv = ['書類ID,書類番号,書類種別,入庫区分,ステータス,顧客名,車名,登録番号,入庫日,出庫予定日,支払期限,税率,小計,消費税,合計,明細,備考,明細詳細', [maintenanceId, maintenanceNumber, '整備請求書', '一般整備', '下書き', customerName, '', '', maintenanceDetailJson ? maintenanceIntakeDate : '', maintenanceDetailJson ? maintenanceCompletionDate : '', '', maintenanceDetailJson ? maintenanceTaxRate : '10', maintenanceDetailJson ? maintenanceSubtotal : '2000', maintenanceDetailJson ? maintenanceTax : '0', maintenanceDetailJson ? maintenanceTotal : '2000', '移行整備', 'ABACUSテスト', csvCell(maintenanceDetailJson)].join(','), [duplicateMaintenanceId, duplicateMaintenanceNumber, '整備請求書', '一般整備', '下書き', customerName, '', '', '', '', '', '10', '', '0', '', '移行整備2', 'ABACUSテスト', ''].join(',')].join('\n')
  const linksJson = JSON.stringify({ version: 1, kind: 'abacus-export-import-document-links', status: 'finalization-preview', documents: [
    { documentKey: `販売書類|final|${salesNumber}`, documentId: salesId, documentKind: '販売書類', documentNumber: salesNumber, customerId: customer, customerName, vehicleId: null, vehicleName: null, vehicleless: true, sourceLocation: 'hanbai.csv #1', warning: '' },
    { documentKey: `整備書類|final|${maintenanceNumber}`, documentId: maintenanceId, documentKind: '整備書類', documentNumber: maintenanceNumber, customerId: customer, customerName, vehicleId: null, vehicleName: null, vehicleless: true, sourceLocation: 'seibi.csv #1', warning: '' },
    { documentKey: `整備書類|final|${duplicateMaintenanceNumber}|2`, documentId: duplicateMaintenanceId, documentKind: '整備書類', documentNumber: duplicateMaintenanceNumber, customerId: customer, customerName, vehicleId: null, vehicleName: null, vehicleless: true, sourceLocation: 'seibi.csv #2', warning: '' },
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
    { documentKey: `整備書類|final|${maintenanceNumber}`, documentId: maintenanceId, kind: '整備書類', customerId: customer, vehicleId: null, sourceLocation: 'seibi.csv #1', vehicleless: true },
    { documentKey: `整備書類|final|${duplicateMaintenanceNumber}|2`, documentId: duplicateMaintenanceId, kind: '整備書類', customerId: customer, vehicleId: null, sourceLocation: 'seibi.csv #2', vehicleless: true },
  ], excludedDocumentKeys: [] })
  return { manifest, manifestSha256: await sha256(manifest), files, customerId: customer, salesId, maintenanceId, duplicateMaintenanceId }
}

async function createGate24Package() {
  const basePackage = await createGraphFinalPackage('gate24-classification')
  const importBaseDate = '2026-08-14'
  const files = basePackage.files.map(([fileName, content]) => {
    if (fileName === 'sales.csv') {
      const rows = content.split('\n')
      const fields = rows[1].split(',')
      fields[2] = '見積書'
      fields[3] = '完了'
      fields[7] = '2025-01-01'
      rows[1] = fields.join(',')
      return [fileName, rows.join('\n')] as const
    }
    if (fileName === 'maintenance.csv') {
      const rows = content.split('\n')
      const fields = rows[1].split(',')
      fields[2] = '整備見積書'
      fields[3] = '点検'
      fields[4] = '完了'
      fields[8] = '2025-01-01'
      rows[1] = fields.join(',')
      return [fileName, rows.join('\n')] as const
    }
    return [fileName, content] as const
  })
  const manifestValue = JSON.parse(basePackage.manifest) as { importBaseDate?: string; dataFiles: Array<{ fileName: string; sizeBytes: number; sha256: string }> }
  manifestValue.importBaseDate = importBaseDate
  manifestValue.dataFiles = await Promise.all(files.map(async ([fileName, content]) => ({ fileName, sizeBytes: byteLength(content), sha256: await sha256(content) })))
  const manifest = JSON.stringify(manifestValue)
  return { ...basePackage, manifest, manifestSha256: await sha256(manifest), files }
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
