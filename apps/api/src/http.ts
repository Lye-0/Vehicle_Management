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
    'Access-Control-Allow-Headers': 'Authorization, Content-Type',
    'Access-Control-Allow-Methods': 'GET, POST, PUT, PATCH, DELETE, OPTIONS',
    'Vary': 'Origin',
  }
}

export async function readJson(request: Request) {
  const body: unknown = await request.json().catch(() => null)
  if (!body || typeof body !== 'object' || Array.isArray(body)) throw new HttpError(400, '入力内容が不正です。')
  return body as Record<string, unknown>
}
