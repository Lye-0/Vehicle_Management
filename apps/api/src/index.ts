import { handleCustomerRoutes } from './routes/customer-routes'
import { handleDashboardRoutes } from './routes/dashboard-routes'
import { handleExportRoutes } from './routes/export-routes'
import { handleMaintenanceRoutes } from './routes/maintenance-routes'
import { handlePaymentRoutes } from './routes/payment-routes'
import { handleSalesRoutes } from './routes/sales-routes'
import { handleSettingsRoutes } from './routes/settings-routes'
import { handleOrganizationRoutes } from './routes/organization-routes'
import { handleMemberRoutes } from './routes/member-routes'
import { handleImportRoutes } from './routes/import-routes'
import { handleAbacusRegistrationRoutes } from './routes/abacus-registration-routes'
import { handleBackupRoutes } from './routes/backup-routes'
import { handleArchiveRoutes } from './routes/archive-routes'
import { handleInspectionRoutes } from './routes/inspection-routes'
import { handleSharedScheduleRoutes } from './routes/shared-schedule-routes'
import { handleSyncPreviewRoutes } from './routes/sync-preview-routes'
import { runScheduledBackupMaintenance } from './routes/backup-routes'
import { corsHeaders, jsonResponse } from './http'
import { getEnvironmentIssues } from './environment'

export default {
  async fetch(request, env): Promise<Response> {
    const url = new URL(request.url);

    const environmentIssues = getEnvironmentIssues(env)
    if (environmentIssues.length > 0) {
      console.error(`[environment] ${environmentIssues.join(' ')}`)
      return jsonResponse({ error: 'サーバーの環境設定が不正です。', code: 'INVALID_ENVIRONMENT' }, 500, env)
    }

    if (request.method === "OPTIONS") {
      return new Response(null, { status: 204, headers: corsHeaders(env) })
    }

    const organizationRouteResponse = await handleOrganizationRoutes(request, env)
    if (organizationRouteResponse) return organizationRouteResponse

    const memberRouteResponse = await handleMemberRoutes(request, env)
    if (memberRouteResponse) return memberRouteResponse

    const importRouteResponse = await handleImportRoutes(request, env)
    if (importRouteResponse) return importRouteResponse

    const abacusRegistrationRouteResponse = await handleAbacusRegistrationRoutes(request, env)
    if (abacusRegistrationRouteResponse) return abacusRegistrationRouteResponse

    const backupRouteResponse = await handleBackupRoutes(request, env)
    if (backupRouteResponse) return backupRouteResponse

    const archiveRouteResponse = await handleArchiveRoutes(request, env)
    if (archiveRouteResponse) return archiveRouteResponse

    const customerRouteResponse = await handleCustomerRoutes(request, env)
    if (customerRouteResponse) return customerRouteResponse

    const syncPreviewRouteResponse = await handleSyncPreviewRoutes(request, env)
    if (syncPreviewRouteResponse) return syncPreviewRouteResponse

    const inspectionRouteResponse = await handleInspectionRoutes(request, env)
    if (inspectionRouteResponse) return inspectionRouteResponse

    const sharedScheduleRouteResponse = await handleSharedScheduleRoutes(request, env)
    if (sharedScheduleRouteResponse) return sharedScheduleRouteResponse

    const exportRouteResponse = await handleExportRoutes(request, env)
    if (exportRouteResponse) return exportRouteResponse

    const dashboardRouteResponse = await handleDashboardRoutes(request, env)
    if (dashboardRouteResponse) return dashboardRouteResponse

    const maintenanceRouteResponse = await handleMaintenanceRoutes(request, env)
    if (maintenanceRouteResponse) return maintenanceRouteResponse

    const paymentRouteResponse = await handlePaymentRoutes(request, env)
    if (paymentRouteResponse) return paymentRouteResponse

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

    return jsonResponse({ error: "Not Found" }, 404, env)
  },
  async scheduled(controller, env): Promise<void> {
    await runScheduledBackupMaintenance(env, controller.scheduledTime)
  },
} satisfies ExportedHandler<Env>;

function isB2Configured(env: Env) {
  return Boolean(env.B2_ENDPOINT && env.B2_REGION && env.B2_BUCKET && env.B2_KEY_ID && env.B2_APPLICATION_KEY)
}
