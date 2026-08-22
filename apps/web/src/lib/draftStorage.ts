const draftDatabaseName = 'vehicle-management-drafts'
const draftStoreName = 'drafts'
const draftDatabaseVersion = 2
const draftTtlMs = 7 * 24 * 60 * 60 * 1000
const draftStorageChangeEvent = 'vehicle-management-draft-storage-change'

export type DraftKind =
  | 'sales-new'
  | 'sales-existing'
  | 'maintenance-new'
  | 'maintenance-existing'
  | 'customer-new'
  | 'customer-existing'
  | 'vehicle-new'
  | 'vehicle-existing'
  | 'settings'

export type DraftScope = {
  userId: string
  organizationId: string
  runId: string
}

export type DraftRecord<T = unknown> = {
  key: string
  value: T
  savedAt: number
  schemaVersion: 2
  userId: string
  organizationId: string
  runId: string
  kind: DraftKind
  targetId?: string
}

type StoredDraft = {
  key: string
  logicalKey?: string
  value: unknown
  savedAt: number
  schemaVersion?: number
  userId?: string
  organizationId?: string
  runId?: string
  kind?: DraftKind
  targetId?: string
}

let activeDraftScope: DraftScope | null = null

export function setDraftScope(scope: DraftScope | null) {
  activeDraftScope = scope
}

export function getDraftScope() {
  return activeDraftScope
}

function createRunId() {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') return crypto.randomUUID()
  return `${Date.now()}-${Math.random().toString(36).slice(2)}`
}

export function createDraftRunId() {
  return createRunId()
}

function inferDraftMetadata(key: string): { kind: DraftKind; targetId?: string } {
  if (key === 'sales-new-document' || key.startsWith('sales-new-document:')) return { kind: 'sales-new' }
  if (key.startsWith('sales-document:')) return { kind: 'sales-existing', targetId: key.slice('sales-document:'.length) }
  if (key === 'maintenance-new-document' || key.startsWith('maintenance-new-document:')) return { kind: 'maintenance-new' }
  if (key.startsWith('maintenance-document:')) return { kind: 'maintenance-existing', targetId: key.slice('maintenance-document:'.length) }
  if (key === 'customer-new' || key.startsWith('customer-new:')) return { kind: 'customer-new' }
  if (key.startsWith('customer-edit:')) return { kind: 'customer-existing', targetId: key.slice('customer-edit:'.length) }
  if (key === 'vehicle-new') return { kind: 'vehicle-new' }
  if (key.startsWith('vehicle-new:')) return { kind: 'vehicle-new', targetId: key.slice('vehicle-new:'.length).split(':')[0] }
  if (key.startsWith('vehicle-edit:')) return { kind: 'vehicle-existing', targetId: key.slice('vehicle-edit:'.length) }
  return { kind: 'settings' }
}

function getLogicalKey(stored: StoredDraft) {
  return stored.logicalKey ?? stored.key
}

function getScopedStorageKey(key: string) {
  if (!activeDraftScope) return key
  return `draft:v2:${encodeURIComponent(activeDraftScope.userId)}:${encodeURIComponent(activeDraftScope.organizationId)}:${encodeURIComponent(key)}`
}

function isAccessibleStoredDraft(stored: StoredDraft) {
  if (!activeDraftScope) return true
  const isCurrent = stored.schemaVersion === 2
    && stored.userId === activeDraftScope.userId
    && stored.organizationId === activeDraftScope.organizationId
  const isLegacy = stored.schemaVersion !== 2 || !stored.userId || !stored.organizationId
  return isCurrent || isLegacy
}

function normalizeStoredDraft(stored: StoredDraft): DraftRecord | null {
  if (!activeDraftScope) return null
  const logicalKey = getLogicalKey(stored)
  const inferred = inferDraftMetadata(logicalKey)
  const isCurrent = stored.schemaVersion === 2
    && stored.userId === activeDraftScope.userId
    && stored.organizationId === activeDraftScope.organizationId
  if (isCurrent) {
    return {
      key: logicalKey,
      value: stored.value,
      savedAt: stored.savedAt,
      schemaVersion: 2,
      userId: stored.userId!,
      organizationId: stored.organizationId!,
      runId: stored.runId ?? 'legacy',
      kind: stored.kind ?? inferred.kind,
      targetId: stored.targetId ?? inferred.targetId,
    }
  }
  // 既存形式には所有者情報がなかったため、認証・組織確定後に現行範囲へ移行する。
  if (stored.schemaVersion !== 2 || !stored.userId || !stored.organizationId) {
    return {
      key: logicalKey,
      value: stored.value,
      savedAt: stored.savedAt,
      schemaVersion: 2,
      userId: activeDraftScope.userId,
      organizationId: activeDraftScope.organizationId,
      runId: 'legacy',
      kind: stored.kind ?? inferred.kind,
      targetId: stored.targetId ?? inferred.targetId,
    }
  }
  return null
}

function needsMigration(stored: StoredDraft, normalized: DraftRecord) {
  return stored.key !== getScopedStorageKey(normalized.key)
    || stored.logicalKey !== normalized.key
    || stored.schemaVersion !== 2
    || stored.userId !== normalized.userId
    || stored.organizationId !== normalized.organizationId
    || stored.runId !== normalized.runId
    || stored.kind !== normalized.kind
    || stored.targetId !== normalized.targetId
}

function notifyDraftStorageChange() {
  if (typeof window !== 'undefined') window.dispatchEvent(new Event(draftStorageChangeEvent))
}

export function subscribeDraftStorageChange(listener: () => void) {
  if (typeof window === 'undefined') return () => undefined
  window.addEventListener(draftStorageChangeEvent, listener)
  return () => window.removeEventListener(draftStorageChangeEvent, listener)
}

function openDraftDatabase(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    if (typeof indexedDB === 'undefined') {
      reject(new Error('端末内下書き保存に対応していません。'))
      return
    }
    const request = indexedDB.open(draftDatabaseName, draftDatabaseVersion)
    request.onupgradeneeded = () => {
      const database = request.result
      if (!database.objectStoreNames.contains(draftStoreName)) database.createObjectStore(draftStoreName, { keyPath: 'key' })
    }
    request.onsuccess = () => resolve(request.result)
    request.onerror = () => reject(request.error ?? new Error('端末内下書きを開けませんでした。'))
  })
}

async function getStoredDraft(key: string) {
  const database = await openDraftDatabase()
  const stored = await new Promise<StoredDraft | undefined>((resolve, reject) => {
    const transaction = database.transaction(draftStoreName, 'readonly')
    const request = transaction.objectStore(draftStoreName).getAll()
    request.onsuccess = () => {
      const records = (request.result as StoredDraft[]) ?? []
      const scopedKey = getScopedStorageKey(key)
      const scoped = records.find((record) => record.key === scopedKey)
      if (scoped) {
        resolve(scoped)
        return
      }
      const legacy = records.find((record) => record.key === key && isAccessibleStoredDraft(record))
      resolve(legacy)
    }
    request.onerror = () => reject(request.error ?? new Error('端末内下書きを読み込めませんでした。'))
  })
  database.close()
  return stored
}

async function putStoredDraft(record: DraftRecord, previousKey?: string) {
  const database = await openDraftDatabase()
  await new Promise<void>((resolve, reject) => {
    const transaction = database.transaction(draftStoreName, 'readwrite')
    const store = transaction.objectStore(draftStoreName)
    const storageKey = getScopedStorageKey(record.key)
    const migrated: StoredDraft = {
      ...record,
      key: storageKey,
      logicalKey: record.key,
    }
    store.put(migrated)
    if (previousKey && previousKey !== storageKey) store.delete(previousKey)
    transaction.oncomplete = () => resolve()
    transaction.onerror = () => reject(transaction.error ?? new Error('端末内下書きを移行できませんでした。'))
    transaction.onabort = () => reject(transaction.error ?? new Error('端末内下書きを移行できませんでした。'))
  })
  database.close()
}

export async function writeDraft(key: string, value: unknown) {
  const database = await openDraftDatabase()
  const inferred = inferDraftMetadata(key)
  const record: StoredDraft = {
    key: getScopedStorageKey(key),
    logicalKey: activeDraftScope ? key : undefined,
    value,
    savedAt: Date.now(),
    schemaVersion: 2,
    userId: activeDraftScope?.userId,
    organizationId: activeDraftScope?.organizationId,
    runId: activeDraftScope?.runId ?? createRunId(),
    kind: inferred.kind,
    targetId: inferred.targetId,
  }
  await new Promise<void>((resolve, reject) => {
    const transaction = database.transaction(draftStoreName, 'readwrite')
    const store = transaction.objectStore(draftStoreName)
    store.put(record)
    if (activeDraftScope && record.key !== key) {
      const legacyRequest = store.get(key)
      legacyRequest.onsuccess = () => {
        const legacy = legacyRequest.result as StoredDraft | undefined
        if (legacy && isAccessibleStoredDraft(legacy)) store.delete(key)
      }
    }
    transaction.oncomplete = () => resolve()
    transaction.onerror = () => reject(transaction.error ?? new Error('端末内下書きを保存できませんでした。'))
    transaction.onabort = () => reject(transaction.error ?? new Error('端末内下書きを保存できませんでした。'))
  })
  database.close()
  notifyDraftStorageChange()
}

export async function readDraft<T>(key: string): Promise<DraftRecord<T> | null> {
  const stored = await getStoredDraft(key)
  if (!stored) return null
  if (Date.now() - stored.savedAt > draftTtlMs) {
    await deleteDraft(key)
    return null
  }
  const normalized = normalizeStoredDraft(stored)
  if (!normalized) return null
  if (needsMigration(stored, normalized)) {
    await putStoredDraft(normalized, stored.key)
  }
  return normalized as DraftRecord<T>
}

export async function listDrafts(): Promise<DraftRecord[]> {
  if (!activeDraftScope) return []
  const database = await openDraftDatabase()
  const storedDrafts = await new Promise<StoredDraft[]>((resolve, reject) => {
    const transaction = database.transaction(draftStoreName, 'readonly')
    const request = transaction.objectStore(draftStoreName).getAll()
    request.onsuccess = () => resolve((request.result as StoredDraft[]) ?? [])
    request.onerror = () => reject(request.error ?? new Error('端末内下書きを読み込めませんでした。'))
  })
  database.close()

  const selected = new Map<string, { stored: StoredDraft; normalized: DraftRecord }>()
  const staleKeys = new Set<string>()
  for (const stored of storedDrafts) {
    const normalized = normalizeStoredDraft(stored)
    if (!normalized) continue
    if (Date.now() - stored.savedAt > draftTtlMs) {
      staleKeys.add(stored.key)
      continue
    }
    const previous = selected.get(normalized.key)
    if (!previous || normalized.savedAt > previous.normalized.savedAt) {
      if (previous) staleKeys.add(previous.stored.key)
      selected.set(normalized.key, { stored, normalized })
    } else {
      staleKeys.add(stored.key)
    }
  }
  for (const staleKey of staleKeys) {
    await deleteStoredKey(staleKey)
  }
  const drafts: DraftRecord[] = []
  for (const { stored, normalized } of selected.values()) {
    if (needsMigration(stored, normalized)) await putStoredDraft(normalized, stored.key)
    drafts.push(normalized)
  }
  return drafts.sort((left, right) => right.savedAt - left.savedAt)
}

async function deleteStoredKey(key: string) {
  const database = await openDraftDatabase()
  await new Promise<void>((resolve, reject) => {
    const transaction = database.transaction(draftStoreName, 'readwrite')
    transaction.objectStore(draftStoreName).delete(key)
    transaction.oncomplete = () => resolve()
    transaction.onerror = () => reject(transaction.error ?? new Error('端末内下書きを削除できませんでした。'))
    transaction.onabort = () => reject(transaction.error ?? new Error('端末内下書きを削除できませんでした。'))
  })
  database.close()
}

export async function deleteDraft(key: string) {
  const database = await openDraftDatabase()
  await new Promise<void>((resolve, reject) => {
    const transaction = database.transaction(draftStoreName, 'readwrite')
    const store = transaction.objectStore(draftStoreName)
    if (!activeDraftScope) {
      store.delete(key)
    } else {
      store.delete(getScopedStorageKey(key))
      const legacyRequest = store.get(key)
      legacyRequest.onsuccess = () => {
        const legacy = legacyRequest.result as StoredDraft | undefined
        if (legacy && isAccessibleStoredDraft(legacy)) store.delete(key)
      }
    }
    transaction.oncomplete = () => resolve()
    transaction.onerror = () => reject(transaction.error ?? new Error('端末内下書きを削除できませんでした。'))
    transaction.onabort = () => reject(transaction.error ?? new Error('端末内下書きを削除できませんでした。'))
  })
  database.close()
  notifyDraftStorageChange()
}
