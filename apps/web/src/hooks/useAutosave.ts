import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { deleteDraft, writeDraft } from '../lib/draftStorage'
import { useRegisterAutosave, type AutosaveFlush } from './autosaveNavigationContext'

export type AutosaveStatus = 'idle' | 'waiting' | 'local-saved' | 'saving' | 'saved' | 'error' | 'blocked'

export class AutosaveBlockedError extends Error {
  constructor(message = '確認が必要なため自動保存を保留しています。') {
    super(message)
    this.name = 'AutosaveBlockedError'
  }
}

type SaveResult = boolean | void

type UseAutosaveOptions<T> = {
  value: T
  dirty: boolean
  enabled?: boolean
  serverEnabled?: boolean
  serverSaveDeferred?: boolean
  registrationKey: string
  storageKey?: string | null
  save: (value: T) => Promise<SaveResult>
  onError?: (error: unknown) => void
  onBlocked?: (error: AutosaveBlockedError) => void
  idleMs?: number
  maxMs?: number
  localMs?: number
}

function valueSignature(value: unknown) {
  try {
    return JSON.stringify(value)
  } catch {
    return String(value)
  }
}

export function useAutosave<T>({ value, dirty, enabled = true, serverEnabled = enabled, serverSaveDeferred = false, registrationKey, storageKey = null, save, onError, onBlocked, idleMs = 10_000, maxMs = 60_000, localMs = 500 }: UseAutosaveOptions<T>) {
  const [status, setStatus] = useState<AutosaveStatus>('idle')
  const [lastSavedAt, setLastSavedAt] = useState<number | null>(null)
  const valueRef = useRef(value)
  const dirtyRef = useRef(dirty)
  const enabledRef = useRef(enabled)
  const serverEnabledRef = useRef(serverEnabled)
  const storageKeyRef = useRef(storageKey)
  const saveRef = useRef(save)
  const onErrorRef = useRef(onError)
  const onBlockedRef = useRef(onBlocked)
  const signature = useMemo(() => valueSignature(value), [value])
  const lastSavedSignatureRef = useRef<string | null>(null)
  const dirtyStartedAtRef = useRef<number | null>(null)
  const idleTimerRef = useRef<number | null>(null)
  const maxTimerRef = useRef<number | null>(null)
  const localTimerRef = useRef<number | null>(null)
  const inFlightRef = useRef<Promise<boolean> | null>(null)
  const pendingRef = useRef(false)
  const blockedSignatureRef = useRef<string | null>(null)
  const errorSignatureRef = useRef<string | null>(null)

  valueRef.current = value
  dirtyRef.current = dirty
  enabledRef.current = enabled
  serverEnabledRef.current = serverEnabled
  storageKeyRef.current = storageKey
  saveRef.current = save
  onErrorRef.current = onError
  onBlockedRef.current = onBlocked

  const clearTimers = useCallback(() => {
    if (idleTimerRef.current !== null) window.clearTimeout(idleTimerRef.current)
    if (maxTimerRef.current !== null) window.clearTimeout(maxTimerRef.current)
    if (localTimerRef.current !== null) window.clearTimeout(localTimerRef.current)
    idleTimerRef.current = null
    maxTimerRef.current = null
    localTimerRef.current = null
  }, [])

  useEffect(() => {
    clearTimers()
    lastSavedSignatureRef.current = null
    blockedSignatureRef.current = null
    errorSignatureRef.current = null
    dirtyStartedAtRef.current = null
    setStatus('idle')
    setLastSavedAt(null)
  }, [clearTimers, registrationKey, storageKey])

  const persistLocalDraft = useCallback(async () => {
    const currentKey = storageKeyRef.current
    if (!enabledRef.current || !dirtyRef.current || !currentKey) return
    const snapshot = valueRef.current
    const snapshotSignature = valueSignature(snapshot)
    try {
      await writeDraft(currentKey, snapshot)
      if (dirtyRef.current && valueSignature(valueRef.current) === snapshotSignature) setStatus((current) => current === 'waiting' || current === 'idle' ? 'local-saved' : current)
    } catch {
      // 端末内下書き保存に失敗しても、サーバー自動保存は継続する。
    }
  }, [])

  const flush = useCallback<AutosaveFlush>(async (force = false): Promise<boolean> => {
    if (!serverEnabledRef.current || !dirtyRef.current) return true
    if (force) {
      blockedSignatureRef.current = null
      errorSignatureRef.current = null
    }

    while (true) {
      if (inFlightRef.current) {
        pendingRef.current = true
        const result = await inFlightRef.current
        if (!pendingRef.current || !dirtyRef.current) return result
        pendingRef.current = false
        continue
      }

      if (idleTimerRef.current !== null) window.clearTimeout(idleTimerRef.current)
      if (maxTimerRef.current !== null) window.clearTimeout(maxTimerRef.current)
      idleTimerRef.current = null
      maxTimerRef.current = null

      const snapshot = valueRef.current
      const snapshotSignature = valueSignature(snapshot)
      if (lastSavedSignatureRef.current === snapshotSignature && !dirtyRef.current) {
        setStatus('saved')
        return true
      }

      setStatus('saving')
      const promise = (async () => {
        try {
          const result = await saveRef.current(snapshot)
          if (result === false) {
            errorSignatureRef.current = snapshotSignature
            setStatus('error')
            return false
          }
          blockedSignatureRef.current = null
          errorSignatureRef.current = null
          lastSavedSignatureRef.current = snapshotSignature
          setLastSavedAt(Date.now())
          setStatus('saved')
          const currentKey = storageKeyRef.current
          if (currentKey && valueSignature(valueRef.current) === snapshotSignature) {
            try { await deleteDraft(currentKey) } catch { /* 下書き削除失敗はサーバー保存成功を取り消さない */ }
          }
          return true
        } catch (error) {
          if (error instanceof AutosaveBlockedError) {
            blockedSignatureRef.current = snapshotSignature
            setStatus('blocked')
            onBlockedRef.current?.(error)
          } else {
            errorSignatureRef.current = snapshotSignature
            setStatus('error')
            onErrorRef.current?.(error)
          }
          return false
        }
      })()
      inFlightRef.current = promise
      const result = await promise
      inFlightRef.current = null
      if (!pendingRef.current || !dirtyRef.current) return result
      pendingRef.current = false
    }
  }, [])

  useRegisterAutosave(`autosave:${registrationKey}`, flush)

  useEffect(() => {
    if (!serverEnabled) {
      if (idleTimerRef.current !== null) window.clearTimeout(idleTimerRef.current)
      if (maxTimerRef.current !== null) window.clearTimeout(maxTimerRef.current)
      idleTimerRef.current = null
      maxTimerRef.current = null
      dirtyStartedAtRef.current = null
      if (!dirty) setStatus((current) => current === 'saved' ? current : 'idle')
      else if (serverSaveDeferred) setStatus((current) => current === 'saving' ? current : 'waiting')
      return
    }
    if (!dirty || signature === lastSavedSignatureRef.current || signature === blockedSignatureRef.current || signature === errorSignatureRef.current) {
      if (!dirty) {
        clearTimers()
        dirtyStartedAtRef.current = null
        setStatus((current) => current === 'saved' ? current : 'idle')
      }
      return
    }

    const now = Date.now()
    if (dirtyStartedAtRef.current === null) {
      dirtyStartedAtRef.current = now
      maxTimerRef.current = window.setTimeout(() => { void flush() }, maxMs)
    }
    if (idleTimerRef.current !== null) window.clearTimeout(idleTimerRef.current)
    idleTimerRef.current = window.setTimeout(() => { void flush() }, idleMs)
    setStatus((current) => current === 'saving' ? current : 'waiting')
  }, [clearTimers, dirty, enabled, flush, idleMs, maxMs, serverEnabled, serverSaveDeferred, signature])

  useEffect(() => {
    if (!enabled || !dirty || !storageKey) return
    if (localTimerRef.current !== null) window.clearTimeout(localTimerRef.current)
    localTimerRef.current = window.setTimeout(() => { void persistLocalDraft() }, localMs)
    return () => {
      if (localTimerRef.current !== null) window.clearTimeout(localTimerRef.current)
      localTimerRef.current = null
    }
  }, [dirty, enabled, localMs, persistLocalDraft, signature, storageKey])

  useEffect(() => {
    if (!enabled) return
    const flushWhenHidden = () => {
      if (document.visibilityState === 'hidden') {
        void persistLocalDraft()
        void flush()
      }
    }
    const flushOnPageHide = () => {
      void persistLocalDraft()
      void flush()
    }
    document.addEventListener('visibilitychange', flushWhenHidden)
    window.addEventListener('pagehide', flushOnPageHide)
    return () => {
      document.removeEventListener('visibilitychange', flushWhenHidden)
      window.removeEventListener('pagehide', flushOnPageHide)
    }
  }, [enabled, flush, persistLocalDraft])

  useEffect(() => () => clearTimers(), [clearTimers])

  return { status, lastSavedAt, flush }
}
