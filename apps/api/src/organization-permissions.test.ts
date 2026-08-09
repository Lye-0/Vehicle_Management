import { describe, expect, it } from 'vitest'
import { defaultOrganizationPermissions, canManageArchiveRetention, normalizeOrganizationPermissions } from './organization-permissions'

describe('archive retention permission', () => {
  it('defaults to disabled for employees and preserves explicit values', () => {
    expect(defaultOrganizationPermissions.employeeCanManageArchiveRetention).toBe(false)
    expect(normalizeOrganizationPermissions({}).employeeCanManageArchiveRetention).toBe(false)
    expect(normalizeOrganizationPermissions({ employeeCanManageArchiveRetention: true }).employeeCanManageArchiveRetention).toBe(true)
  })

  it('allows administrators and explicitly permitted employees only', () => {
    const disabled = normalizeOrganizationPermissions({ employeeCanManageArchiveRetention: false })
    const enabled = normalizeOrganizationPermissions({ employeeCanManageArchiveRetention: true })
    expect(canManageArchiveRetention('employee', disabled)).toBe(false)
    expect(canManageArchiveRetention('employee', enabled)).toBe(true)
    expect(canManageArchiveRetention('admin', disabled)).toBe(true)
    expect(canManageArchiveRetention('owner', disabled)).toBe(true)
  })
})
