export type DocumentNumberPrefix = 'S' | 'M'

const documentNumberTimeZone = 'Asia/Tokyo'

export function getDocumentNumberPeriod(date = new Date()) {
  const parts = new Intl.DateTimeFormat('en-US', { timeZone: documentNumberTimeZone, year: 'numeric', month: '2-digit' }).formatToParts(date)
  const year = Number(parts.find((part) => part.type === 'year')?.value)
  const month = Number(parts.find((part) => part.type === 'month')?.value)
  if (!Number.isInteger(year) || !Number.isInteger(month) || month < 1 || month > 12) throw new Error('書類番号の作成年月を取得できません。')
  return { year, month }
}

export function formatDocumentNumber(prefix: DocumentNumberPrefix, year: number, month: number, sequence: number) {
  return `${prefix}-${year}-${String(month).padStart(2, '0')}${String(sequence).padStart(3, '0')}`
}

export async function nextDocumentNumber(database: D1Database, organizationId: string, prefix: DocumentNumberPrefix, now = new Date()) {
  const { year, month } = getDocumentNumberPeriod(now)
  const row = await database.prepare(`
    INSERT INTO document_number_sequences (organization_id, prefix, year, month, next_sequence, updated_at)
    VALUES (?, ?, ?, ?, 2, ?)
    ON CONFLICT (organization_id, prefix, year, month)
    DO UPDATE SET next_sequence = document_number_sequences.next_sequence + 1, updated_at = excluded.updated_at
    RETURNING next_sequence - 1 AS sequence
  `).bind(organizationId, prefix, year, month, now.toISOString()).first<{ sequence: number }>()
  const sequence = Number(row?.sequence)
  if (!Number.isInteger(sequence) || sequence < 1) throw new Error('書類番号の連番を発行できません。')
  return formatDocumentNumber(prefix, year, month, sequence)
}
