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

async function countRows(table: 'customers' | 'vehicles') {
  const column = table === 'customers' ? 'organization_id' : 'organization_id'
  const result = await env.DB.prepare(`SELECT COUNT(*) AS count FROM ${table} WHERE ${column} = ?`).bind(testOrganizationId).first<{ count: number }>()
  return Number(result?.count ?? 0)
}
