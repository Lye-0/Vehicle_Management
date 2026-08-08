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

export type DuplicateCustomerCandidate = {
  id: string
  name: string
  phone: string | null
  email: string | null
  matchReason: 'phone' | 'email'
  strength: 'strong'
}

export type DuplicateVehicleCandidate = {
  id: string
  maker: string | null
  name: string
  registrationNumber: string | null
  chassisNumber: string | null
  matchReason: 'chassis_number' | 'registration_number'
  strength: 'strong'
}

export type SyncPreviewResponse = {
  hasDifferences: boolean
  isOlderThanLatestDocument: boolean
  customerDiffs: SyncPreviewCustomerDiff[]
  vehicleDiffs: SyncPreviewVehicleDiff[]
  mileageDiff?: SyncPreviewMileageDiff
  expectedCustomerUpdatedAt: string | null
  expectedVehicleUpdatedAt: string | null
  duplicateCustomers?: DuplicateCustomerCandidate[]
  duplicateVehicles?: DuplicateVehicleCandidate[]
}

export type SyncPreviewInput = {
  documentType: SyncPreviewDocumentType
  documentId?: string
  customerId?: string
  vehicleId?: string
  newCustomer?: { name: string; nameKana?: string; phone?: string; email?: string; postalCode?: string; address?: string; birthDate?: string; employer?: string }
  newVehicle?: { maker: string; name: string; model?: string; registrationNumber?: string; chassisNumber?: string; modelYear?: number; inspectionDate?: string; bodyColor?: string; transmission?: string; mileage?: number; displacement?: number }
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
