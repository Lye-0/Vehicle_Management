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
  getDirectoryHandle: (name: string, options?: { create?: boolean }) => Promise<DirectoryHandleLike>
  getFileHandle: (name: string, options?: { create?: boolean }) => Promise<FileHandleLike>
  entries: () => AsyncIterableIterator<[string, FileHandleLike | DirectoryHandleLike]>
  removeEntry: (name: string) => Promise<void>
}

const directoryStoreName = 'backup-directory'
const directoryKey = 'selected'
const backupDirectoryName = 'Vehicle Management Backup'
const backupFilePattern = /^vehicle-management-backup-.*\.json$/u

export type PcBackupFile = {
  name: string
  size: number
  lastModified: number
  createdAt: string
  note: string
  keepForever: boolean
}

export type PcBackupListing = {
  available: boolean
  parentName: string | null
  directoryName: string | null
  files: PcBackupFile[]
}

let activeRestoreDirectory: DirectoryHandleLike | null = null
let preparedWritableDirectory: DirectoryHandleLike | null = null

export async function saveBackupToPc(backup: BackupExport, keepForever: boolean, retentionDays: number) {
  const directory = await getWritableDirectory()
  const serialized = JSON.stringify(backup)
  const fileName = `vehicle-management-backup-${formatFileTimestamp(new Date(backup.createdAt))}${keepForever ? '-permanent' : ''}.json`
  if (!directory) return { mode: 'unavailable' as const, fileName }
  try {
    const fileHandle = await directory.getFileHandle(fileName, { create: true })
    const writable = await fileHandle.createWritable()
    await writable.write(serialized)
    await writable.close()
    await prunePcBackups(directory, retentionDays)
    return { mode: 'folder' as const, fileName, directoryName: directory.name }
  } catch {
    preparedWritableDirectory = null
    return { mode: 'unavailable' as const, fileName }
  }
}

export async function preparePcBackupDestination() {
  try {
    return Boolean(await getWritableDirectory())
  } catch {
    preparedWritableDirectory = null
    return false
  }
}

export async function changePcBackupDirectory() {
  const picker = getDirectoryPicker()
  if (!picker) return { mode: 'unsupported' as const }
  try {
    const parent = await picker({ mode: 'readwrite' })
    const directory = await getBackupDirectory(parent)
    await storeDirectory(parent)
    preparedWritableDirectory = directory
    return { mode: 'selected' as const, directoryName: directory.name }
  } catch (reason: unknown) {
    if (isAbortError(reason)) return { mode: 'cancelled' as const }
    throw reason
  }
}

export async function listDefaultPcBackups(): Promise<PcBackupListing> {
  const stored = await readStoredDirectory()
  if (!stored || !(await ensureReadPermission(stored))) {
    activeRestoreDirectory = null
    return { available: false, parentName: stored?.name ?? null, directoryName: stored ? backupDirectoryName : null, files: [] }
  }

  const resolved = await resolveRestoreDirectory(stored)
  activeRestoreDirectory = resolved.directory
  return { available: true, parentName: stored.name, directoryName: resolved.directory.name, files: resolved.files }
}

export async function choosePcBackupDirectory() {
  const picker = getDirectoryPicker()
  if (!picker) return { mode: 'unsupported' as const }
  try {
    const selected = await picker({ mode: 'read' })
    const resolved = await resolveRestoreDirectory(selected)
    activeRestoreDirectory = resolved.directory
    return { mode: 'selected' as const, available: true, parentName: selected.name, directoryName: resolved.directory.name, files: resolved.files }
  } catch (reason: unknown) {
    if (isAbortError(reason)) return { mode: 'cancelled' as const }
    throw reason
  }
}

export async function readPcBackup(name: string): Promise<BackupExport> {
  if (!activeRestoreDirectory) throw new Error('復元元のPCバックアップフォルダが選択されていません。')
  if (!backupFilePattern.test(name)) throw new Error('選択したファイルはバックアップ形式ではありません。')
  const fileHandle = await activeRestoreDirectory.getFileHandle(name)
  const file = await fileHandle.getFile()
  return parseBackupExport(await file.text())
}

export async function updatePcBackupFileRetention(name: string, keepForever: boolean): Promise<PcBackupFile> {
  if (!activeRestoreDirectory) throw new Error('復元元のPCバックアップフォルダが選択されていません。')
  if (!backupFilePattern.test(name)) throw new Error('選択したファイルはバックアップ形式ではありません。')

  const sourceHandle = await activeRestoreDirectory.getFileHandle(name)
  const sourceFile = await sourceHandle.getFile()
  const serialized = await sourceFile.text()
  const backup = parseBackupExport(serialized)
  const nextName = getPcBackupFileName(name, keepForever)

  if (nextName !== name) {
    const targetHandle = await activeRestoreDirectory.getFileHandle(nextName, { create: true })
    const writable = await targetHandle.createWritable()
    await writable.write(serialized)
    await writable.close()
    await activeRestoreDirectory.removeEntry(name)
  }

  const targetHandle = await activeRestoreDirectory.getFileHandle(nextName)
  const targetFile = await targetHandle.getFile()
  return toPcBackupFile(nextName, targetFile, backup, keepForever)
}

export async function deletePcBackupFile(name: string) {
  if (!activeRestoreDirectory) throw new Error('復元元のPCバックアップフォルダが選択されていません。')
  if (!backupFilePattern.test(name)) throw new Error('選択したファイルはバックアップ形式ではありません。')
  await activeRestoreDirectory.removeEntry(name)
}

async function getWritableDirectory() {
  if (preparedWritableDirectory) return preparedWritableDirectory
  const stored = await readStoredDirectory()
  if (stored && await ensurePermission(stored)) {
    preparedWritableDirectory = await getBackupDirectory(stored)
    return preparedWritableDirectory
  }
  const picker = getDirectoryPicker()
  if (!picker) return null
  try {
    const selected = await picker({ mode: 'readwrite' })
    const directory = await getBackupDirectory(selected)
    await storeDirectory(selected)
    preparedWritableDirectory = directory
    return directory
  } catch (reason: unknown) {
    if (isAbortError(reason)) return null
    throw reason
  }
}

function getDirectoryPicker() {
  return (window as Window & { showDirectoryPicker?: (options?: { mode?: 'read' | 'readwrite' }) => Promise<DirectoryHandleLike> }).showDirectoryPicker
}

async function getBackupDirectory(parent: DirectoryHandleLike, create = true) {
  return parent.getDirectoryHandle(backupDirectoryName, { create })
}

function isAbortError(reason: unknown) {
  return reason instanceof DOMException && reason.name === 'AbortError'
}

async function ensurePermission(directory: DirectoryHandleLike) {
  if (!directory.queryPermission) return true
  const options = { mode: 'readwrite' as const }
  const current = await directory.queryPermission(options)
  if (current === 'granted') return true
  if (current !== 'prompt' || !directory.requestPermission) return false
  return (await directory.requestPermission(options)) === 'granted'
}

async function ensureReadPermission(directory: DirectoryHandleLike) {
  if (!directory.queryPermission) return true
  const options = { mode: 'read' as const }
  const current = await directory.queryPermission(options)
  if (current === 'granted') return true
  if (current !== 'prompt' || !directory.requestPermission) return false
  return (await directory.requestPermission(options)) === 'granted'
}

async function resolveRestoreDirectory(parent: DirectoryHandleLike) {
  const directFiles = await readPcBackupFiles(parent)
  let child: DirectoryHandleLike | null = null
  let childFiles: PcBackupFile[] = []
  try {
    child = await getBackupDirectory(parent, false)
    childFiles = await readPcBackupFiles(child)
  } catch {
    // A user-selected folder does not need to contain the standard subfolder.
  }
  if (child && childFiles.length > 0) return { directory: child, files: childFiles }
  if (directFiles.length > 0) return { directory: parent, files: directFiles }
  if (child) return { directory: child, files: childFiles }
  return { directory: parent, files: directFiles }
}

async function readPcBackupFiles(directory: DirectoryHandleLike) {
  const files: PcBackupFile[] = []
  for await (const [name, entry] of directory.entries()) {
    if (entry.kind !== 'file' || !backupFilePattern.test(name)) continue
    try {
      const file = await (entry as FileHandleLike).getFile()
      const backup = parseBackupExport(await file.text())
      files.push(toPcBackupFile(name, file, backup, isPermanentPcBackupFile(name)))
    } catch {
      // Ignore unrelated or incomplete JSON files in the selected folder.
    }
  }
  return files.sort((left, right) => new Date(right.createdAt).getTime() - new Date(left.createdAt).getTime())
}

function toPcBackupFile(name: string, file: File, backup: BackupExport, keepForever: boolean): PcBackupFile {
  return {
    name,
    size: file.size,
    lastModified: file.lastModified,
    createdAt: backup.createdAt,
    note: backup.note?.trim() ?? '',
    keepForever,
  }
}

function isPermanentPcBackupFile(name: string) {
  return /-permanent\.json$/u.test(name)
}

function getPcBackupFileName(name: string, keepForever: boolean) {
  if (keepForever) return name.endsWith('.json') && !isPermanentPcBackupFile(name) ? name.replace(/\.json$/u, '-permanent.json') : name
  return name.replace(/-permanent\.json$/u, '.json')
}

function parseBackupExport(serialized: string): BackupExport {
  let value: unknown
  try {
    value = JSON.parse(serialized)
  } catch {
    throw new Error('バックアップファイルのJSONを読み込めませんでした。')
  }
  if (!isRecord(value) || value.version !== 1 || typeof value.organizationId !== 'string' || typeof value.createdAt !== 'string' || !isRecord(value.tables) || !Array.isArray(value.files)) {
    throw new Error('バックアップファイルの形式が正しくありません。')
  }
  return value as BackupExport
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

async function prunePcBackups(directory: DirectoryHandleLike, retentionDays: number) {
  const cutoff = Date.now() - retentionDays * 86_400_000
  for await (const [name, entry] of directory.entries()) {
    if (entry.kind !== 'file' || !backupFilePattern.test(name) || name.includes('-permanent.json')) continue
    const file = await (entry as FileHandleLike).getFile()
    if (file.lastModified < cutoff) await directory.removeEntry(name)
  }
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
