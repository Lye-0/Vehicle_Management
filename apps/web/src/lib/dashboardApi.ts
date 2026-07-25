import { apiFetch } from './api'

export type DashboardData = {
  summary: {
    registeredVehicles: number
    monthlySales: number
    inspectionsWithin30Days: number
    overdueInspections: number
    unpaidInvoices: number
    unpaidAmount: number
  }
  inspections: Array<{
    customer: string
    vehicle: string
    plate: string
    date: string
    tone: 'normal' | 'warning' | 'danger'
  }>
  unpaidInvoices: Array<{
    customer: string
    document: string
    vehicle: string
    amount: number
    due: string
    tone: 'normal' | 'warning' | 'danger'
  }>
  recentActivities: Array<{
    kind: 'sales' | 'vehicle' | 'payment'
    label: string
    detail: string
    at: string
  }>
}

export async function fetchDashboard() {
  const response = await apiFetch<{ dashboard: DashboardData }>('/api/dashboard')
  return response.dashboard
}
