import type { BackupExport } from './backupsApi'

type PermissionState = 'granted' | 'prompt' | 'denied'
type FileHandleLike = {
  kind: 'file'
  getFile: () => Promise<File>
  createWritable: () => Promise<{ write: (value: string) => Promise<void>; close: () => Promise<void> }>
}
type DirectoryHandleLike = {
  kind: 'directory'
  name: string
  queryPermission?: (options?: { mode?: 'read' | 'readwrite' }) => Promise<PermissionState>
  requestPermission?: (options?: { mode?: 'read' | 'readwrite' }) => Promise<PermissionState>
  getFileHandle: (name: string, options?: { create?: boolean }) => Promise<FileHandleLike>
  entries: () => AsyncIterableIterator<[string, FileHandleLike | DirectoryHandleLike]>
  removeEntry: (name: string) => Promise<void>
}

const directoryStoreName = 'backup-directory'
const directoryKey = 'selected'

export async function saveBackupToPc(backup: BackupExport, keepForever: boolean, retentionDays: number) {
  const directory = await getWritableDirectory()
  const serialized = JSON.stringify(backup)
  const fileName = `vehicle-management-backup-${formatFileTimestamp(new Date(backup.createdAt))}${keepForever ? '-permanent' : ''}.json`
  if (!directory) {
    downloadBackup(serialized, fileName)
    return { mode: 'download' as const, fileName }
  }
  const fileHandle = await directory.getFileHandle(fileName, { create: true })
  const writable = await fileHandle.createWritable()
  await writable.write(serialized)
  await writable.close()
  await prunePcBackups(directory, retentionDays)
  return { mode: 'folder' as const, fileName, directoryName: directory.name }
}

async function getWritableDirectory() {
  const stored = await readStoredDirectory()
  if (stored && await ensurePermission(stored)) return stored
  const picker = (window as Window & { showDirectoryPicker?: (options?: { mode?: 'read' | 'readwrite' }) => Promise<DirectoryHandleLike> }).showDirectoryPicker
  if (!picker) return null
  const selected = await picker({ mode: 'readwrite' })
  await storeDirectory(selected)
  return selected
}

async function ensurePermission(directory: DirectoryHandleLike) {
  if (!directory.queryPermission) return true
  const options = { mode: 'readwrite' as const }
  const current = await directory.queryPermission(options)
  if (current === 'granted') return true
  if (current !== 'prompt' || !directory.requestPermission) return false
  return (await directory.requestPermission(options)) === 'granted'
}

async function prunePcBackups(directory: DirectoryHandleLike, retentionDays: number) {
  const cutoff = Date.now() - retentionDays * 86_400_000
  for await (const [name, entry] of directory.entries()) {
    if (entry.kind !== 'file' || !/^vehicle-management-backup-.*\.json$/u.test(name) || name.includes('-permanent.json')) continue
    const file = await (entry as FileHandleLike).getFile()
    if (file.lastModified < cutoff) await directory.removeEntry(name)
  }
}

function downloadBackup(serialized: string, fileName: string) {
  const blob = new Blob([serialized], { type: 'application/json' })
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = fileName
  document.body.appendChild(link)
  link.click()
  link.remove()
  window.setTimeout(() => URL.revokeObjectURL(url), 10_000)
}

function formatFileTimestamp(value: Date) {
  return value.toISOString().replace(/[:.]/gu, '-').replace(/Z$/u, 'Z')
}

function openDirectoryDatabase() {
  return new Promise<IDBDatabase>((resolve, reject) => {
    const request = indexedDB.open('vehicle-management-local', 1)
    request.onupgradeneeded = () => request.result.createObjectStore(directoryStoreName)
    request.onsuccess = () => resolve(request.result)
    request.onerror = () => reject(request.error ?? new Error('PC保存先を読み込めませんでした。'))
  })
}

async function storeDirectory(directory: DirectoryHandleLike) {
  try {
    const database = await openDirectoryDatabase()
    await new Promise<void>((resolve, reject) => {
      const transaction = database.transaction(directoryStoreName, 'readwrite')
      transaction.objectStore(directoryStoreName).put(directory, directoryKey)
      transaction.oncomplete = () => resolve()
      transaction.onerror = () => reject(transaction.error ?? new Error('PC保存先を保存できませんでした。'))
    })
    database.close()
  } catch {
    // IndexedDB is only a convenience. The current user gesture can still save to the selected directory.
  }
}

async function readStoredDirectory() {
  try {
    const database = await openDirectoryDatabase()
    const directory = await new Promise<DirectoryHandleLike | undefined>((resolve, reject) => {
      const request = database.transaction(directoryStoreName, 'readonly').objectStore(directoryStoreName).get(directoryKey)
      request.onsuccess = () => resolve(request.result as DirectoryHandleLike | undefined)
      request.onerror = () => reject(request.error)
    })
    database.close()
    return directory
  } catch {
    return undefined
  }
}
