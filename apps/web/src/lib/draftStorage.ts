const draftDatabaseName = 'vehicle-management-drafts'
const draftStoreName = 'drafts'
const draftDatabaseVersion = 1
const draftTtlMs = 7 * 24 * 60 * 60 * 1000

type StoredDraft = {
  key: string
  value: unknown
  savedAt: number
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

export async function writeDraft(key: string, value: unknown) {
  const database = await openDraftDatabase()
  await new Promise<void>((resolve, reject) => {
    const transaction = database.transaction(draftStoreName, 'readwrite')
    transaction.objectStore(draftStoreName).put({ key, value, savedAt: Date.now() } satisfies StoredDraft)
    transaction.oncomplete = () => resolve()
    transaction.onerror = () => reject(transaction.error ?? new Error('端末内下書きを保存できませんでした。'))
    transaction.onabort = () => reject(transaction.error ?? new Error('端末内下書きを保存できませんでした。'))
  })
  database.close()
}

export async function readDraft<T>(key: string): Promise<{ value: T; savedAt: number } | null> {
  const database = await openDraftDatabase()
  const stored = await new Promise<StoredDraft | undefined>((resolve, reject) => {
    const transaction = database.transaction(draftStoreName, 'readonly')
    const request = transaction.objectStore(draftStoreName).get(key)
    request.onsuccess = () => resolve(request.result as StoredDraft | undefined)
    request.onerror = () => reject(request.error ?? new Error('端末内下書きを読み込めませんでした。'))
  })
  database.close()
  if (!stored) return null
  if (Date.now() - stored.savedAt > draftTtlMs) {
    await deleteDraft(key)
    return null
  }
  return { value: stored.value as T, savedAt: stored.savedAt }
}

export async function deleteDraft(key: string) {
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
