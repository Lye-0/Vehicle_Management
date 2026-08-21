import { createContext, useContext } from 'react'
import type { DraftKind, DraftRecord } from '../lib/draftStorage'

export type DraftRecoverySection = 'customers' | 'sales' | 'maintenance' | 'settings'
export type DraftRecoveryDestination = { section: DraftRecoverySection; recordId?: string }

export type DraftRecoveryContextValue = {
  drafts: DraftRecord[]
  notificationDrafts: DraftRecord[]
  currentRunId: string
  pendingRestore: DraftRecord | null
  notificationOpen: boolean
  openNotifications: () => void
  closeNotifications: () => void
  openRecovery: (key?: string) => void
  restoreDraft: (draft: DraftRecord) => void
  deferDraft: (draft: DraftRecord) => void
  discardDraft: (draft: DraftRecord) => void
  registerActiveDraft: (kind: DraftKind, key: string | null) => void
  holdDraft: (draft: DraftRecord) => void
  acknowledgeRestore: (key: string) => void
  getAutoResumeDraft: (kind: DraftKind) => DraftRecord | null
  refreshDrafts: () => Promise<void>
}

export const DraftRecoveryContext = createContext<DraftRecoveryContextValue | null>(null)

export function useDraftRecovery() {
  const context = useContext(DraftRecoveryContext)
  if (!context) throw new Error('DraftRecoveryProviderの内側で使用してください。')
  return context
}
