import { requireFirebaseUser, UnauthorizedError } from './auth/firebase'
import { handleCustomerRoutes } from './routes/customer-routes'
import { handleDashboardRoutes } from './routes/dashboard-routes'
import { handleMaintenanceRoutes } from './routes/maintenance-routes'
import { handleSalesRoutes } from './routes/sales-routes'
import { handleSettingsRoutes } from './routes/settings-routes'
import { corsHeaders, jsonResponse } from './http'

export default {
  async fetch(request, env): Promise<Response> {
    const url = new URL(request.url);

    if (request.method === "OPTIONS") {
      return new Response(null, { status: 204, headers: corsHeaders(env) })
    }

    const customerRouteResponse = await handleCustomerRoutes(request, env)
    if (customerRouteResponse) return customerRouteResponse

    const dashboardRouteResponse = await handleDashboardRoutes(request, env)
    if (dashboardRouteResponse) return dashboardRouteResponse

    const maintenanceRouteResponse = await handleMaintenanceRoutes(request, env)
    if (maintenanceRouteResponse) return maintenanceRouteResponse

    const salesRouteResponse = await handleSalesRoutes(request, env)
    if (salesRouteResponse) return salesRouteResponse

    const settingsRouteResponse = await handleSettingsRoutes(request, env)
    if (settingsRouteResponse) return settingsRouteResponse

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

function isB2Configured(env: Env) {
  return Boolean(env.B2_ENDPOINT && env.B2_REGION && env.B2_BUCKET && env.B2_KEY_ID && env.B2_APPLICATION_KEY)
}
