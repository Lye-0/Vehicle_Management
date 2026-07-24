import { getCurrentIdToken } from './auth'

const apiBaseUrl = (import.meta.env.VITE_API_BASE_URL ?? '').replace(/\/$/, '')

export async function apiFetch<T>(path: string, init: RequestInit = {}) {
  const headers = new Headers(init.headers)
  if (typeof init.body === 'string' && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json')
  const token = await getCurrentIdToken()
  if (token) headers.set('Authorization', `Bearer ${token}`)

  const response = await fetch(`${apiBaseUrl}${path}`, { ...init, headers })
  if (!response.ok) throw new Error(await readApiError(response))
  return response.json() as Promise<T>
}

async function readApiError(response: Response) {
  try {
    const body = await response.json() as { error?: string }
    return body.error ?? `APIリクエストに失敗しました（${response.status}）。`
  } catch {
    return `APIリクエストに失敗しました（${response.status}）。`
  }
}
