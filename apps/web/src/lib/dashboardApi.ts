import { apiFetch } from './api'

export type DashboardNavigationTarget =
  | { section: 'customers'; customerId: string; vehicleId: string }
  | { section: 'sales' | 'maintenance' | 'payments'; recordId: string }

export type DashboardCalendarEvent = {
  id: string
  date: string
  endDate: string
  category: 'vehicle-inspection' | 'inspection' | 'maintenance' | 'sales' | 'payment-due' | 'payment' | 'shared'
  categoryLabel: string
  title: string
  detail: string
  status: string | null
  amount: number | null
  authorName?: string
  sharedScheduleId?: string
  navigation?: DashboardNavigationTarget
}

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
    customerId: string
    vehicleId: string
    customer: string
    vehicle: string
    plate: string
    date: string
    tone: 'normal' | 'warning' | 'danger'
  }>
  upcomingIntakeVehicles: Array<{
    customerId: string
    vehicleId: string
    customer: string
    vehicle: string
    plate: string
    date: string
    tone: 'normal' | 'warning' | 'danger'
  }>
  upcomingReleaseVehicles: Array<{
    customerId: string
    vehicleId: string
    customer: string
    vehicle: string
    plate: string
    date: string
    tone: 'normal' | 'warning' | 'danger'
  }>
  unpaidInvoices: Array<{
    documentId: string
    section: 'sales' | 'maintenance'
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
  calendarEvents: DashboardCalendarEvent[]
}

export async function fetchDashboard() {
  const response = await apiFetch<{ dashboard: DashboardData }>('/api/dashboard')
  return response.dashboard
}
