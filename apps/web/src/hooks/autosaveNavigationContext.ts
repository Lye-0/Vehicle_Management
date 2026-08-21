import { createContext, useContext, useEffect } from 'react'

export type AutosaveFlush = (force?: boolean) => Promise<boolean>

type AutosaveNavigationContextValue = {
  register: (key: string, handler: AutosaveFlush) => () => void
  flushAll: () => Promise<boolean>
}

export const AutosaveNavigationContext = createContext<AutosaveNavigationContextValue>({
  register: () => () => undefined,
  flushAll: async () => true,
})

export function useAutosaveNavigation() {
  return useContext(AutosaveNavigationContext)
}

export function useRegisterAutosave(key: string, handler: AutosaveFlush) {
  const { register } = useAutosaveNavigation()
  useEffect(() => register(key, handler), [handler, key, register])
}
