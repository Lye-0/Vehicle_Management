import { describe, expect, it } from 'vitest'
import { decodeBase64Url, encodeBase64Url } from '../base64url'

describe('base64url', () => {
  it('encodes and decodes UTF-8 text', () => {
    const value = JSON.stringify({ name: '顧客名', id: 'customer-1', value: '箕面市' })

    expect(decodeBase64Url(encodeBase64Url(value))).toBe(value)
  })

  it('decodes cursors written by the previous ASCII-compatible encoder', () => {
    const value = '{"name":"Customer","id":"customer-1"}'
    const legacyCursor = btoa(value).replaceAll('+', '-').replaceAll('/', '_').replace(/=+$/u, '')

    expect(decodeBase64Url(legacyCursor)).toBe(value)
  })
})
