import { apiFetch } from './api'

export type SyncPreviewDocumentType = 'maintenance' | 'sales'

export type SyncPreviewCustomerDiff = {
  field: string
  label: string
  currentValue: string
  documentValue: string
  isConflict: boolean
  isAttention: boolean
}

export type SyncPreviewVehicleDiff = {
  field: string
  label: string
  currentValue: string
  documentValue: string
  isConflict: boolean
  isAttention: boolean
}

export type SyncPreviewMileageDiff = {
  currentValue: number | null
  openedValue: number | null
  documentValue: number
  isChanged: boolean
  willUpdateVehicle: boolean
}

export type SyncPreviewResponse = {
  hasDifferences: boolean
  isOlderThanLatestDocument: boolean
  customerDiffs: SyncPreviewCustomerDiff[]
  vehicleDiffs: SyncPreviewVehicleDiff[]
  mileageDiff?: SyncPreviewMileageDiff
  expectedCustomerUpdatedAt: string | null
  expectedVehicleUpdatedAt: string | null
}

export type SyncPreviewInput = {
  documentType: SyncPreviewDocumentType
  documentId?: string
  customerId?: string
  vehicleId?: string
  customerOverride?: Record<string, unknown>
  vehicleOverride?: Record<string, unknown>
  issuedAt?: string
  openedCustomerUpdatedAt?: string
  openedVehicleUpdatedAt?: string
  mileageContext?: {
    openedMileage?: number | null
  }
}

export async function fetchSyncPreview(input: SyncPreviewInput): Promise<SyncPreviewResponse> {
  return apiFetch<SyncPreviewResponse>('/api/sync-preview', {
    method: 'POST',
    body: JSON.stringify(input),
  })
}
