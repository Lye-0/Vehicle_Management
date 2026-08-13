import { apiFetch, apiFetchBlob } from './api'

export type Attachment = {
  id: string
  name: string
  type: 'image' | 'pdf' | 'other'
  contentType: string
  size: number
  createdAt: string
}

export type Vehicle = {
  id: string
  maker: string
  model: string
  modelType: string
  plate: string
  vin: string
  year: string
  inspectionDate: string
  mileage: string
  color: string
  displacement: string
  transmission: string
  inspectionRecordAvailable: boolean
  note: string
  freeItem1: string
  freeItem2: string
  freeItem3: string
  updatedAt: string
  attachments: Attachment[]
}

export type Customer = {
  id: string
  name: string
  kana: string
  phone: string
  email: string
  postalCode: string
  address: string
  birthDate: string
  employer: string
  memo: string
  updatedAt: string
  vehicles: Vehicle[]
}

export type CustomerInput = {
  name: string
  kana: string
  phone: string
  email: string
  postalCode?: string
  address: string
  birthDate: string
  employer: string
  memo: string
}

export type VehiclelessDocument = {
  id: string
  kind: 'sales' | 'maintenance'
  number: string
  type: string
  category: string | null
  status: string
  issuedAt: string
  total: number
  sourceLocation: string
}

export type VehiclelessDocuments = {
  customerId: string
  salesCount: number
  maintenanceCount: number
  documents: VehiclelessDocument[]
}

export type VehicleInput = {
  maker: string
  model: string
  modelType: string
  plate: string
  vin: string
  year: string
  inspectionDate: string
  mileage: string
  color: string
  displacement: string
  transmission: string
  note: string
  freeItem1: string
  freeItem2: string
  freeItem3: string
}

type ApiCustomer = {
  id: string
  name: string
  nameKana: string | null
  phone: string | null
  email: string | null
  postalCode: string | null
  address: string | null
  birthDate: string | null
  employer: string | null
  memo: string | null
  updatedAt: string
  vehicles: ApiVehicle[]
}

type ApiVehicle = {
  id: string
  maker: string | null
  name: string
  modelType?: string | null
  model?: string | null
  registrationNumber: string | null
  chassisNumber: string | null
  modelYear: number | null
  inspectionDate: string | null
  mileage: number | null
  bodyColor: string | null
  displacement: number | null
  transmission: string | null
  inspectionRecordAvailable?: boolean
  memo: string | null
  freeItem1: string | null
  freeItem2: string | null
  freeItem3: string | null
  updatedAt: string
  files: ApiAttachment[]
}

type ApiAttachment = {
  id: string
  name: string
  type: 'image' | 'pdf' | 'other'
  contentType: string
  size: number
  createdAt: string
}

type ApiVehiclelessDocument = VehiclelessDocument

export async function fetchCustomers() {
  const response = await apiFetch<{ customers: ApiCustomer[] }>('/api/customers')
  return response.customers.map(mapCustomer)
}

export async function fetchVehiclelessDocuments(customerId: string) {
  const response = await apiFetch<{ customerId: string; salesCount: number; maintenanceCount: number; documents: ApiVehiclelessDocument[] }>(`/api/customers/${encodeURIComponent(customerId)}/vehicleless-documents`)
  return response
}

export async function createCustomer(input: CustomerInput) {
  const response = await apiFetch<{ customer: ApiCustomer }>('/api/customers', { method: 'POST', body: JSON.stringify(toCustomerPayload(input)) })
  return mapCustomer(response.customer)
}

export async function updateCustomer(id: string, input: CustomerInput) {
  const response = await apiFetch<{ customer: ApiCustomer }>(`/api/customers/${id}`, { method: 'PATCH', body: JSON.stringify(toCustomerPayload(input)) })
  return mapCustomer(response.customer)
}

export async function createVehicle(customerId: string, input: VehicleInput) {
  const response = await apiFetch<{ customer: ApiCustomer; vehicleId: string }>(`/api/customers/${customerId}/vehicles`, { method: 'POST', body: JSON.stringify(toVehiclePayload(input)) })
  return { customer: mapCustomer(response.customer), vehicleId: response.vehicleId }
}

export async function updateVehicle(id: string, input: VehicleInput) {
  await apiFetch<{ vehicleId: string }>(`/api/vehicles/${id}`, { method: 'PATCH', body: JSON.stringify(toVehiclePayload(input)) })
}

export async function uploadVehicleFile(vehicleId: string, file: File) {
  const formData = new FormData()
  formData.append('file', file)
  const response = await apiFetch<{ file: ApiAttachment }>(`/api/vehicles/${vehicleId}/files`, { method: 'POST', body: formData })
  return mapAttachment(response.file)
}

export async function deleteVehicleFile(vehicleId: string, fileId: string) {
  await apiFetch(`/api/vehicles/${vehicleId}/files/${fileId}`, { method: 'DELETE' })
}

export async function fetchVehicleFile(vehicleId: string, fileId: string) {
  return apiFetchBlob(`/api/vehicles/${vehicleId}/files/${fileId}`)
}

export type VehicleHistory = {
  vehicle: {
    id: string
    customerId: string
    customerName: string
    maker: string | null
    name: string
    modelType: string | null
    registrationNumber: string | null
    chassisNumber: string | null
    modelYear: number | null
    inspectionDate: string | null
    mileage: number | null
    bodyColor: string | null
    displacement: number | null
    transmission: string | null
    memo: string | null
    freeItem1: string | null
    freeItem2: string | null
    freeItem3: string | null
  }
  sales: Array<{ id: string; number: string; type: string; status: string; issuedAt: string; dueDate: string | null; total: number }>
  maintenance: Array<{ id: string; number: string; type: string; category: string; status: string; issuedAt: string; intakeDate: string | null; completionDate: string | null; total: number; recordedMileage: number | null }>
  inspections: Array<{ id: string; inspectionType: string; dueDate: string; status: string; note: string; notifiedAt: string | null }>
  payments: Array<{ id: string; documentType: string; documentId: string; documentNumber: string; paidAmount: number; paymentDate: string | null; method: string | null; note: string | null }>
  attachments: ApiAttachment[]
}

export async function fetchVehicleHistory(vehicleId: string) {
  return apiFetch<VehicleHistory>(`/api/vehicles/${vehicleId}/history`)
}

function mapCustomer(customer: ApiCustomer): Customer {
  return {
    id: customer.id,
    name: customer.name,
    kana: customer.nameKana ?? '',
    phone: customer.phone ?? '',
    email: customer.email ?? '',
    postalCode: customer.postalCode ?? '',
    address: customer.address ?? '',
    birthDate: customer.birthDate ?? '',
    employer: customer.employer ?? '',
    memo: customer.memo ?? '',
    updatedAt: customer.updatedAt,
    vehicles: customer.vehicles.map(mapVehicle),
  }
}

function mapVehicle(vehicle: ApiVehicle): Vehicle {
  return {
    id: vehicle.id,
    maker: vehicle.maker ?? '',
    model: vehicle.name,
    modelType: vehicle.modelType ?? vehicle.model ?? '',
    plate: vehicle.registrationNumber ?? '',
    vin: vehicle.chassisNumber ?? '',
    year: vehicle.modelYear ? `${vehicle.modelYear}年` : '',
    inspectionDate: vehicle.inspectionDate ?? '',
    mileage: vehicle.mileage === null ? '' : `${vehicle.mileage.toLocaleString('ja-JP')} km`,
    color: vehicle.bodyColor ?? '',
    displacement: vehicle.displacement === null ? '' : `${vehicle.displacement.toLocaleString('ja-JP')} cc`,
    transmission: vehicle.transmission ?? '',
    inspectionRecordAvailable: Boolean(vehicle.inspectionRecordAvailable),
    note: vehicle.memo ?? '',
    freeItem1: vehicle.freeItem1 ?? '',
    freeItem2: vehicle.freeItem2 ?? '',
    freeItem3: vehicle.freeItem3 ?? '',
    updatedAt: vehicle.updatedAt,
    attachments: vehicle.files.map(mapAttachment),
  }
}

function mapAttachment(attachment: ApiAttachment): Attachment {
  return { ...attachment, createdAt: formatDate(attachment.createdAt) }
}

function formatDate(date: string) {
  return date.slice(0, 10).replace(/-/g, '/')
}

function toCustomerPayload(input: CustomerInput) {
  return { name: input.name, nameKana: input.kana, phone: input.phone, email: input.email, postalCode: input.postalCode ?? '', address: input.address, birthDate: input.birthDate, employer: input.employer, memo: input.memo }
}

function toVehiclePayload(input: VehicleInput) {
  return {
    maker: input.maker,
    name: input.model,
    modelType: input.modelType,
    registrationNumber: input.plate,
    chassisNumber: input.vin,
    modelYear: parseNumber(input.year),
    inspectionDate: input.inspectionDate,
    mileage: parseNumber(input.mileage),
    bodyColor: input.color,
    displacement: parseNumber(input.displacement),
    transmission: input.transmission,
    memo: input.note,
    freeItem1: input.freeItem1,
    freeItem2: input.freeItem2,
    freeItem3: input.freeItem3,
  }
}

function parseNumber(value: string) {
  const normalized = value.replace(/[^0-9]/g, '')
  return normalized ? Number(normalized) : null
}
