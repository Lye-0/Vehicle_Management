export class HttpError extends Error {
  constructor(public readonly status: number, message: string) {
    super(message)
    this.name = 'HttpError'
  }
}

export function jsonResponse(body: unknown, status: number, env: Env) {
  return Response.json(body, { status, headers: corsHeaders(env) })
}

export function corsHeaders(env: Env) {
  return {
    'Access-Control-Allow-Origin': env.CORS_ORIGIN ?? 'http://localhost:5173',
    'Access-Control-Allow-Headers': 'Authorization, Content-Type, X-Organization-Id',
    'Access-Control-Allow-Methods': 'GET, POST, PUT, PATCH, DELETE, OPTIONS',
    'Vary': 'Origin',
  }
}

const defaultJsonBodyLimit = 2 * 1024 * 1024

export async function readJson(request: Request, maximumBytes = defaultJsonBodyLimit) {
  let body: unknown
  try {
    body = JSON.parse(await readRequestText(request, maximumBytes))
  } catch (error) {
    if (error instanceof HttpError) throw error
    throw new HttpError(400, '入力内容が不正です。')
  }
  if (!body || typeof body !== 'object' || Array.isArray(body)) throw new HttpError(400, '入力内容が不正です。')
  return body as Record<string, unknown>
}

export async function readFormData(request: Request, maximumBytes: number) {
  const bytes = await readRequestBytes(request, maximumBytes)
  const headers = new Headers(request.headers)
  headers.delete('content-length')
  try {
    return await new Request(request.url, { method: request.method, headers, body: bytes }).formData()
  } catch {
    throw new HttpError(400, 'multipart形式の入力が不正です。')
  }
}

export function assertRequestContentLength(request: Request, maximumBytes: number, options: { required?: boolean } = {}) {
  const header = request.headers.get('Content-Length')
  if (!header) {
    if (options.required) throw new HttpError(411, 'リクエスト本文のサイズを指定してください。')
    return
  }
  if (!/^\d+$/u.test(header)) throw new HttpError(400, 'リクエスト本文のサイズが不正です。')
  const contentLength = Number(header)
  if (!Number.isSafeInteger(contentLength)) throw new HttpError(413, 'リクエスト本文が大きすぎます。')
  if (contentLength > maximumBytes) throw new HttpError(413, 'リクエスト本文が大きすぎます。')
}

async function readRequestText(request: Request, maximumBytes: number) {
  return new TextDecoder().decode(await readRequestBytes(request, maximumBytes))
}

async function readRequestBytes(request: Request, maximumBytes: number) {
  const contentLength = Number(request.headers.get('Content-Length'))
  if (Number.isFinite(contentLength) && contentLength > maximumBytes) throw new HttpError(413, 'リクエスト本文が大きすぎます。')
  if (!request.body) return new Uint8Array()
  const reader = request.body.getReader()
  const chunks: Uint8Array[] = []
  let totalBytes = 0
  try {
    while (true) {
      const next = await reader.read()
      if (next.done) break
      totalBytes += next.value.byteLength
      if (totalBytes > maximumBytes) {
        await reader.cancel()
        throw new HttpError(413, 'リクエスト本文が大きすぎます。')
      }
      chunks.push(next.value)
    }
  } finally {
    reader.releaseLock()
  }
  const bytes = new Uint8Array(totalBytes)
  let offset = 0
  for (const chunk of chunks) {
    bytes.set(chunk, offset)
    offset += chunk.byteLength
  }
  return bytes
}
