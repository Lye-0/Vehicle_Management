import { apiFetch } from './api'

export type SharedScheduleInput = {
  title: string
  startDate: string
  endDate: string
  detail: string
}

export type SharedSchedule = SharedScheduleInput & {
  id: string
  authorName: string
  createdAt: string
  updatedAt: string
}

export async function createSharedSchedule(input: SharedScheduleInput) {
  return apiFetch<{ schedule: SharedSchedule }>('/api/shared-schedules', {
    method: 'POST',
    body: JSON.stringify(input),
  })
}

export async function updateSharedSchedule(id: string, input: SharedScheduleInput) {
  return apiFetch<{ schedule: SharedSchedule }>(`/api/shared-schedules/${encodeURIComponent(id)}`, {
    method: 'PATCH',
    body: JSON.stringify(input),
  })
}

export async function deleteSharedSchedule(id: string) {
  return apiFetch<{ deleted: boolean }>(`/api/shared-schedules/${encodeURIComponent(id)}`, {
    method: 'DELETE',
  })
}
