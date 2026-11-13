import { HttpError } from '../http'
import type { DuplicateVehicle } from './master-sync-helpers'

export function confirmedVehicleTransfer(value: unknown, matches: DuplicateVehicle[]) {
  if (!value || typeof value !== 'object' || !('previousCustomerId' in value)) return null
  const candidate = matches.find(item => item.matchReason === 'chassis_number' &&
    'confirmedVehicleId' in value && item.id === value.confirmedVehicleId)
  if (!candidate || !candidate.customerId || candidate.customerId !== value.previousCustomerId ||
      !candidate.updatedAt || !('expectedVehicleUpdatedAt' in value) || candidate.updatedAt !== value.expectedVehicleUpdatedAt) {
    throw new HttpError(409, '車両の登録情報が変わりました。もう一度保存して所有者を確認してください。')
  }
  return { id: candidate.id, customerId: candidate.customerId, updatedAt: candidate.updatedAt }
}

export function transferStatements(db: D1Database, transfer: NonNullable<ReturnType<typeof confirmedVehicleTransfer>>, organizationId: string, customerId: string, now: string) {
  // A stale owner/version must fail the entire document batch (customer_id is NOT NULL).
  return [
    db.prepare(`UPDATE vehicles SET customer_id = CASE WHEN customer_id = ? AND updated_at = ? AND deleted_at IS NULL
      AND EXISTS (SELECT 1 FROM customers WHERE id = ? AND organization_id = ? AND deleted_at IS NULL)
      THEN ? ELSE NULL END, updated_at = ? WHERE id = ? AND organization_id = ?`)
      .bind(transfer.customerId, transfer.updatedAt, customerId, organizationId, customerId, now, transfer.id, organizationId),
    db.prepare(`UPDATE inspection_schedules SET customer_id = ?, updated_at = ?
      WHERE vehicle_id = ? AND organization_id = ? AND status = '予定' AND deletion_batch_id IS NULL`)
      .bind(customerId, now, transfer.id, organizationId),
  ]
}
