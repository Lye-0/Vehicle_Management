import { requireFirebaseUser, UnauthorizedError } from './auth/firebase'

export default {
  async fetch(request, env): Promise<Response> {
    const url = new URL(request.url);

    if (request.method === "OPTIONS") {
      return new Response(null, { status: 204, headers: corsHeaders(env) })
    }

    if (request.method === "GET" && (url.pathname === "/health" || url.pathname === "/api/health")) {
      return jsonResponse({
        status: "ok",
        services: {
          database: env.DB ? "configured" : "missing",
          firebaseAuth: env.FIREBASE_PROJECT_ID ? "configured" : "missing",
          objectStorage: isB2Configured(env) ? "configured" : "missing",
        },
      }, 200, env)
    }

    if (request.method === "GET" && url.pathname === "/api/auth/me") {
      try {
        const allowEmulatorToken = env.APP_ENV === 'development' && env.FIREBASE_AUTH_EMULATOR === 'true'
        const user = await requireFirebaseUser(request, env.FIREBASE_PROJECT_ID, allowEmulatorToken)
        return jsonResponse({ user }, 200, env)
      } catch (error) {
        if (error instanceof UnauthorizedError) return jsonResponse({ error: error.message }, 401, env)
        return jsonResponse({ error: "認証設定を確認してください。" }, 500, env)
      }
    }

    return jsonResponse({ error: "Not Found" }, 404, env)
  },
} satisfies ExportedHandler<Env>;

function jsonResponse(body: unknown, status: number, env: Env) {
  return Response.json(body, { status, headers: corsHeaders(env) })
}

function corsHeaders(env: Env) {
  return {
    "Access-Control-Allow-Origin": env.CORS_ORIGIN ?? "http://localhost:5173",
    "Access-Control-Allow-Headers": "Authorization, Content-Type",
    "Access-Control-Allow-Methods": "GET, POST, PUT, PATCH, DELETE, OPTIONS",
    "Vary": "Origin",
  }
}

function isB2Configured(env: Env) {
  return Boolean(env.B2_ENDPOINT && env.B2_REGION && env.B2_BUCKET && env.B2_KEY_ID && env.B2_APPLICATION_KEY)
}
