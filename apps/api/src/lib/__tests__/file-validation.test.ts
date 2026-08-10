import { describe, expect, it } from 'vitest'
import { HttpError } from '../../http'
import { assertAttachmentSignature, assertSupportedAttachmentContentType, createVehicleFileObjectKey } from '../file-validation'

describe('file validation', () => {
  it('accepts only the supported attachment content types', () => {
    expect(assertSupportedAttachmentContentType('application/pdf')).toBe('application/pdf')
    expect(() => assertSupportedAttachmentContentType('text/html')).toThrow(HttpError)
  })

  it('checks file signatures instead of trusting the declared MIME type', () => {
    expect(() => assertAttachmentSignature(new Uint8Array([0x25, 0x50, 0x44, 0x46, 0x2d]), 'application/pdf')).not.toThrow()
    expect(() => assertAttachmentSignature(new TextEncoder().encode('<html>'), 'application/pdf')).toThrow(HttpError)
  })

  it('creates an object key without path separators from the file name', () => {
    const key = createVehicleFileObjectKey('org-default', 'vehicle-1', 'file-1', '../receipt.pdf')
    expect(key).toBe('organizations/org-default/vehicles/vehicle-1/file-1-__receipt.pdf')
    expect(key).not.toContain('..')
    expect(key).not.toContain('\\')
  })
})
