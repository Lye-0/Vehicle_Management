import { HttpError } from '../http'

export const supportedAttachmentContentTypes = ['application/pdf', 'image/jpeg', 'image/png'] as const
export type SupportedAttachmentContentType = typeof supportedAttachmentContentTypes[number]
export type AttachmentKind = 'image' | 'pdf'

const supportedContentTypes = new Set<string>(supportedAttachmentContentTypes)

export function isSupportedAttachmentContentType(value: unknown): value is SupportedAttachmentContentType {
  return typeof value === 'string' && supportedContentTypes.has(value)
}

export function assertSupportedAttachmentContentType(value: unknown): SupportedAttachmentContentType {
  if (!isSupportedAttachmentContentType(value)) throw new HttpError(415, 'JPEG・PNG・PDFのみ添付できます。')
  return value as SupportedAttachmentContentType
}

export function attachmentKind(contentType: SupportedAttachmentContentType): AttachmentKind {
  return contentType === 'application/pdf' ? 'pdf' : 'image'
}

export function assertAttachmentSignature(bytes: Uint8Array, contentType: SupportedAttachmentContentType) {
  const valid = contentType === 'application/pdf'
    ? startsWith(bytes, [0x25, 0x50, 0x44, 0x46])
    : contentType === 'image/png'
      ? startsWith(bytes, [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a])
      : startsWith(bytes, [0xff, 0xd8, 0xff])
  if (!valid) throw new HttpError(415, 'ファイルの内容と拡張子が一致しません。')
}

export function createVehicleFileObjectKey(organizationId: string, vehicleId: string, fileId: string, fileName: string) {
  if (![organizationId, vehicleId, fileId].every(isSafePathSegment)) throw new HttpError(400, '添付ファイルの識別子が不正です。')
  const safeName = fileName.replace(/[^a-zA-Z0-9._-]/g, '_').replace(/\.\./gu, '_').slice(0, 120) || 'file'
  return `organizations/${organizationId}/vehicles/${vehicleId}/${fileId}-${safeName}`
}

export function isSafePathSegment(value: string) {
  return /^[A-Za-z0-9_-]{1,128}$/u.test(value)
}

function startsWith(bytes: Uint8Array, signature: number[]) {
  return bytes.length >= signature.length && signature.every((value, index) => bytes[index] === value)
}
