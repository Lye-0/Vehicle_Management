export const ABACUS_LINK_METHODS = ['automatic', 'manual-vehicle', 'manual-customer-only', 'recommended'] as const
export type AbacusLinkMethod = typeof ABACUS_LINK_METHODS[number]

export type AbacusDocumentImportMetadata = {
  documentKey: string
  sourceCandidateId: string
  sourceLocation: string
  vehicleless: boolean
  linkedCustomerId: string | null
  linkedVehicleId: string | null
  linkMethod: AbacusLinkMethod
  linkReason: string
  legacyFormat: boolean
}
