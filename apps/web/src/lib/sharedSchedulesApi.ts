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
