import { describe, expect, it } from 'vitest'
import { HttpError } from '../../http'
import { assertManifestReferences } from '../backup-routes'

type ManifestTables = Parameters<typeof assertManifestReferences>[0]

function tables(overrides: Partial<ManifestTables> = {}): ManifestTables {
  return {
    customers: [],
    vehicles: [],
    vehicleFiles: [],
    salesDocuments: [],
    salesDocumentItems: [],
    maintenanceDocuments: [],
    maintenanceItems: [],
    paymentRecords: [],
    paymentEntries: [],
    mileageHistories: [],
    inspectionSchedules: [],
    sharedSchedules: [],
    appSettings: [],
    ...overrides,
  }
}

describe('backup manifest relationship validation', () => {
  it('rejects a relation to an ID outside the manifest', () => {
    const vehicle = { id: 'vehicle-1', organizationId: 'org-1', customerId: 'customer-from-another-org' } as ManifestTables['vehicles'][number]
    expect(() => assertManifestReferences(tables({ vehicles: [vehicle] }))).toThrow(HttpError)
  })

  it('accepts related rows that form a closed organization graph', () => {
    const customer = { id: 'customer-1', organizationId: 'org-1' } as ManifestTables['customers'][number]
    const vehicle = { id: 'vehicle-1', organizationId: 'org-1', customerId: customer.id } as ManifestTables['vehicles'][number]
    const salesDocument = { id: 'sales-1', organizationId: 'org-1', customerId: customer.id, vehicleId: vehicle.id } as ManifestTables['salesDocuments'][number]
    const maintenanceDocument = { id: 'maintenance-1', organizationId: 'org-1', customerId: customer.id, vehicleId: vehicle.id } as ManifestTables['maintenanceDocuments'][number]
    const salesItem = { id: 'sales-item-1', organizationId: 'org-1', documentId: salesDocument.id } as ManifestTables['salesDocumentItems'][number]
    const maintenanceItem = { id: 'maintenance-item-1', organizationId: 'org-1', documentId: maintenanceDocument.id } as ManifestTables['maintenanceItems'][number]
    const paymentRecord = { id: 'payment-1', organizationId: 'org-1', documentType: '販売請求書', documentId: salesDocument.id } as ManifestTables['paymentRecords'][number]
    const paymentEntry = { id: 'entry-1', organizationId: 'org-1', documentType: '整備請求書', documentId: maintenanceDocument.id } as ManifestTables['paymentEntries'][number]
    const inspection = { id: 'inspection-1', organizationId: 'org-1', customerId: customer.id, vehicleId: vehicle.id } as ManifestTables['inspectionSchedules'][number]
    const mileage = { id: 'mileage-1', organizationId: 'org-1', vehicleId: vehicle.id, maintenanceDocumentId: maintenanceDocument.id } as ManifestTables['mileageHistories'][number]

    expect(() => assertManifestReferences(tables({ customers: [customer], vehicles: [vehicle], salesDocuments: [salesDocument], salesDocumentItems: [salesItem], maintenanceDocuments: [maintenanceDocument], maintenanceItems: [maintenanceItem], paymentRecords: [paymentRecord], paymentEntries: [paymentEntry], inspectionSchedules: [inspection], mileageHistories: [mileage] }))).not.toThrow()
  })

  it('accepts vehicleless maintenance documents from ABACUS imports', () => {
    const customer = { id: 'customer-vehicleless', organizationId: 'org-1' } as ManifestTables['customers'][number]
    const maintenanceDocument = { id: 'maintenance-vehicleless', organizationId: 'org-1', customerId: customer.id, vehicleId: null } as ManifestTables['maintenanceDocuments'][number]
    const maintenanceItem = { id: 'maintenance-item-vehicleless', organizationId: 'org-1', documentId: maintenanceDocument.id } as ManifestTables['maintenanceItems'][number]

    expect(() => assertManifestReferences(tables({ customers: [customer], maintenanceDocuments: [maintenanceDocument], maintenanceItems: [maintenanceItem] }))).not.toThrow()
  })

  it('rejects a maintenance document that references a vehicle outside the manifest', () => {
    const customer = { id: 'customer-2', organizationId: 'org-1' } as ManifestTables['customers'][number]
    const maintenanceDocument = { id: 'maintenance-2', organizationId: 'org-1', customerId: customer.id, vehicleId: 'vehicle-from-another-backup' } as ManifestTables['maintenanceDocuments'][number]

    expect(() => assertManifestReferences(tables({ customers: [customer], maintenanceDocuments: [maintenanceDocument] }))).toThrow(HttpError)
  })
})
