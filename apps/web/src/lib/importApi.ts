import { apiFetch } from './api'

export type CsvImportResource = 'customers' | 'vehicles' | 'sales' | 'maintenance' | 'payments'

export type CsvImportPreview = {
  resource: CsvImportResource
  totalRows: number
  previewRows: Array<Record<string, string>>
  errors: Array<{ row: number; message: string }>
}

export type CsvImportResult = {
  resource: CsvImportResource
  imported: number
  updated: number
  skipped: number
  errors: Array<{ row: number; message: string }>
}

export type AbacusRegistrationCommitResult = {
  status: 'committed'
  manifestSha256: string
  customerCount: number
  vehicleCount: number
  imageCount: number
  customers: { imported: number; updated: number }
  vehicles: { imported: number; updated: number }
}

export type AbacusRegistrationImageResult = {
  status: 'uploaded' | 'already-uploaded'
  manifestSha256: string
  file: { id: string; name: string; type: string; contentType: string; size: number; createdAt: string } | null
}

export type AbacusGraphFinalRegistrationResult = {
  status: 'committed'
  manifestSha256: string
  customerCount: number
  vehicleCount: number
  salesCount: number
  maintenanceCount: number
  vehiclelessDocumentCount: number
  excludedDocumentCount: number
  numberAdjustedDocumentCount: number
  amountDefaultedDocumentCount: number
  imageCount: number
  customers: { imported: number; updated: number }
  vehicles: { imported: number; updated: number }
  documents: { imported: number; existing: number }
}

export async function previewCsvImport(resource: CsvImportResource, file: File) {
  return apiFetch<CsvImportPreview>(`/api/import/${resource}/preview`, { method: 'POST', body: createFormData(file) })
}

export async function commitCsvImport(resource: CsvImportResource, file: File) {
  return apiFetch<CsvImportResult>(`/api/import/${resource}/commit`, { method: 'POST', body: createFormData(file) })
}

export async function commitAbacusRegistration(formData: FormData) {
  return apiFetch<AbacusRegistrationCommitResult>('/api/import/abacus-registration/commit', { method: 'POST', body: formData })
}

export async function commitAbacusGraphFinalRegistration(formData: FormData) {
  return apiFetch<AbacusGraphFinalRegistrationResult>('/api/import/abacus-registration/commit', { method: 'POST', body: formData })
}

export async function uploadAbacusRegistrationImage(input: { vehicleId: string; customerId: string; imagePath: string; imageSha256: string; manifestSha256: string; file: File }) {
  const formData = new FormData()
  formData.append('vehicleId', input.vehicleId)
  formData.append('customerId', input.customerId)
  formData.append('imagePath', input.imagePath)
  formData.append('imageSha256', input.imageSha256)
  formData.append('manifestSha256', input.manifestSha256)
  formData.append('file', input.file, input.imagePath)
  return apiFetch<AbacusRegistrationImageResult>('/api/import/abacus-registration/image', { method: 'POST', body: formData })
}

function createFormData(file: File) {
  const formData = new FormData()
  formData.append('file', file)
  return formData
}
