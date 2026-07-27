import { and, eq } from 'drizzle-orm'
import { appSettings, organizations } from '@vehicle-management/database'
import { UnauthorizedError } from '../auth/firebase'
import { requireOrganizationContext } from '../auth/organization'
import { createDatabase } from '../db/client'
import { HttpError, jsonResponse, readJson } from '../http'

const defaultSettings: AppSettings = {
  shop: {
    name: '',
    postalCode: '',
    address: '',
    phone: '',
    representative: '',
    registrationNumber: '',
    bankName: '',
    bankAccount: '',
  },
  document: {
    defaultDueDays: 14,
    footerNote: '',
    paymentNote: '',
  },
  tax: {
    consumptionTaxRate: 10,
    display: '税込',
    rounding: '切り捨て',
  },
  salesItemPresets: ['車両本体価格', '付属品・特別仕様', '登録代行費用', '納車費用', 'リサイクル料金', '値引き'],
  maintenanceItemPresets: ['法定24か月点検', 'エンジンオイル交換', 'オイルフィルター交換', 'ブレーキ点検', 'タイヤ交換'],
}

const settingKeys = ['shop', 'document', 'tax', 'salesItemPresets', 'maintenanceItemPresets'] as const

export async function handleSettingsRoutes(request: Request, env: Env): Promise<Response | null> {
  const pathname = new URL(request.url).pathname.replace(/\/$/, '') || '/'
  if (pathname !== '/api/settings') return null

  try {
    const database = createDatabase(env.DB)
    const context = await requireOrganizationContext(request, env, database)
    const organizationId = context.organization.organizationId
    if (request.method === 'GET') return jsonResponse({ settings: await loadSettings(database, organizationId) }, 200, env)
    if (request.method === 'PATCH') return await updateSettings(request, env, database, organizationId)
    throw new HttpError(405, 'この操作には対応していません。')
  } catch (error) {
    if (error instanceof UnauthorizedError) return jsonResponse({ error: error.message }, 401, env)
    if (error instanceof HttpError) return jsonResponse({ error: error.message }, error.status, env)
    console.error(error)
    return jsonResponse({ error: '設定の処理に失敗しました。' }, 500, env)
  }
}

async function updateSettings(request: Request, env: Env, database: ReturnType<typeof createDatabase>, organizationId: string) {
  const body = await readJson(request)
  const incoming = recordValue(body.settings)
  if (!incoming) throw new HttpError(400, '設定内容が不正です。')
  const current = await loadSettings(database, organizationId)
  const next = normalizeSettings({
    shop: { ...current.shop, ...recordValue(incoming.shop) },
    document: { ...current.document, ...recordValue(incoming.document) },
    tax: { ...current.tax, ...recordValue(incoming.tax) },
    salesItemPresets: incoming.salesItemPresets ?? current.salesItemPresets,
    maintenanceItemPresets: incoming.maintenanceItemPresets ?? current.maintenanceItemPresets,
  })

  const serializedSettings: Record<string, unknown> = {
    shop: next.shop,
    document: next.document,
    tax: next.tax,
    salesItemPresets: next.salesItemPresets,
    maintenanceItemPresets: next.maintenanceItemPresets,
  }
  const now = new Date().toISOString()
  for (const key of settingKeys) {
    const value = JSON.stringify(serializedSettings[key])
    const existing = await database.select({ key: appSettings.key }).from(appSettings).where(and(eq(appSettings.organizationId, organizationId), eq(appSettings.key, key))).get()
    if (existing) {
      await database.update(appSettings).set({ value, updatedAt: now }).where(and(eq(appSettings.organizationId, organizationId), eq(appSettings.key, key))).run()
    } else {
      await database.insert(appSettings).values({ organizationId, key, value, updatedAt: now }).run()
    }
  }

  const shopName = next.shop.name
  if (shopName) {
    await database.update(organizations).set({ name: shopName, updatedAt: now }).where(eq(organizations.id, organizationId)).run()
  }

  return jsonResponse({ settings: next }, 200, env)
}

async function loadSettings(database: ReturnType<typeof createDatabase>, organizationId: string): Promise<AppSettings> {
  const rows = await database.select().from(appSettings).where(eq(appSettings.organizationId, organizationId)).all()
  const saved = new Map(rows.map((row) => [row.key, parseJson(row.value)]))
  return normalizeSettings({
    shop: saved.get('shop'),
    document: saved.get('document'),
    tax: saved.get('tax'),
    salesItemPresets: saved.get('salesItemPresets'),
    maintenanceItemPresets: saved.get('maintenanceItemPresets'),
  })
}

function normalizeSettings(value: Record<string, unknown>): AppSettings {
  const shop = recordValue(value.shop)
  const document = recordValue(value.document)
  const tax = recordValue(value.tax)
  return {
    shop: {
      name: textValue(shop?.name, defaultSettings.shop.name),
      postalCode: textValue(shop?.postalCode, defaultSettings.shop.postalCode),
      address: textValue(shop?.address, defaultSettings.shop.address),
      phone: textValue(shop?.phone, defaultSettings.shop.phone),
      representative: textValue(shop?.representative, defaultSettings.shop.representative),
      registrationNumber: textValue(shop?.registrationNumber, defaultSettings.shop.registrationNumber),
      bankName: textValue(shop?.bankName, defaultSettings.shop.bankName),
      bankAccount: textValue(shop?.bankAccount, defaultSettings.shop.bankAccount),
    },
    document: {
      defaultDueDays: integerValue(document?.defaultDueDays, defaultSettings.document.defaultDueDays, 0, 365),
      footerNote: textValue(document?.footerNote, defaultSettings.document.footerNote),
      paymentNote: textValue(document?.paymentNote, defaultSettings.document.paymentNote),
    },
    tax: {
      consumptionTaxRate: integerValue(tax?.consumptionTaxRate, defaultSettings.tax.consumptionTaxRate, 0, 100),
      display: tax?.display === '税別' ? '税別' : '税込',
      rounding: tax?.rounding === '四捨五入' ? '四捨五入' : '切り捨て',
    },
    salesItemPresets: listValue(value.salesItemPresets, defaultSettings.salesItemPresets),
    maintenanceItemPresets: listValue(value.maintenanceItemPresets, defaultSettings.maintenanceItemPresets),
  }
}

function parseJson(value: string) {
  try {
    return JSON.parse(value) as unknown
  } catch {
    return null
  }
}

function recordValue(value: unknown): Record<string, unknown> | null {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : null
}

function textValue(value: unknown, fallback: string) {
  return typeof value === 'string' ? value.trim().slice(0, 500) : fallback
}

function integerValue(value: unknown, fallback: number, minimum: number, maximum: number) {
  const number = typeof value === 'number' ? value : Number(value)
  if (!Number.isFinite(number)) return fallback
  return Math.min(maximum, Math.max(minimum, Math.round(number)))
}

function listValue(value: unknown, fallback: string[]) {
  if (!Array.isArray(value)) return fallback
  const list = value.filter((item): item is string => typeof item === 'string').map((item) => item.trim().slice(0, 100)).filter(Boolean)
  return Array.from(new Set(list)).slice(0, 100)
}

type AppSettings = {
  shop: {
    name: string
    postalCode: string
    address: string
    phone: string
    representative: string
    registrationNumber: string
    bankName: string
    bankAccount: string
  }
  document: {
    defaultDueDays: number
    footerNote: string
    paymentNote: string
  }
  tax: {
    consumptionTaxRate: number
    display: '税込' | '税別'
    rounding: '切り捨て' | '四捨五入'
  }
  salesItemPresets: string[]
  maintenanceItemPresets: string[]
}
