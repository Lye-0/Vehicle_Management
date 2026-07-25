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

export async function previewCsvImport(resource: CsvImportResource, file: File) {
  return apiFetch<CsvImportPreview>(`/api/import/${resource}/preview`, { method: 'POST', body: createFormData(file) })
}

export async function commitCsvImport(resource: CsvImportResource, file: File) {
  return apiFetch<CsvImportResult>(`/api/import/${resource}/commit`, { method: 'POST', body: createFormData(file) })
}

function createFormData(file: File) {
  const formData = new FormData()
  formData.append('file', file)
  return formData
}
