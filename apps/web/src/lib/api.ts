import { getCurrentIdToken } from './auth'

const apiBaseUrl = (import.meta.env.VITE_API_BASE_URL ?? '').replace(/\/$/, '')
let activeOrganizationId: string | null = null

export class ApiError extends Error {
  readonly status: number

  constructor(status: number, message: string) {
    super(message)
    this.name = 'ApiError'
    this.status = status
  }
}

export function setActiveOrganizationId(organizationId: string | null) {
  activeOrganizationId = organizationId
}

export function getActiveOrganizationId() {
  return activeOrganizationId
}

export async function apiFetch<T>(path: string, init: RequestInit = {}) {
  const response = await fetch(`${apiBaseUrl}${path}`, { ...init, headers: await createApiHeaders(init) })
  if (!response.ok) throw new ApiError(response.status, await readApiError(response))
  return response.json() as Promise<T>
}

export async function apiFetchBlob(path: string, init: RequestInit = {}) {
  const response = await fetch(`${apiBaseUrl}${path}`, { ...init, headers: await createApiHeaders(init) })
  if (!response.ok) throw new ApiError(response.status, await readApiError(response))
  return response.blob()
}

async function createApiHeaders(init: RequestInit) {
  const headers = new Headers(init.headers)
  if (typeof init.body === 'string' && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json')
  const token = await getCurrentIdToken()
  if (token) headers.set('Authorization', `Bearer ${token}`)
  if (activeOrganizationId) headers.set('X-Organization-Id', activeOrganizationId)
  return headers
}

async function readApiError(response: Response) {
  try {
    const body = await response.json() as { error?: string }
    return body.error ?? `APIリクエストに失敗しました（${response.status}）。`
  } catch {
    return `APIリクエストに失敗しました（${response.status}）。`
  }
}
