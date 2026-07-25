import { apiFetch } from './api'

export type InspectionType = '車検' | '12か月点検' | '24か月点検' | '一般点検'
export type InspectionStatus = '予定' | '完了' | 'キャンセル'

export type InspectionSchedule = {
  id: string
  customerId: string
  customerName: string
  vehicleId: string
  vehicle: string
  plate: string
  inspectionType: InspectionType
  dueDate: string
  status: InspectionStatus
  notifiedAt: string | null
  note: string
  createdAt: string
  updatedAt: string
}

export type InspectionScheduleInput = {
  customerId: string
  vehicleId: string
  inspectionType: InspectionType
  dueDate: string
  status: InspectionStatus
  note: string
}

export async function fetchInspectionSchedules() {
  const response = await apiFetch<{ schedules: InspectionSchedule[] }>('/api/inspection-schedules')
  return response.schedules
}

export async function createInspectionSchedule(input: InspectionScheduleInput) {
  const response = await apiFetch<{ schedule: InspectionSchedule }>('/api/inspection-schedules', { method: 'POST', body: JSON.stringify(toPayload(input)) })
  return response.schedule
}

export async function updateInspectionSchedule(id: string, input: InspectionScheduleInput) {
  const response = await apiFetch<{ schedule: InspectionSchedule }>(`/api/inspection-schedules/${id}`, { method: 'PATCH', body: JSON.stringify(toPayload(input)) })
  return response.schedule
}

export async function deleteInspectionSchedule(id: string) {
  await apiFetch(`/api/inspection-schedules/${id}`, { method: 'DELETE' })
}

function toPayload(input: InspectionScheduleInput) {
  return { ...input, dueDate: input.dueDate.replaceAll('/', '-') }
}
