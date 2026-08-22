export function hasOwnField(value: object | null | undefined, field: PropertyKey): boolean {
  return value !== null && value !== undefined && Object.prototype.hasOwnProperty.call(value, field)
}

export function resolveDocumentCustomerField(
  override: object | null | undefined,
  field: 'birthDate' | 'employer',
  documentValue: string | null | undefined,
  customerValue: string | null | undefined,
): string {
  if (hasOwnField(override, field)) {
    const value = (override as Record<string, unknown>)[field]
    return typeof value === 'string' ? value : ''
  }
  return documentValue || customerValue || ''
}
