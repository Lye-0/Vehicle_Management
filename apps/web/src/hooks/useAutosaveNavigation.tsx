import { useCallback, useRef, type ReactNode } from 'react'
import { AutosaveNavigationContext, type AutosaveFlush } from './autosaveNavigationContext'

type FlushHandler = AutosaveFlush

export function AutosaveNavigationProvider({ children }: { children: ReactNode }) {
  const handlersRef = useRef(new Map<string, FlushHandler>())
  const register = useCallback((key: string, handler: FlushHandler) => {
    handlersRef.current.set(key, handler)
    return () => {
      if (handlersRef.current.get(key) === handler) handlersRef.current.delete(key)
    }
  }, [])
  const flushAll = useCallback(async () => {
    const results = await Promise.all(Array.from(handlersRef.current.values()).map((handler) => handler()))
    return results.every(Boolean)
  }, [])

  return <AutosaveNavigationContext.Provider value={{ register, flushAll }}>{children}</AutosaveNavigationContext.Provider>
}
