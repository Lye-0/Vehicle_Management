import { env } from 'cloudflare:test'
import { afterAll, beforeEach, describe, expect, it } from 'vitest'
import { paymentRecordUpsertStatement } from '../payment-routes'

const organizationId = 'org-payment-batch-test'
const documentType = '販売請求書'
const documentId = 'sales-payment-batch-test'

describe('payment record batch aggregation', () => {
  beforeEach(async () => {
    await env.DB.prepare('DELETE FROM payment_entries WHERE organization_id = ?').bind(organizationId).run()
    await env.DB.prepare('DELETE FROM payment_records WHERE organization_id = ?').bind(organizationId).run()
  })

  afterAll(async () => {
    await env.DB.prepare('DELETE FROM payment_entries WHERE organization_id = ?').bind(organizationId).run()
    await env.DB.prepare('DELETE FROM payment_records WHERE organization_id = ?').bind(organizationId).run()
  })

  it('recalculates the aggregate record in the same D1 batch', async () => {
    await env.DB.prepare(`
      INSERT INTO payment_entries (id, organization_id, document_type, document_id, amount, payment_date, method, note)
      VALUES (?, ?, ?, ?, ?, ?, ?, ?)
    `).bind('payment-entry-batch-1', organizationId, documentType, documentId, 300, '2026-08-01', '現金', '先入金').run()
    await env.DB.prepare(`
      INSERT INTO payment_entries (id, organization_id, document_type, document_id, amount, payment_date, method, note)
      VALUES (?, ?, ?, ?, ?, ?, ?, ?)
    `).bind('payment-entry-batch-2', organizationId, documentType, documentId, 200, '2026-08-02', '銀行振込', '後入金').run()

    await env.DB.batch([paymentRecordUpsertStatement(env, 1000, documentType, documentId, organizationId, '2026-08-03T00:00:00.000Z')])

    const record = await env.DB.prepare('SELECT paid_amount, payment_date, method, note FROM payment_records WHERE organization_id = ? AND document_type = ? AND document_id = ?').bind(organizationId, documentType, documentId).first<{ paid_amount: number; payment_date: string; method: string; note: string }>()
    expect(record).toEqual({ paid_amount: 500, payment_date: '2026-08-02', method: '銀行振込', note: '後入金' })
  })
})
