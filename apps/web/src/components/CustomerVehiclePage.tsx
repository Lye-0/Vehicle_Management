import { useEffect, useMemo, useRef, useState, type ChangeEvent, type DragEvent, type FormEvent, type PointerEvent as ReactPointerEvent, type ReactNode } from 'react'
import type { LucideIcon } from 'lucide-react'
import { normalizeDisplacement, normalizeMileage, normalizeModelYear, normalizePhone, normalizePostalCode } from '@vehicle-management/shared'
import {
  CarFront,
  CalendarDays,
  ChevronLeft,
  ChevronRight,
  Download,
  Eye,
  BriefcaseBusiness,
  FileText,
  Image as ImageIcon,
  Mail,
  MapPin,
  Paperclip,
  Pencil,
  Phone,
  Plus,
  Search,
  Trash2,
  UserRound,
  X,
} from 'lucide-react'
import {
  createCustomer,
  createVehicle,
  deleteCustomer,
  deleteVehicle,
  deleteVehicleFile,
  fetchVehicleFile,
  fetchCustomerDetail,
  fetchCustomerDeletionImpact,
  fetchCustomerSummaries,
  fetchVehicleHistory,
  fetchVehicleDeletionImpact,
  fetchVehiclelessDocuments,
  type Customer,
  type CustomerInput,
  type MasterDeletionImpact,
  type Attachment,
  type Vehicle,
  type VehicleInput,
  type VehicleHistory,
  type VehiclelessDocuments,
  updateCustomer,
  updateVehicle,
  uploadVehicleFile,
} from '../lib/customerApi'
import { AutosaveBlockedError, useAutosave, type AutosaveStatus as AutosaveState } from '../hooks/useAutosave'
import { createDraftRunId, deleteDraft, readDraft } from '../lib/draftStorage'
import { DateCalendarButton } from './DateCalendarButton'
import { NormalizedInput } from './NormalizedValueInput'
import { AutosaveStatus } from './AutosaveStatus'
import { useDraftRecovery } from '../hooks/draftRecoveryContext'

const emptyCustomerForm: CustomerInput = { name: '', kana: '', phone: '', email: '', postalCode: '', address: '', birthDate: '', employer: '', memo: '' }
const emptyVehicleForm: VehicleInput = { maker: '', model: '', modelType: '', plate: '', vin: '', year: '', inspectionDate: '', mileage: '', color: '', displacement: '', transmission: '', note: '', freeItem1: '', freeItem2: '', freeItem3: '' }
function normalizeCustomerForm(form: CustomerInput): CustomerInput {
  return { ...form, phone: normalizePhone(form.phone), postalCode: normalizePostalCode(form.postalCode ?? '') }
}
function normalizeVehicleForm(form: VehicleInput): VehicleInput {
  return { ...form, year: normalizeModelYear(form.year), mileage: normalizeMileage(form.mileage), displacement: normalizeDisplacement(form.displacement) }
}
function formSignature(value: unknown) {
  return JSON.stringify(value)
}
const customerSearchFields = ['すべて', '顧客名', 'ふりがな', 'メールアドレス', '電話番号', '住所', '車名', '登録番号', '車台番号'] as const
type CustomerSearchField = (typeof customerSearchFields)[number]
const customerSearchPlaceholders: Record<CustomerSearchField, string> = {
  すべて: '顧客名、ふりがな、メールアドレス、電話番号、住所、車名、登録番号、車台番号で検索',
  顧客名: '顧客名で検索',
  ふりがな: 'ふりがなで検索',
  メールアドレス: 'メールアドレスで検索',
  電話番号: '電話番号で検索',
  住所: '住所で検索',
  車名: '車名で検索',
  登録番号: '登録番号で検索',
  車台番号: '車台番号で検索',
}
type AttachmentPreview = { vehicleId: string; attachment: Attachment; url: string }
export type CustomerVehicleNavigation = { section: 'customers'; customerId: string; vehicleId: string }
export type VehicleHistoryNavigation = { section: 'sales' | 'maintenance' | 'inspections' | 'payments'; recordId: string }
type OcrStatus = 'idle' | 'running' | 'ready' | 'empty' | 'error'
type OcrTextRegion = { text: string; x0: number; y0: number; x1: number; y1: number; confidence: number }
type OcrImageSize = { width: number; height: number; renderedWidth: number; renderedHeight: number }
type OcrPointerSelection = { pointerId: number; anchorIndex: number; focusIndex: number }
type PendingMasterDeletion = { impact: MasterDeletionImpact; expectedUpdatedAt: string | null }

export function CustomerVehiclePage({ onNavigate, initialCustomerId, initialVehicleId, onNavigationConsumed }: { onNavigate?: (target: VehicleHistoryNavigation) => void; initialCustomerId?: string; initialVehicleId?: string; onNavigationConsumed?: () => void } = {}) {
  const [customers, setCustomers] = useState<Customer[]>([])
  const [customerNextCursor, setCustomerNextCursor] = useState<string | null>(null)
  const [customerHasMore, setCustomerHasMore] = useState(false)
  const [loadingMoreCustomers, setLoadingMoreCustomers] = useState(false)
  const [query, setQuery] = useState('')
  const [searchField, setSearchField] = useState<CustomerSearchField>('すべて')
  const [selectedCustomerId, setSelectedCustomerId] = useState('')
  const [selectedVehicleId, setSelectedVehicleId] = useState('')
  const [vehiclelessDocumentsByCustomer, setVehiclelessDocumentsByCustomer] = useState<Record<string, VehiclelessDocuments>>({})
  const [vehiclelessLoadingCustomerId, setVehiclelessLoadingCustomerId] = useState('')
  const [selectedVehiclelessCustomerId, setSelectedVehiclelessCustomerId] = useState('')
  const [mobileWorkspaceView, setMobileWorkspaceView] = useState<'list' | 'detail'>(initialCustomerId ? 'detail' : 'list')
  const [customerDialogOpen, setCustomerDialogOpen] = useState(false)
  const [vehicleDialogOpen, setVehicleDialogOpen] = useState(false)
  const [editingCustomerId, setEditingCustomerId] = useState<string | null>(null)
  const [editingVehicleId, setEditingVehicleId] = useState<string | null>(null)
  const [customerForm, setCustomerForm] = useState<CustomerInput>(emptyCustomerForm)
  const [vehicleForm, setVehicleForm] = useState<VehicleInput>(emptyVehicleForm)
  const [newCustomerStorageKey, setNewCustomerStorageKey] = useState('customer-new')
  const [newVehicleStorageKey, setNewVehicleStorageKey] = useState('vehicle-new')
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [deletionLoading, setDeletionLoading] = useState(false)
  const [pendingMasterDeletion, setPendingMasterDeletion] = useState<PendingMasterDeletion | null>(null)
  const [customerDirty, setCustomerDirty] = useState(false)
  const [vehicleDirty, setVehicleDirty] = useState(false)
  const [error, setError] = useState('')
  const [attachmentPreview, setAttachmentPreview] = useState<AttachmentPreview | null>(null)
  const initialNavigationRef = useRef({ customerId: initialCustomerId, vehicleId: initialVehicleId })
  const onNavigationConsumedRef = useRef(onNavigationConsumed)
  const customerAutosaveCancelLocalDraftRef = useRef<(key?: string) => Promise<void>>(async () => undefined)
  const vehicleAutosaveCancelLocalDraftRef = useRef<(key?: string) => Promise<void>>(async () => undefined)
  const customerSavedSignatureRef = useRef('')
  const vehicleSavedSignatureRef = useRef('')
  const customerUpdatedAtRef = useRef<string | null>(null)
  const vehicleUpdatedAtRef = useRef<string | null>(null)
  const openEditCustomerDialogRef = useRef<(customer: Customer) => void>(() => undefined)
  const openEditVehicleDialogRef = useRef<(vehicle: Vehicle) => void>(() => undefined)
  const { pendingRestore, acknowledgeRestore, currentRunId, getAutoResumeDraft, refreshDrafts, registerActiveDraft } = useDraftRecovery()
  onNavigationConsumedRef.current = onNavigationConsumed

  useEffect(() => {
    let active = true
    setLoading(true)
    void fetchCustomerSummaries({ limit: 50 }).then((result) => {
      if (!active) return
      const nextCustomers = result.customers.map(mapCustomerSummaryToRecord)
      setCustomers((current) => {
        const detailedTarget = current.find((customer) => customer.id === initialNavigationRef.current.customerId && !customer.isSummary)
        return detailedTarget ? [detailedTarget, ...nextCustomers.filter((customer) => customer.id !== detailedTarget.id)] : nextCustomers
      })
      setCustomerNextCursor(result.nextCursor)
      setCustomerHasMore(result.hasMore)
      const targetCustomer = initialNavigationRef.current.customerId ? nextCustomers.find((customer) => customer.id === initialNavigationRef.current.customerId) : undefined
      if (targetCustomer) {
        setSelectedCustomerId(targetCustomer.id)
        setSelectedVehicleId('')
        setMobileWorkspaceView('detail')
        onNavigationConsumedRef.current?.()
      }
      setError('')
    }).catch((reason: unknown) => {
      if (active) setError(getErrorMessage(reason))
    }).finally(() => {
      if (active) setLoading(false)
    })
    return () => { active = false }
  }, [])

  useEffect(() => {
    if (!attachmentPreview) return
    function handlePreviewKeyDown(event: KeyboardEvent) {
      if (event.key === 'Escape') setAttachmentPreview(null)
    }
    window.addEventListener('keydown', handlePreviewKeyDown)
    return () => {
      window.removeEventListener('keydown', handlePreviewKeyDown)
      URL.revokeObjectURL(attachmentPreview.url)
    }
  }, [attachmentPreview])

  useEffect(() => {
    let active = true
    const timer = window.setTimeout(() => {
      void fetchCustomerSummaries({ q: query, field: searchField, limit: 50 }).then((result) => {
        if (!active) return
        setCustomers(result.customers.map(mapCustomerSummaryToRecord))
        setCustomerNextCursor(result.nextCursor)
        setCustomerHasMore(result.hasMore)
      }).catch((reason: unknown) => {
        if (active) setError(getErrorMessage(reason))
      })
    }, query.trim() || searchField !== 'すべて' ? 280 : 0)
    return () => { active = false; window.clearTimeout(timer) }
  }, [query, searchField])

  const filteredCustomers = customers

  useEffect(() => {
    if (filteredCustomers.some((customer) => customer.id === selectedCustomerId)) return
    const nextCustomerId = filteredCustomers[0]?.id ?? ''
    if (nextCustomerId === selectedCustomerId) return
    setSelectedCustomerId(nextCustomerId)
    setSelectedVehicleId('')
    setSelectedVehiclelessCustomerId('')
  }, [filteredCustomers, selectedCustomerId])

  async function loadMoreCustomers() {
    if (!customerHasMore || !customerNextCursor || loadingMoreCustomers) return
    setLoadingMoreCustomers(true)
    try {
      const result = await fetchCustomerSummaries({ q: query, field: searchField, cursor: customerNextCursor, limit: 50 })
      setCustomers((current) => [...current, ...result.customers.map(mapCustomerSummaryToRecord).filter((customer) => !current.some((item) => item.id === customer.id))])
      setCustomerNextCursor(result.nextCursor)
      setCustomerHasMore(result.hasMore)
    } catch (reason: unknown) {
      setError(getErrorMessage(reason))
    } finally {
      setLoadingMoreCustomers(false)
    }
  }

  const selectedCustomer = filteredCustomers.find((customer) => customer.id === selectedCustomerId) ?? filteredCustomers[0] ?? null
  const selectedVehicle = selectedCustomer?.vehicles.find((vehicle) => vehicle.id === selectedVehicleId) ?? selectedCustomer?.vehicles[0] ?? null
  const selectedVehiclelessDocuments = selectedCustomer ? vehiclelessDocumentsByCustomer[selectedCustomer.id] ?? null : null
  const vehiclelessLoading = Boolean(selectedCustomer && vehiclelessLoadingCustomerId === selectedCustomer.id)
  const selectedCustomerVehicleCount = selectedCustomer?.vehicles.length ?? 0
  const selectedVehiclelessDocumentCount = selectedVehiclelessDocuments?.documents.length ?? 0

  useEffect(() => {
    if (!selectedCustomerId || selectedCustomerVehicleCount > 0 || selectedVehiclelessDocumentCount === 0 || selectedVehiclelessCustomerId === selectedCustomerId) return
    setSelectedVehiclelessCustomerId(selectedCustomerId)
  }, [selectedCustomerId, selectedCustomerVehicleCount, selectedVehiclelessCustomerId, selectedVehiclelessDocumentCount])

  useEffect(() => {
    if (!selectedCustomerId) return
    const current = customers.find((customer) => customer.id === selectedCustomerId)
    if (!current?.isSummary) return
    let active = true
    void fetchCustomerDetail(selectedCustomerId).then((detail) => {
      if (!active) return
      setCustomers((items) => items.map((item) => item.id === detail.id ? detail : item))
      const targetVehicleId = initialNavigationRef.current.customerId === detail.id ? initialNavigationRef.current.vehicleId : undefined
      setSelectedVehicleId(targetVehicleId && detail.vehicles.some((vehicle) => vehicle.id === targetVehicleId) ? targetVehicleId : detail.vehicles[0]?.id ?? '')
    }).catch((reason: unknown) => {
      if (active) setError(getErrorMessage(reason))
    })
    return () => { active = false }
  }, [customers, selectedCustomerId])

  useEffect(() => {
    const targetCustomerId = initialNavigationRef.current.customerId
    if (!targetCustomerId || customers.some((customer) => customer.id === targetCustomerId)) return
    let active = true
    void fetchCustomerDetail(targetCustomerId).then((detail) => {
      if (!active) return
      setCustomers((current) => [detail, ...current])
      setSelectedCustomerId(detail.id)
      setSelectedVehicleId(initialNavigationRef.current.vehicleId && detail.vehicles.some((vehicle) => vehicle.id === initialNavigationRef.current.vehicleId) ? initialNavigationRef.current.vehicleId : detail.vehicles[0]?.id ?? '')
      setMobileWorkspaceView('detail')
      onNavigationConsumedRef.current?.()
    }).catch((reason: unknown) => {
      if (active) setError(getErrorMessage(reason))
    })
    return () => { active = false }
  }, [customers])

  useEffect(() => {
    const customerId = selectedCustomer?.id
    if (!customerId) return
    let active = true
    setVehiclelessLoadingCustomerId(customerId)
    void fetchVehiclelessDocuments(customerId).then((nextDocuments) => {
      if (!active) return
      setVehiclelessDocumentsByCustomer((current) => ({ ...current, [customerId]: nextDocuments }))
    }).catch((reason: unknown) => {
      if (active) setError(getErrorMessage(reason))
    }).finally(() => {
      if (active) setVehiclelessLoadingCustomerId((current) => current === customerId ? '' : current)
    })
    return () => { active = false }
  }, [selectedCustomer?.id])

  function openMobileDetail() {
    setMobileWorkspaceView('detail')
    if (window.matchMedia('(max-width: 1169px)').matches) window.scrollTo(0, 0)
  }

  function openMobileList() {
    setMobileWorkspaceView('list')
    if (window.matchMedia('(max-width: 1169px)').matches) window.scrollTo(0, 0)
  }

  useEffect(() => { void refreshDrafts() }, [refreshDrafts])

  useEffect(() => {
    if (customerDialogOpen && !editingCustomerId) registerActiveDraft('customer-new', newCustomerStorageKey)
  }, [customerDialogOpen, editingCustomerId, newCustomerStorageKey, registerActiveDraft])

  useEffect(() => {
    if (vehicleDialogOpen && !editingVehicleId) registerActiveDraft('vehicle-new', newVehicleStorageKey)
  }, [editingVehicleId, newVehicleStorageKey, registerActiveDraft, vehicleDialogOpen])

  useEffect(() => {
    const draft = pendingRestore ?? getAutoResumeDraft('customer-new') ?? getAutoResumeDraft('vehicle-new')
    if (!draft) return
    if (draft.kind === 'customer-new') {
      setNewCustomerStorageKey(draft.key)
      setEditingCustomerId(null)
      setCustomerForm(normalizeCustomerForm(draft.value as CustomerInput))
      setCustomerDirty(true)
      customerSavedSignatureRef.current = ''
      customerUpdatedAtRef.current = null
      setCustomerDialogOpen(true)
      setError('端末内に残っていた顧客登録の入力を復元しました。')
      if (pendingRestore?.key === draft.key) acknowledgeRestore(draft.key)
      return
    }
    if (draft.kind === 'vehicle-new') {
      const customer = draft.targetId ? customers.find((item) => item.id === draft.targetId) : selectedCustomer
      if (!customer) return
      setSelectedCustomerId(customer.id)
      setNewVehicleStorageKey(draft.key)
      setEditingVehicleId(null)
      setVehicleForm(normalizeVehicleForm(draft.value as VehicleInput))
      setVehicleDirty(true)
      vehicleSavedSignatureRef.current = ''
      vehicleUpdatedAtRef.current = null
      setVehicleDialogOpen(true)
      setError('端末内に残っていた車両登録の入力を復元しました。')
      if (pendingRestore?.key === draft.key) acknowledgeRestore(draft.key)
      return
    }
    if (draft.kind === 'customer-existing' && draft.targetId) {
      const customer = customers.find((item) => item.id === draft.targetId)
      if (!customer) return
      openEditCustomerDialogRef.current(customer)
      return
    }
    if (draft.kind === 'vehicle-existing' && draft.targetId) {
      const customer = customers.find((item) => item.vehicles.some((vehicle) => vehicle.id === draft.targetId))
      const vehicle = customer?.vehicles.find((item) => item.id === draft.targetId)
      if (!customer || !vehicle) return
      setSelectedCustomerId(customer.id)
      openEditVehicleDialogRef.current(vehicle)
    }
  }, [acknowledgeRestore, customers, getAutoResumeDraft, pendingRestore, refreshDrafts, selectedCustomer])

  function selectCustomer(customer: Customer) {
    setSelectedCustomerId(customer.id)
    setSelectedVehicleId(customer.vehicles[0]?.id ?? '')
    setSelectedVehiclelessCustomerId('')
    openMobileDetail()
  }

  function selectVehicle(customer: Customer, vehicle: Vehicle) {
    setSelectedCustomerId(customer.id)
    setSelectedVehicleId(vehicle.id)
    setSelectedVehiclelessCustomerId('')
  }

  function selectVehiclelessDocuments(customer: Customer) {
    setSelectedCustomerId(customer.id)
    setSelectedVehicleId('')
    setSelectedVehiclelessCustomerId(customer.id)
    openMobileDetail()
  }

  function openNewCustomerDialog() {
    setNewCustomerStorageKey(`customer-new:${createDraftRunId()}`)
    setEditingCustomerId(null)
    setCustomerForm(emptyCustomerForm)
    setCustomerDirty(false)
    setError('')
    customerSavedSignatureRef.current = ''
    customerUpdatedAtRef.current = null
    setCustomerDialogOpen(true)
  }

  async function requestMasterDeletion(kind: 'customer' | 'vehicle', id: string, expectedUpdatedAt: string | null) {
    if (deletionLoading) return
    setDeletionLoading(true)
    setError('')
    try {
      const impact = kind === 'customer' ? await fetchCustomerDeletionImpact(id) : await fetchVehicleDeletionImpact(id)
      setPendingMasterDeletion({ impact, expectedUpdatedAt })
    } catch (reason: unknown) {
      setError(getErrorMessage(reason))
    } finally {
      setDeletionLoading(false)
    }
  }

  async function confirmMasterDeletion() {
    const pending = pendingMasterDeletion
    if (!pending || deletionLoading) return
    setDeletionLoading(true)
    setError('')
    try {
      if (pending.impact.kind === 'customer') {
        const result = await deleteCustomer(pending.impact.id, pending.expectedUpdatedAt ?? undefined)
        await customerAutosaveCancelLocalDraftRef.current(`customer-edit:${pending.impact.id}`)
        setCustomers((current) => current.filter((customer) => customer.id !== pending.impact.id))
        setVehiclelessDocumentsByCustomer((current) => {
          const next = { ...current }
          delete next[pending.impact.id]
          return next
        })
        setPendingMasterDeletion(null)
        closeCustomerDialogNow()
        setSelectedCustomerId((current) => current === pending.impact.id ? (customers.find((customer) => customer.id !== pending.impact.id)?.id ?? '') : current)
        setSelectedVehicleId('')
        setSelectedVehiclelessCustomerId('')
        if (result.customerId !== pending.impact.id) setError('顧客を削除しましたが、表示の更新に失敗しました。')
      } else {
        const result = await deleteVehicle(pending.impact.id, pending.expectedUpdatedAt ?? undefined)
        await vehicleAutosaveCancelLocalDraftRef.current(`vehicle-edit:${pending.impact.id}`)
        const detail = await fetchCustomerDetail(result.customerId)
        setCustomers((current) => current.map((customer) => customer.id === detail.id ? detail : customer))
        setSelectedCustomerId(detail.id)
        setSelectedVehicleId(detail.vehicles[0]?.id ?? '')
        setSelectedVehiclelessCustomerId('')
        setPendingMasterDeletion(null)
        closeVehicleDialogNow()
      }
      void refreshDrafts()
    } catch (reason: unknown) {
      setError(getErrorMessage(reason))
    } finally {
      setDeletionLoading(false)
    }
  }

  function openEditCustomerDialog(customer: Customer) {
    setEditingCustomerId(customer.id)
    const form = { name: customer.name, kana: customer.kana, phone: customer.phone, email: customer.email, postalCode: customer.postalCode, address: customer.address, birthDate: customer.birthDate, employer: customer.employer, memo: customer.memo }
    setCustomerForm(form)
    setCustomerDirty(false)
    setError('')
    customerSavedSignatureRef.current = formSignature(normalizeCustomerForm(form))
    customerUpdatedAtRef.current = customer.updatedAt
    setCustomerDialogOpen(true)
    void readDraft<CustomerInput>(`customer-edit:${customer.id}`).then((draft) => {
      const explicitlyRequested = pendingRestore?.key === `customer-edit:${customer.id}`
      if (!draft) {
        if (explicitlyRequested) acknowledgeRestore(`customer-edit:${customer.id}`)
        return
      }
      if (draft.savedAt <= (Date.parse(customer.updatedAt) || 0) || customerSavedSignatureRef.current === formSignature(normalizeCustomerForm(draft.value))) {
        if (explicitlyRequested) acknowledgeRestore(draft.key)
        return
      }
      if (!explicitlyRequested && draft.runId !== currentRunId) return
      setCustomerForm(draft.value)
      setCustomerDirty(true)
      setError('端末内に残っていた顧客情報の変更を復元しました。')
      if (explicitlyRequested) acknowledgeRestore(draft.key)
    }).catch(() => undefined)
  }

  openEditCustomerDialogRef.current = openEditCustomerDialog

  function closeCustomerDialogNow() {
    setCustomerDialogOpen(false)
    setEditingCustomerId(null)
    setCustomerForm(emptyCustomerForm)
    setCustomerDirty(false)
    setError('')
  }

  function closeCustomerDialog() {
    if (saving) return
    if (!editingCustomerId) {
      if (customerDirty && !window.confirm('入力内容と端末内の復元データを削除して、顧客の登録を中止しますか？')) return
      const storageKey = newCustomerStorageKey
      void customerAutosaveCancelLocalDraftRef.current(storageKey).then(async () => {
        await refreshDrafts()
        setNewCustomerStorageKey('customer-new')
        registerActiveDraft('customer-new', null)
        closeCustomerDialogNow()
      }).catch((reason: unknown) => setError(getErrorMessage(reason)))
      return
    }
    void customerAutosaveCancelLocalDraftRef.current(`customer-edit:${editingCustomerId}`).then(async () => {
      await refreshDrafts()
      closeCustomerDialogNow()
    }).catch((reason: unknown) => setError(getErrorMessage(reason)))
  }

  function updateCustomerForm(nextForm: CustomerInput) {
    setCustomerForm(nextForm)
    setCustomerDirty(true)
    setError('')
  }

  async function persistCustomerForm(id: string, form: CustomerInput): Promise<Customer> {
    const normalizedForm = normalizeCustomerForm(form)
    if (!normalizedForm.name.trim()) throw new AutosaveBlockedError('顧客名を入力してから保存してください。')
    if (customerSavedSignatureRef.current === formSignature(normalizedForm)) {
      setCustomerDirty(false)
      const current = customers.find((customer) => customer.id === id)
      if (!current) throw new Error('顧客情報を読み込めませんでした。')
      return current
    }
    setSaving(true)
    try {
      const savedCustomer = await updateCustomer(id, normalizedForm, customerUpdatedAtRef.current ?? undefined)
      setCustomers((current) => current.map((customer) => customer.id === savedCustomer.id ? savedCustomer : customer))
      customerSavedSignatureRef.current = formSignature(normalizedForm)
      customerUpdatedAtRef.current = savedCustomer.updatedAt
      setCustomerDirty(false)
      void deleteDraft(`customer-edit:${savedCustomer.id}`)
      setError('')
      return savedCustomer
    } finally {
      setSaving(false)
    }
  }

  async function handleCustomerSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (saving) return
    if (!customerForm.name.trim()) return
    const normalizedForm = normalizeCustomerForm(customerForm)
    const editingId = editingCustomerId
    if (!editingId) setSaving(true)
    setError('')
    try {
      const savedCustomer = editingId
        ? await persistCustomerForm(editingId, normalizedForm)
        : await createCustomer(normalizedForm)
      if (!savedCustomer) throw new Error('顧客情報を読み込めませんでした。')
      if (!editingId) {
        await deleteDraft(newCustomerStorageKey)
        await refreshDrafts()
        setNewCustomerStorageKey('customer-new')
        registerActiveDraft('customer-new', null)
      }
      if (!editingId) setCustomers((current) => [...current, savedCustomer])
      setSelectedCustomerId(savedCustomer.id)
      setSelectedVehicleId(savedCustomer.vehicles[0]?.id ?? '')
      openMobileDetail()
      closeCustomerDialogNow()
    } catch (reason: unknown) {
      setError(getErrorMessage(reason))
    } finally {
      if (!editingId) setSaving(false)
    }
  }

  function openNewVehicleDialog() {
    if (!selectedCustomer) return
    setNewVehicleStorageKey(`vehicle-new:${selectedCustomer.id}:${createDraftRunId()}`)
    setEditingVehicleId(null)
    setVehicleForm(emptyVehicleForm)
    setVehicleDirty(false)
    setError('')
    vehicleSavedSignatureRef.current = ''
    vehicleUpdatedAtRef.current = null
    setVehicleDialogOpen(true)
  }

  function openEditVehicleDialog(vehicle: Vehicle) {
    setEditingVehicleId(vehicle.id)
    const form = { maker: vehicle.maker, model: vehicle.model, modelType: vehicle.modelType, plate: vehicle.plate, vin: vehicle.vin, year: vehicle.year, inspectionDate: vehicle.inspectionDate, mileage: vehicle.mileage, color: vehicle.color, displacement: vehicle.displacement, transmission: vehicle.transmission, note: vehicle.note, freeItem1: vehicle.freeItem1, freeItem2: vehicle.freeItem2, freeItem3: vehicle.freeItem3 }
    setVehicleForm(form)
    setVehicleDirty(false)
    setError('')
    vehicleSavedSignatureRef.current = formSignature(normalizeVehicleForm(form))
    vehicleUpdatedAtRef.current = vehicle.updatedAt
    setVehicleDialogOpen(true)
    void readDraft<VehicleInput>(`vehicle-edit:${vehicle.id}`).then((draft) => {
      const explicitlyRequested = pendingRestore?.key === `vehicle-edit:${vehicle.id}`
      if (!draft) {
        if (explicitlyRequested) acknowledgeRestore(`vehicle-edit:${vehicle.id}`)
        return
      }
      if (draft.savedAt <= (Date.parse(vehicle.updatedAt) || 0) || vehicleSavedSignatureRef.current === formSignature(normalizeVehicleForm(draft.value))) {
        if (explicitlyRequested) acknowledgeRestore(draft.key)
        return
      }
      if (!explicitlyRequested && draft.runId !== currentRunId) return
      setVehicleForm(draft.value)
      setVehicleDirty(true)
      setError('端末内に残っていた車両情報の変更を復元しました。')
      if (explicitlyRequested) acknowledgeRestore(draft.key)
    }).catch(() => undefined)
  }

  openEditVehicleDialogRef.current = openEditVehicleDialog

  function closeVehicleDialogNow() {
    setVehicleDialogOpen(false)
    setEditingVehicleId(null)
    setVehicleForm(emptyVehicleForm)
    setVehicleDirty(false)
    setError('')
  }

  function closeVehicleDialog() {
    if (saving) return
    if (!editingVehicleId) {
      if (vehicleDirty && !window.confirm('入力内容と端末内の復元データを削除して、車両の登録を中止しますか？')) return
      const storageKey = newVehicleStorageKey
      void vehicleAutosaveCancelLocalDraftRef.current(storageKey).then(async () => {
        await refreshDrafts()
        setNewVehicleStorageKey('vehicle-new')
        registerActiveDraft('vehicle-new', null)
        closeVehicleDialogNow()
      }).catch((reason: unknown) => setError(getErrorMessage(reason)))
      return
    }
    void vehicleAutosaveCancelLocalDraftRef.current(`vehicle-edit:${editingVehicleId}`).then(async () => {
      await refreshDrafts()
      closeVehicleDialogNow()
    }).catch((reason: unknown) => setError(getErrorMessage(reason)))
  }

  function updateVehicleForm(nextForm: VehicleInput) {
    setVehicleForm(nextForm)
    setVehicleDirty(true)
    setError('')
  }

  async function persistVehicleForm(id: string, form: VehicleInput): Promise<Customer> {
    const normalizedForm = normalizeVehicleForm(form)
    if (!normalizedForm.maker.trim() || !normalizedForm.model.trim()) throw new AutosaveBlockedError('メーカーと車名を入力してから保存してください。')
    if (vehicleSavedSignatureRef.current === formSignature(normalizedForm)) {
      setVehicleDirty(false)
      const current = customers.find((customer) => customer.vehicles.some((vehicle) => vehicle.id === id))
      if (!current) throw new Error('車両情報を読み込めませんでした。')
      return current
    }
    setSaving(true)
    try {
      const result = await updateVehicle(id, normalizedForm, vehicleUpdatedAtRef.current ?? undefined)
      if (result.customer) setCustomers((current) => current.map((customer) => customer.id === result.customer?.id ? result.customer : customer))
      vehicleSavedSignatureRef.current = formSignature(normalizedForm)
      vehicleUpdatedAtRef.current = result.customer.vehicles.find((vehicle) => vehicle.id === id)?.updatedAt ?? vehicleUpdatedAtRef.current
      setVehicleDirty(false)
      void deleteDraft(`vehicle-edit:${id}`)
      setError('')
      return result.customer
    } finally {
      setSaving(false)
    }
  }

  async function handleVehicleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (saving) return
    if (!selectedCustomer || !vehicleForm.maker.trim() || !vehicleForm.model.trim()) return
    const normalizedForm = normalizeVehicleForm(vehicleForm)
    const editingId = editingVehicleId
    if (!editingId) setSaving(true)
    setError('')
    try {
      if (editingId) {
        await persistVehicleForm(editingId, normalizedForm)
        setSelectedVehicleId(editingId)
      } else {
        const result = await createVehicle(selectedCustomer.id, normalizedForm)
        await deleteDraft(newVehicleStorageKey)
        await refreshDrafts()
        setNewVehicleStorageKey('vehicle-new')
        registerActiveDraft('vehicle-new', null)
        setCustomers((current) => current.map((customer) => customer.id === result.customer.id ? result.customer : customer))
        setSelectedCustomerId(result.customer.id)
        setSelectedVehicleId(result.vehicleId)
      }
      openMobileDetail()
      closeVehicleDialogNow()
    } catch (reason: unknown) {
      setError(getErrorMessage(reason))
    } finally {
      if (!editingId) setSaving(false)
    }
  }

  async function handleAttachmentFiles(files: File[], vehicleId: string) {
    if (!files.length) return
    setSaving(true)
    setError('')
    try {
      for (const file of files) {
        const attachment = await uploadVehicleFile(vehicleId, file)
        setCustomers((current) => current.map((customer) => ({ ...customer, vehicles: customer.vehicles.map((vehicle) => vehicle.id === vehicleId ? { ...vehicle, attachments: [...vehicle.attachments, attachment] } : vehicle) })))
      }
    } catch (reason: unknown) {
      setError(getErrorMessage(reason))
    } finally {
      setSaving(false)
    }
  }

  function handleAttachments(event: ChangeEvent<HTMLInputElement>, vehicleId: string) {
    const files = Array.from(event.target.files ?? [])
    event.target.value = ''
    void handleAttachmentFiles(files, vehicleId)
  }

  function handleAttachmentDrop(event: DragEvent<HTMLLabelElement>, vehicleId: string) {
    event.preventDefault()
    event.currentTarget.classList.remove('is-dragging')
    void handleAttachmentFiles(Array.from(event.dataTransfer.files), vehicleId)
  }

  async function removeAttachment(vehicleId: string, attachmentId: string) {
    setSaving(true)
    setError('')
    try {
      await deleteVehicleFile(vehicleId, attachmentId)
      setCustomers((current) => current.map((customer) => ({ ...customer, vehicles: customer.vehicles.map((vehicle) => vehicle.id === vehicleId ? { ...vehicle, attachments: vehicle.attachments.filter((attachment) => attachment.id !== attachmentId) } : vehicle) })))
    } catch (reason: unknown) {
      setError(getErrorMessage(reason))
    } finally {
      setSaving(false)
    }
  }

  async function openAttachment(vehicleId: string, attachment: Attachment, mode: 'preview' | 'download') {
    setSaving(true)
    setError('')
    try {
      const blob = await fetchVehicleFile(vehicleId, attachment.id)
      const url = URL.createObjectURL(blob)
      if (mode === 'preview') {
        setAttachmentPreview({ vehicleId, attachment, url })
      } else {
        const link = document.createElement('a')
        link.href = url
        link.download = attachment.name
        document.body.appendChild(link)
        link.click()
        link.remove()
        window.setTimeout(() => URL.revokeObjectURL(url), 60_000)
      }
    } catch (reason: unknown) {
      setError(getErrorMessage(reason))
    } finally {
      setSaving(false)
    }
  }

  function closeAttachmentPreview() {
    setAttachmentPreview(null)
  }

  const customerAutosave = useAutosave<CustomerInput>({
    value: customerForm,
    dirty: customerDirty,
    enabled: customerDialogOpen,
    serverEnabled: Boolean(customerDialogOpen && editingCustomerId && customerDirty && !saving),
    registrationKey: `customer:${editingCustomerId ?? 'new'}`,
    storageKey: editingCustomerId ? `customer-edit:${editingCustomerId}` : newCustomerStorageKey,
    save: async (snapshot) => {
      if (!editingCustomerId) throw new AutosaveBlockedError()
      await persistCustomerForm(editingCustomerId, snapshot)
      return true
    },
    onError: (reason) => setError(getErrorMessage(reason)),
    onBlocked: (reason) => setError(reason.message),
  })
  customerAutosaveCancelLocalDraftRef.current = customerAutosave.cancelLocalDraft

  const vehicleAutosave = useAutosave<VehicleInput>({
    value: vehicleForm,
    dirty: vehicleDirty,
    enabled: vehicleDialogOpen,
    serverEnabled: Boolean(vehicleDialogOpen && editingVehicleId && vehicleDirty && !saving),
    registrationKey: `vehicle:${editingVehicleId ?? 'new'}`,
    storageKey: editingVehicleId ? `vehicle-edit:${editingVehicleId}` : newVehicleStorageKey,
    save: async (snapshot) => {
      if (!editingVehicleId) throw new AutosaveBlockedError()
      await persistVehicleForm(editingVehicleId, snapshot)
      return true
    },
    onError: (reason) => setError(getErrorMessage(reason)),
    onBlocked: (reason) => setError(reason.message),
  })
  vehicleAutosaveCancelLocalDraftRef.current = vehicleAutosave.cancelLocalDraft

  return (
    <>
      <div className="page-header customer-page-header">
        <div><span className="page-eyebrow">顧客・車両</span><h1>顧客・車両</h1><p>顧客情報と、顧客に紐づく複数の車両を管理します。</p></div>
        <button className="button button-primary" type="button" onClick={openNewCustomerDialog}><Plus size={18} />顧客を登録</button>
      </div>

      {(loading || error || saving) && ((!customerDialogOpen && !vehicleDialogOpen) || pendingMasterDeletion) && <div className={`customer-sync-status${error ? ' is-error' : ''}`} role={error ? 'alert' : 'status'}><span>{loading ? '顧客・車両データを読み込んでいます…' : saving ? '変更を保存しています…' : error}</span>{error && <button className="text-button" type="button" onClick={() => window.location.reload()}>再読み込み</button>}</div>}

      <div className="customer-toolbar">
        <label className="customer-search"><Search size={19} /><span className="sr-only">顧客・車両を検索</span><input value={query} onChange={(event) => setQuery(event.target.value)} placeholder={customerSearchPlaceholders[searchField]} /></label>
        <label className="customer-search-filter"><span className="sr-only">検索項目</span><select value={searchField} onChange={(event) => setSearchField(event.target.value as CustomerSearchField)}>{customerSearchFields.map((field) => <option key={field} value={field}>{field}</option>)}</select></label>
      </div>

      <div className={`customer-directory mobile-workspace mobile-workspace-${mobileWorkspaceView}`}>
        <div className="mobile-workspace-list"><CustomerList customers={filteredCustomers} selectedCustomerId={selectedCustomer?.id ?? ''} onSelect={selectCustomer} hasMore={customerHasMore} loadingMore={loadingMoreCustomers} onLoadMore={() => void loadMoreCustomers()} /></div>
        <div className="mobile-workspace-detail">
          <button className="mobile-workspace-back" type="button" onClick={openMobileList}><ChevronLeft size={16} />顧客一覧へ戻る</button>
          <CustomerProfile customer={selectedCustomer} vehicle={selectedVehicle} vehiclelessDocuments={selectedVehiclelessDocuments} vehiclelessLoading={vehiclelessLoading} vehiclelessSelected={selectedVehiclelessCustomerId === selectedCustomer?.id} onSelectVehicle={(vehicle) => selectedCustomer && selectVehicle(selectedCustomer, vehicle)} onSelectVehiclelessDocuments={() => selectedCustomer && selectVehiclelessDocuments(selectedCustomer)} onAddVehicle={openNewVehicleDialog} onEditCustomer={openEditCustomerDialog} onEditVehicle={openEditVehicleDialog} onAttachments={handleAttachments} onAttachmentDrop={handleAttachmentDrop} onPreviewAttachment={openAttachment} onRemoveAttachment={removeAttachment} onNavigate={onNavigate} />
        </div>
      </div>

      {customerDialogOpen && <CustomerDialog form={customerForm} title={editingCustomerId ? '顧客情報を編集' : '顧客を登録'} submitLabel={editingCustomerId ? '変更を保存' : '顧客を登録'} cancelLabel={editingCustomerId ? '閉じる' : undefined} autosaveStatus={customerAutosave.status} autosaveLastSavedAt={customerAutosave.lastSavedAt} saving={saving} deleteLoading={deletionLoading} error={error} onChange={updateCustomerForm} onClose={closeCustomerDialog} onSubmit={handleCustomerSubmit} onDelete={editingCustomerId ? () => void requestMasterDeletion('customer', editingCustomerId, customerUpdatedAtRef.current) : undefined} />}
      {vehicleDialogOpen && selectedCustomer && <VehicleDialog form={vehicleForm} title={editingVehicleId ? '車両情報を編集' : '車両を追加'} submitLabel={editingVehicleId ? '変更を保存' : '車両を追加'} cancelLabel={editingVehicleId ? '閉じる' : undefined} autosaveStatus={vehicleAutosave.status} autosaveLastSavedAt={vehicleAutosave.lastSavedAt} saving={saving} deleteLoading={deletionLoading} error={error} customerName={selectedCustomer.name} onChange={updateVehicleForm} onClose={closeVehicleDialog} onSubmit={handleVehicleSubmit} onDelete={editingVehicleId ? () => void requestMasterDeletion('vehicle', editingVehicleId, vehicleUpdatedAtRef.current) : undefined} />}
      {pendingMasterDeletion && <MasterDeletionDialog impact={pendingMasterDeletion.impact} loading={deletionLoading} onClose={() => setPendingMasterDeletion(null)} onConfirm={() => void confirmMasterDeletion()} />}
      {attachmentPreview && <AttachmentPreviewModal preview={attachmentPreview} onClose={closeAttachmentPreview} />}
    </>
  )
}

function CustomerList({ customers, selectedCustomerId, onSelect, hasMore, loadingMore, onLoadMore }: { customers: Customer[]; selectedCustomerId: string; onSelect: (customer: Customer) => void; hasMore: boolean; loadingMore: boolean; onLoadMore: () => void }) {
  return <section className="panel customer-list-panel"><div className="customer-list-header"><div><h2>顧客一覧</h2><span>顧客を選択すると詳細を表示します</span></div></div><div className="customer-list">{customers.map((customer) => <button className={`customer-list-item${customer.id === selectedCustomerId ? ' is-selected' : ''}`} key={customer.id} type="button" onClick={() => onSelect(customer)}><span className="customer-list-avatar"><UserRound size={19} /></span><span className="customer-list-copy"><strong>{customer.name}</strong><small>{customer.phone || '電話番号未登録'}</small></span><ChevronRight size={17} className="customer-list-chevron" /></button>)}{hasMore && <button className="button button-secondary customer-list-load-more" type="button" onClick={onLoadMore} disabled={loadingMore}>{loadingMore ? '読み込み中…' : '次の顧客を読み込む'}</button>}{!customers.length && <div className="empty-state"><Search size={24} /><strong>顧客が見つかりません</strong><span>顧客を登録するか、検索条件を変更してください。</span></div>}</div></section>
}

function CustomerProfile({ customer, vehicle, vehiclelessDocuments, vehiclelessLoading, vehiclelessSelected, onSelectVehicle, onSelectVehiclelessDocuments, onAddVehicle, onEditCustomer, onEditVehicle, onAttachments, onAttachmentDrop, onPreviewAttachment, onRemoveAttachment, onNavigate }: { customer: Customer | null; vehicle: Vehicle | null; vehiclelessDocuments: VehiclelessDocuments | null; vehiclelessLoading: boolean; vehiclelessSelected: boolean; onSelectVehicle: (vehicle: Vehicle) => void; onSelectVehiclelessDocuments: () => void; onAddVehicle: () => void; onEditCustomer: (customer: Customer) => void; onEditVehicle: (vehicle: Vehicle) => void; onAttachments: (event: ChangeEvent<HTMLInputElement>, vehicleId: string) => void; onAttachmentDrop: (event: DragEvent<HTMLLabelElement>, vehicleId: string) => void; onPreviewAttachment: (vehicleId: string, attachment: Attachment, mode: 'preview' | 'download') => void; onRemoveAttachment: (vehicleId: string, attachmentId: string) => void; onNavigate?: (target: VehicleHistoryNavigation) => void }) {
  if (!customer) return <section className="panel customer-profile-empty"><UserRound size={30} /><strong>顧客を登録してください</strong><span>登録した顧客の情報がここに表示されます。</span></section>

  const hasVehiclelessDocuments = Boolean(vehiclelessDocuments?.documents.length)
  const showVehiclelessDocuments = vehiclelessSelected && vehiclelessDocuments !== null

  return <section className="customer-profile"><section className="panel customer-info-panel"><div className="customer-profile-header"><div className="customer-identity"><span className="customer-profile-avatar"><UserRound size={28} /></span><span><h2>{customer.name}</h2><small>{customer.kana || 'ふりがな未登録'}</small></span></div><button className="button button-secondary" type="button" onClick={() => onEditCustomer(customer)}><Pencil size={17} />顧客情報を編集</button></div><div className="customer-info-grid"><InfoItem icon={Phone} label="電話番号" value={customer.phone || '未登録'} /><InfoItem icon={Mail} label="メールアドレス" value={customer.email || '未登録'} /><InfoItem icon={CalendarDays} label="生年月日" value={customer.birthDate || '未登録'} /><InfoItem icon={BriefcaseBusiness} label="勤務先等" value={customer.employer || '未登録'} /><InfoItem icon={MapPin} label="住所" value={customer.address || '未登録'} /></div>{customer.memo && <div className="customer-memo"><span>メモ</span><p>{customer.memo}</p></div>}</section><section className="owned-vehicles-section"><div className="owned-vehicles-header"><div><h2>所有車両</h2><span>車両を選択すると詳細と添付ファイルが切り替わります</span>{vehiclelessLoading && <small className="vehicleless-documents-loading">車両情報のない書類を確認中…</small>}</div><button className="button button-primary" type="button" onClick={onAddVehicle}><Plus size={17} />車両を追加</button></div>{customer.vehicles.length || hasVehiclelessDocuments ? <div className="vehicle-choice-grid">{customer.vehicles.map((item) => <button className={`vehicle-choice-card${!vehiclelessSelected && item.id === vehicle?.id ? ' is-selected' : ''}`} key={item.id} type="button" onClick={() => onSelectVehicle(item)}><span className="vehicle-choice-name"><span className={`vehicle-status-dot ${vehicleInspectionTone(item.inspectionDate)}`} /><strong>{item.maker} {item.model}</strong></span><span className="vehicle-choice-plate">{item.plate || '登録番号未登録'}</span><span className="vehicle-choice-footer"><span>{item.year || '年式未登録'}</span><span>{item.attachments.length}件の添付</span></span></button>)}{hasVehiclelessDocuments && vehiclelessDocuments && <button className={`vehicle-choice-card vehicleless-choice-card${vehiclelessSelected ? ' is-selected' : ''}`} type="button" onClick={onSelectVehiclelessDocuments}><span className="vehicle-choice-name"><FileText size={15} aria-hidden="true" /><strong>車両情報のない書類</strong></span><span className="vehicle-choice-plate">販売 {vehiclelessDocuments.salesCount}件 ・ 整備 {vehiclelessDocuments.maintenanceCount}件</span><span className="vehicle-choice-footer"><span>ABACUS互換</span><span>車両：なし</span></span></button>}</div> : <div className="owned-vehicles-empty"><CarFront size={23} /><strong>所有車両が登録されていません</strong><span>この顧客に最初の車両を追加してください。</span><button className="button button-primary" type="button" onClick={onAddVehicle}><Plus size={17} />車両を追加</button></div>}</section>{showVehiclelessDocuments ? <VehiclelessDocumentPanel documents={vehiclelessDocuments.documents} onNavigate={onNavigate} /> : vehicle && <><div className="selected-vehicle-grid"><VehicleSummary vehicle={vehicle} onEdit={onEditVehicle} /><section className="panel attachments-panel"><AttachmentSection vehicle={vehicle} onAttachments={onAttachments} onAttachmentDrop={onAttachmentDrop} onPreviewAttachment={onPreviewAttachment} onRemoveAttachment={onRemoveAttachment} /></section></div><VehicleHistoryPanel vehicleId={vehicle.id} onNavigate={onNavigate} /></>}</section>
}

function VehiclelessDocumentPanel({ documents, onNavigate }: { documents: VehiclelessDocuments['documents']; onNavigate?: (target: VehicleHistoryNavigation) => void }) {
  return <section className="panel vehicleless-documents-panel"><div className="vehicle-history-header"><div><span className="page-eyebrow">ABACUS COMPATIBILITY</span><h3>車両情報のない書類</h3><p>顧客にのみ紐づくABACUS移行書類です。車両履歴や車両添付には含まれません。</p></div><FileText size={20} /></div><div className="vehicleless-documents-list">{documents.map((document) => { const kindLabel = document.kind === 'sales' ? '販売' : '整備'; const categoryLabel = document.kind === 'maintenance' && document.category ? ` ・ ${document.category}` : ''; return <div className="vehicleless-document-row" key={`${document.kind}-${document.id}`}><span className="vehicleless-document-kind">{kindLabel}{categoryLabel}</span><HistoryRow primary={`${document.type} ${document.number}`} secondary={`${formatHistoryDate(document.issuedAt)} ・ ${document.status} ・ ${document.sourceLocation}`} onClick={onNavigate ? () => onNavigate({ section: document.kind, recordId: document.id }) : undefined} /><strong className="vehicleless-document-amount">{formatYen(document.total)}</strong></div> })}</div></section>
}

function InfoItem({ icon: Icon, label, value }: { icon: LucideIcon; label: string; value: string }) {
  return <div className="customer-info-item"><span className="customer-info-label"><Icon size={16} />{label}</span><strong>{value}</strong></div>
}

function VehicleSummary({ vehicle, onEdit }: { vehicle: Vehicle; onEdit: (vehicle: Vehicle) => void }) {
  return <section className="panel vehicle-summary-panel"><div className="vehicle-summary-header"><div><span>選択中の車両</span><h2>{vehicle.maker} {vehicle.model}</h2><small>{vehicle.plate || '登録番号未登録'}</small></div><button className="detail-action-button" type="button" onClick={() => onEdit(vehicle)}><Pencil size={15} />編集</button></div><div className="vehicle-summary-body"><div className="detail-fields"><DetailField label="車検満了日" value={vehicle.inspectionDate || '未登録'} /><DetailField label="車台番号" value={vehicle.vin || '未登録'} /><DetailField label="型式・年式" value={[vehicle.modelType, vehicle.year].filter(Boolean).join(' ・ ') || '未登録'} /><DetailField label="車体色" value={vehicle.color || '未登録'} /><DetailField label="走行距離" value={vehicle.mileage || '未登録'} /><DetailField label="排気量" value={vehicle.displacement || '未登録'} /><DetailField label="ミッション" value={vehicle.transmission || '未登録'} /></div>{[vehicle.freeItem1, vehicle.freeItem2, vehicle.freeItem3].some(Boolean) && <div className="vehicle-free-items">{[vehicle.freeItem1, vehicle.freeItem2, vehicle.freeItem3].filter(Boolean).map((item, index) => <DetailField key={`${item}-${index}`} label={`自由項目${index + 1}`} value={item} />)}</div>}{vehicle.note && <div className="vehicle-note"><span>備考</span><p>{vehicle.note}</p></div>}</div></section>
}

function VehicleHistoryPanel({ vehicleId, onNavigate }: { vehicleId: string; onNavigate?: (target: VehicleHistoryNavigation) => void }) {
  const [history, setHistory] = useState<VehicleHistory | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')

  useEffect(() => {
    let active = true
    setLoading(true)
    setError('')
    void fetchVehicleHistory(vehicleId).then((nextHistory) => {
      if (active) setHistory(nextHistory)
    }).catch((reason: unknown) => {
      if (active) setError(reason instanceof Error ? reason.message : '車両履歴を読み込めませんでした。')
    }).finally(() => {
      if (active) setLoading(false)
    })
    return () => { active = false }
  }, [vehicleId])

  const timelineRows = useMemo(() => {
    if (!history) return []
    const salesRows: TimelineRow[] = history.sales.map(s => ({
      documentType: 'sale' as const,
      category: null,
      date: s.issuedAt,
      mileage: null,
      documentId: s.id,
      documentNumber: s.number,
      documentTypeLabel: s.type,
      status: s.status,
      total: s.total,
      section: 'sales' as const,
    }))
    const maintenanceRows: TimelineRow[] = history.maintenance.map(m => ({
      documentType: 'maintenance' as const,
      category: m.category,
      date: m.issuedAt,
      mileage: m.recordedMileage,
      documentId: m.id,
      documentNumber: m.number,
      documentTypeLabel: m.type,
      status: m.status,
      total: m.total,
      section: 'maintenance' as const,
    }))
    return [...salesRows, ...maintenanceRows]
      .sort((a, b) => {
        const dateCompare = a.date.localeCompare(b.date)
        if (dateCompare !== 0) return dateCompare
        // 同日付の場合、販売（sale）を優先して上に表示
        const typePriority = (docType: 'sale' | 'maintenance') => docType === 'sale' ? 0 : 1
        const typeCompare = typePriority(a.documentType) - typePriority(b.documentType)
        if (typeCompare !== 0) return typeCompare
        const numberCompare = a.documentNumber.localeCompare(b.documentNumber, 'ja-JP', { numeric: true })
        if (numberCompare !== 0) return numberCompare
        return a.documentId.localeCompare(b.documentId)
      })
  }, [history])

  const panelContent = loading
    ? <div className="vehicle-history-empty">履歴を読み込んでいます…</div>
    : error
      ? <div className="vehicle-history-empty is-error" role="alert">{error}</div>
      : timelineRows.length === 0
        ? <div className="vehicle-history-empty">履歴はありません</div>
        : <div className="vehicle-timeline-table"><div className="vehicle-timeline-header"><div className="vehicle-timeline-cell vehicle-timeline-date">日付</div><div className="vehicle-timeline-cell vehicle-timeline-category">種別</div><div className="vehicle-timeline-cell vehicle-timeline-mileage">走行距離</div><div className="vehicle-timeline-cell vehicle-timeline-amount">金額</div><div className="vehicle-timeline-cell vehicle-timeline-document">書類</div></div>{timelineRows.map((row) => <TimelineRowComponent key={`${row.documentType}-${row.documentId}`} row={row} onNavigate={onNavigate} />)}</div>

  return <section className="panel vehicle-history-panel"><div className="vehicle-history-header"><div><span className="page-eyebrow">VEHICLE HISTORY</span><h3>車両履歴</h3><p>販売・整備の書類を時系列で確認できます。</p></div><FileText size={20} /></div>{panelContent}</section>
}

type TimelineRow = {
  documentType: 'sale' | 'maintenance'
  category: string | null
  date: string
  mileage: number | null
  documentId: string
  documentNumber: string
  documentTypeLabel: string
  status: string
  total: number
  section: 'sales' | 'maintenance'
}

function TimelineRowComponent({ row, onNavigate }: { row: TimelineRow; onNavigate?: (target: VehicleHistoryNavigation) => void }) {
  const categoryLabel = row.documentType === 'sale' ? '販売' : (row.category ?? '—')
  const mileageLabel = row.mileage !== null ? `${row.mileage.toLocaleString('ja-JP')} km` : '—'
  return <div className="vehicle-timeline-row"><div className="vehicle-timeline-cell vehicle-timeline-date">{formatHistoryDate(row.date)}</div><div className="vehicle-timeline-cell vehicle-timeline-category">{categoryLabel}</div><div className="vehicle-timeline-cell vehicle-timeline-mileage">{mileageLabel}</div><div className="vehicle-timeline-cell vehicle-timeline-amount">{formatYen(row.total)}</div><div className="vehicle-timeline-cell vehicle-timeline-document"><HistoryRow primary={`${row.documentTypeLabel} ${row.documentNumber}`} secondary={`${formatHistoryDate(row.date)} ・ ${row.status}`} onClick={onNavigate ? () => onNavigate({ section: row.section, recordId: row.documentId }) : undefined} /></div></div>
}

function HistoryRow({ primary, secondary, onClick }: { primary: string; secondary: string; onClick?: () => void }) {
  const label = <span className="vehicle-history-row-label"><strong>{primary}</strong><small>{secondary}</small></span>
  return onClick ? <button className="vehicle-history-row-action" type="button" onClick={onClick} aria-label={`${primary}を開く`}>{label}</button> : label
}

function formatYen(amount: number) { return `¥${new Intl.NumberFormat('ja-JP').format(Math.round(amount))}` }
function formatHistoryDate(value: string | null) { return value ? value.slice(0, 10).replaceAll('-', '/') : '日付未登録' }

function vehicleInspectionTone(value: string) {
  const normalized = value.trim().slice(0, 10).replaceAll('/', '-')
  if (!/^\d{4}-\d{2}-\d{2}$/.test(normalized)) return ''
  const dueDate = new Date(`${normalized}T00:00:00`)
  const today = new Date()
  const todayDate = new Date(today.getFullYear(), today.getMonth(), today.getDate())
  const diff = Math.ceil((dueDate.getTime() - todayDate.getTime()) / 86_400_000)
  return diff < 0 ? 'is-danger' : diff <= 30 ? 'is-warning' : ''
}

function DetailField({ label, value }: { label: string; value: string }) {
  return <div className="detail-field"><span>{label}</span><strong>{value}</strong></div>
}

function AttachmentSection({ vehicle, onAttachments, onAttachmentDrop, onPreviewAttachment, onRemoveAttachment }: { vehicle: Vehicle; onAttachments: (event: ChangeEvent<HTMLInputElement>, vehicleId: string) => void; onAttachmentDrop: (event: DragEvent<HTMLLabelElement>, vehicleId: string) => void; onPreviewAttachment: (vehicleId: string, attachment: Attachment, mode: 'preview' | 'download') => void; onRemoveAttachment: (vehicleId: string, attachmentId: string) => void }) {
  return (
    <section className="attachments-section">
      <div className="attachments-header">
        <div><h3>添付ファイル</h3><span>写真・車検証PDFなどを車両ごとに保存</span></div>
        <label className="attachment-add-button"><Plus size={16} />追加<input className="hidden-input" type="file" accept=".jpg,.jpeg,.png,.pdf,image/jpeg,image/png,application/pdf" multiple onChange={(event) => onAttachments(event, vehicle.id)} /></label>
      </div>
      {vehicle.attachments.length > 0 && <div className="attachments-grid">{vehicle.attachments.map((attachment) => <div className="attachment-card" key={attachment.id}><span className={`attachment-icon attachment-icon-${attachment.type}`}>{attachment.type === 'image' ? <ImageIcon size={19} /> : <FileText size={19} />}</span><span className="attachment-card-copy"><strong title={attachment.name}>{attachment.name}</strong><small>{formatFileSize(attachment.size)} ・ {attachment.createdAt}</small></span><span className="attachment-actions"><button className="attachment-action" type="button" aria-label={`${attachment.name}をプレビュー`} title="プレビュー" onClick={() => onPreviewAttachment(vehicle.id, attachment, 'preview')}><Eye size={15} /></button><button className="attachment-action" type="button" aria-label={`${attachment.name}をダウンロード`} title="ダウンロード" onClick={() => onPreviewAttachment(vehicle.id, attachment, 'download')}><Download size={15} /></button><button className="attachment-remove" type="button" aria-label={`${attachment.name}を削除`} onClick={() => onRemoveAttachment(vehicle.id, attachment.id)}><Trash2 size={15} /></button></span></div>)}</div>}
      <label className="attachment-dropzone" onDragEnter={(event) => { event.preventDefault(); event.currentTarget.classList.add('is-dragging') }} onDragOver={(event) => { event.preventDefault(); event.currentTarget.classList.add('is-dragging') }} onDragLeave={(event) => event.currentTarget.classList.remove('is-dragging')} onDrop={(event) => onAttachmentDrop(event, vehicle.id)}>
        <Paperclip size={21} />
        <strong>ファイルをドロップ</strong>
        <span>ここにドラッグ＆ドロップ、またはクリックして選択（JPEG・PNG・PDF）</span>
        <input className="hidden-input" type="file" accept=".jpg,.jpeg,.png,.pdf,image/jpeg,image/png,application/pdf" multiple onChange={(event) => onAttachments(event, vehicle.id)} />
      </label>
    </section>
  )
}

function AttachmentPreviewModal({ preview, onClose }: { preview: AttachmentPreview; onClose: () => void }) {
  const [ocrStatus, setOcrStatus] = useState<OcrStatus>('idle')
  const [ocrProgress, setOcrProgress] = useState(0)
  const [ocrError, setOcrError] = useState('')
  const [ocrRegions, setOcrRegions] = useState<OcrTextRegion[]>([])
  const [imageSize, setImageSize] = useState<OcrImageSize | null>(null)
  const [selectedOcrRegionIndexes, setSelectedOcrRegionIndexes] = useState<Set<number>>(() => new Set())
  const imageRef = useRef<HTMLImageElement | null>(null)
  const ocrLayerRef = useRef<HTMLDivElement | null>(null)
  const ocrPointerSelectionRef = useRef<OcrPointerSelection | null>(null)

  useEffect(() => {
    setOcrStatus('idle')
    setOcrProgress(0)
    setOcrError('')
    setOcrRegions([])
    setImageSize(null)
    setSelectedOcrRegionIndexes(new Set())
  }, [preview.url])

  useEffect(() => {
    const layer = ocrLayerRef.current
    if (!layer || ocrRegions.length === 0) return

    const updateSelectionHighlight = () => {
      const selection = window.getSelection()
      const hasLayerEndpoint = Boolean(selection?.anchorNode && layer.contains(selection.anchorNode)) || Boolean(selection?.focusNode && layer.contains(selection.focusNode))
      if (!selection || selection.rangeCount === 0 || selection.isCollapsed || !hasLayerEndpoint) {
        setSelectedOcrRegionIndexes((current) => current.size === 0 ? current : new Set())
        return
      }

      const range = selection.getRangeAt(0)
      const nextIndexes = new Set<number>()
      layer.querySelectorAll<HTMLElement>('[data-ocr-region-index]').forEach((region) => {
        if (range.intersectsNode(region)) {
          const index = Number(region.dataset.ocrRegionIndex)
          if (Number.isInteger(index)) nextIndexes.add(index)
        }
      })
      setSelectedOcrRegionIndexes((current) => {
        if (current.size === nextIndexes.size && [...current].every((index) => nextIndexes.has(index))) return current
        return nextIndexes
      })
    }

    document.addEventListener('selectionchange', updateSelectionHighlight)
    return () => document.removeEventListener('selectionchange', updateSelectionHighlight)
  }, [ocrRegions.length, preview.url])

  function getOcrRegionIndexAtPoint(clientX: number, clientY: number) {
    const layer = ocrLayerRef.current
    const target = document.elementFromPoint(clientX, clientY)
    const region = target?.closest<HTMLElement>('[data-ocr-region-index]')
    if (!layer || !region || !layer.contains(region)) return null
    const index = Number(region.dataset.ocrRegionIndex)
    return Number.isInteger(index) ? index : null
  }

  function setOcrSelectionRange(anchorIndex: number, focusIndex: number) {
    const layer = ocrLayerRef.current
    if (!layer) return
    const regions = Array.from(layer.querySelectorAll<HTMLElement>('[data-ocr-region-index]'))
    const regionByIndex = new Map(regions.map((region) => [Number(region.dataset.ocrRegionIndex), region]))
    const startIndex = Math.min(anchorIndex, focusIndex)
    const endIndex = Math.max(anchorIndex, focusIndex)
    const startRegion = regionByIndex.get(startIndex)
    const endRegion = regionByIndex.get(endIndex)
    const startText = startRegion?.firstChild
    const endText = endRegion?.firstChild
    if (!startText || !endText || startText.nodeType !== Node.TEXT_NODE || endText.nodeType !== Node.TEXT_NODE) return

    const range = document.createRange()
    range.setStart(startText, 0)
    range.setEnd(endText, endText.textContent?.length ?? 0)
    const selection = window.getSelection()
    if (!selection) return
    selection.removeAllRanges()
    selection.addRange(range)
    setSelectedOcrRegionIndexes(new Set(Array.from({ length: endIndex - startIndex + 1 }, (_, index) => startIndex + index)))
  }

  function handleOcrPointerDown(event: ReactPointerEvent<HTMLDivElement>) {
    if (!event.isPrimary || (event.pointerType === 'mouse' && event.button !== 0)) return
    const anchorIndex = getOcrRegionIndexAtPoint(event.clientX, event.clientY)
    if (anchorIndex === null) return
    event.preventDefault()
    event.currentTarget.setPointerCapture(event.pointerId)
    ocrPointerSelectionRef.current = { pointerId: event.pointerId, anchorIndex, focusIndex: anchorIndex }
    setOcrSelectionRange(anchorIndex, anchorIndex)
  }

  function handleOcrPointerMove(event: ReactPointerEvent<HTMLDivElement>) {
    const pointerSelection = ocrPointerSelectionRef.current
    if (!pointerSelection || pointerSelection.pointerId !== event.pointerId) return
    event.preventDefault()
    const focusIndex = getOcrRegionIndexAtPoint(event.clientX, event.clientY)
    if (focusIndex === null || focusIndex === pointerSelection.focusIndex) return
    pointerSelection.focusIndex = focusIndex
    setOcrSelectionRange(pointerSelection.anchorIndex, focusIndex)
  }

  function endOcrPointerSelection(event: ReactPointerEvent<HTMLDivElement>) {
    const pointerSelection = ocrPointerSelectionRef.current
    if (!pointerSelection || pointerSelection.pointerId !== event.pointerId) return
    if (event.currentTarget.hasPointerCapture(event.pointerId)) event.currentTarget.releasePointerCapture(event.pointerId)
    ocrPointerSelectionRef.current = null
  }

  useEffect(() => {
    const image = imageRef.current
    if (!image) return

    const updateImageSize = () => {
      if (!image.naturalWidth || !image.naturalHeight) return
      const { width: renderedWidth, height: renderedHeight } = image.getBoundingClientRect()
      if (!renderedWidth || !renderedHeight) return
      setImageSize({ width: image.naturalWidth, height: image.naturalHeight, renderedWidth, renderedHeight })
    }

    updateImageSize()
    const observer = new ResizeObserver(updateImageSize)
    observer.observe(image)
    return () => observer.disconnect()
  }, [preview.url])

  async function recognizeText() {
    if (preview.attachment.type !== 'image' || ocrStatus === 'running') return
    setOcrStatus('running')
    setOcrProgress(0)
    setOcrError('')
    let worker: Awaited<ReturnType<(typeof import('tesseract.js'))['createWorker']>> | null = null
    try {
      const { createWorker } = await import('tesseract.js')
      worker = await createWorker('jpn+eng', undefined, { logger: ({ progress }) => setOcrProgress(Math.round(progress * 100)) })
      const result = await worker.recognize(preview.url, {}, { blocks: true })
      const regions = (result.data.blocks ?? []).flatMap((block) => (block.paragraphs ?? []).flatMap((paragraph) => paragraph.lines ?? [])).flatMap((line) => {
        const symbols = (line.words ?? []).flatMap((word) => word.symbols ?? []).filter((symbol) => symbol.text.trim())
        return symbols.length > 0 ? symbols.map((symbol) => ({ text: symbol.text.trim(), x0: symbol.bbox.x0, y0: symbol.bbox.y0, x1: symbol.bbox.x1, y1: symbol.bbox.y1, confidence: symbol.confidence })) : line.text.trim() ? [{ text: line.text.trim(), x0: line.bbox.x0, y0: line.bbox.y0, x1: line.bbox.x1, y1: line.bbox.y1, confidence: line.confidence }] : []
      })
      setOcrRegions(regions)
      setOcrStatus(regions.length ? 'ready' : 'empty')
    } catch (reason: unknown) {
      setOcrStatus('error')
      setOcrError(reason instanceof Error ? reason.message : '画像の文字を認識できませんでした。')
    } finally {
      await worker?.terminate()
    }
  }

  const isImage = preview.attachment.type === 'image'
  const ocrButtonLabel = ocrStatus === 'running' ? `文字を認識中… ${ocrProgress}%` : ocrStatus === 'ready' ? '再認識する' : '文字を認識する'

  return <div className="modal-backdrop attachment-preview-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose() }}><section className="attachment-preview-modal" role="dialog" aria-modal="true" aria-labelledby="attachment-preview-title"><div className="modal-header"><div><h2 id="attachment-preview-title">{preview.attachment.name}</h2><span className="attachment-preview-meta">{isImage ? '画像' : preview.attachment.type === 'pdf' ? 'PDF' : '添付ファイル'} ・ {formatFileSize(preview.attachment.size)}</span></div><div className="attachment-preview-header-actions">{isImage && <button className="button button-secondary" type="button" disabled={ocrStatus === 'running'} onClick={() => void recognizeText()}><FileText size={16} />{ocrButtonLabel}</button>}<button className="modal-close" type="button" aria-label="プレビューを閉じる" onClick={onClose}><X size={19} /></button></div></div><div className="attachment-preview-content">{isImage ? <div className="attachment-image-preview"><div className="attachment-image-stage"><img ref={imageRef} className="attachment-preview-image" src={preview.url} alt={preview.attachment.name} onLoad={(event) => { const image = event.currentTarget; const { width: renderedWidth, height: renderedHeight } = image.getBoundingClientRect(); setImageSize({ width: image.naturalWidth, height: image.naturalHeight, renderedWidth, renderedHeight }) }} />{ocrRegions.length > 0 && imageSize && <div ref={ocrLayerRef} className="attachment-ocr-layer" aria-label="OCRで認識した文字" onPointerDown={handleOcrPointerDown} onPointerMove={handleOcrPointerMove} onPointerUp={endOcrPointerSelection} onPointerCancel={endOcrPointerSelection}>{ocrRegions.map((region, index) => { const renderedRegionHeight = Math.max(1, ((region.y1 - region.y0) / imageSize.height) * imageSize.renderedHeight); return <span className={`attachment-ocr-token${selectedOcrRegionIndexes.has(index) ? ' is-selected' : ''}`} data-confidence={region.confidence} data-ocr-region-index={index} key={`${region.x0}-${region.y0}-${index}`} style={{ left: `${(region.x0 / imageSize.width) * 100}%`, top: `${(region.y0 / imageSize.height) * 100}%`, width: `${((region.x1 - region.x0) / imageSize.width) * 100}%`, height: `${((region.y1 - region.y0) / imageSize.height) * 100}%`, fontSize: `${renderedRegionHeight}px`, lineHeight: `${renderedRegionHeight}px` }}>{region.text}</span> })}</div>}</div>{ocrStatus === 'ready' && <span className="attachment-ocr-status" role="status">認識した文字をカーソルや指でなぞって選択できます。</span>}{ocrStatus === 'running' && <span className="attachment-ocr-status" role="status">画像内の文字を解析しています。初回は少し時間がかかります。</span>}{ocrStatus === 'empty' && <span className="attachment-ocr-status">文字を認識できませんでした。画像を拡大して再認識してください。</span>}{ocrStatus === 'error' && <span className="attachment-ocr-status is-error" role="alert">{ocrError}</span>}</div> : preview.attachment.type === 'pdf' ? <iframe className="attachment-preview-frame" src={`${preview.url}#toolbar=1`} title={`${preview.attachment.name}のプレビュー`} /> : <div className="attachment-preview-empty"><FileText size={30} /><strong>このファイル形式は画面表示に対応していません</strong><a className="button button-secondary" href={preview.url} download={preview.attachment.name}>ファイルをダウンロード</a></div>}</div></section></div>
}

function CustomerDialog({ form, title, submitLabel, cancelLabel, autosaveStatus, autosaveLastSavedAt, saving, deleteLoading, error, onChange, onClose, onSubmit, onDelete }: { form: CustomerInput; title: string; submitLabel: string; cancelLabel?: string; autosaveStatus: AutosaveState; autosaveLastSavedAt: number | null; saving: boolean; deleteLoading: boolean; error: string; onChange: (form: CustomerInput) => void; onClose: () => void; onSubmit: (event: FormEvent<HTMLFormElement>) => void; onDelete?: () => void }) {
  return <Modal title={title} onClose={onClose}><form className="modal-form" onSubmit={onSubmit}>{error && <div className="modal-error" role="alert">{error}</div>}<div className="form-grid"><FormField label="顧客名" required><input autoFocus required value={form.name} onChange={(event) => onChange({ ...form, name: event.target.value })} placeholder="例：佐藤 太郎" /></FormField><FormField label="ふりがな"><input value={form.kana} onChange={(event) => onChange({ ...form, kana: event.target.value })} placeholder="例：さとう たろう" /></FormField><FormField label="電話番号"><NormalizedInput field="phone" type="tel" value={form.phone} onChange={(phone) => onChange({ ...form, phone })} placeholder="例：090-1234-5678" /></FormField><FormField label="メールアドレス"><input type="email" value={form.email} onChange={(event) => onChange({ ...form, email: event.target.value })} placeholder="例：sato@example.com" /></FormField><FormField label="生年月日"><ModalDateInput ariaLabel="生年月日" value={form.birthDate} onChange={(birthDate) => onChange({ ...form, birthDate })} placeholder="例：1990/01/23" /></FormField><FormField label="勤務先等"><input value={form.employer} onChange={(event) => onChange({ ...form, employer: event.target.value })} placeholder="例：〇〇株式会社" /></FormField><FormField label="郵便番号"><NormalizedInput field="postalCode" value={form.postalCode ?? ''} onChange={(postalCode) => onChange({ ...form, postalCode })} placeholder="例：100-0001" /></FormField><FormField label="住所"><input value={form.address} onChange={(event) => onChange({ ...form, address: event.target.value })} placeholder="例：東京都千代田区" /></FormField><FormField label="メモ"><textarea value={form.memo} onChange={(event) => onChange({ ...form, memo: event.target.value })} placeholder="連絡方法など" /></FormField></div><ModalFooter leading={<AutosaveStatus status={autosaveStatus} lastSavedAt={autosaveLastSavedAt} />} onClose={onClose} cancelLabel={cancelLabel} submitLabel={submitLabel} disabled={saving} deleteLoading={deleteLoading} onDelete={onDelete} deleteLabel={deleteLoading ? '削除確認中…' : '顧客を削除'} /></form></Modal>
}

function VehicleDialog({ form, title, submitLabel, cancelLabel, autosaveStatus, autosaveLastSavedAt, saving, deleteLoading, error, customerName, onChange, onClose, onSubmit, onDelete }: { form: VehicleInput; title: string; submitLabel: string; cancelLabel?: string; autosaveStatus: AutosaveState; autosaveLastSavedAt: number | null; saving: boolean; deleteLoading: boolean; error: string; customerName: string; onChange: (form: VehicleInput) => void; onClose: () => void; onSubmit: (event: FormEvent<HTMLFormElement>) => void; onDelete?: () => void }) {
  return <Modal title={title} onClose={onClose}><form className="modal-form" onSubmit={onSubmit}>{error && <div className="modal-error" role="alert">{error}</div>}<p className="modal-description"><UserRound size={16} />{customerName} の車両情報を登録します。</p><div className="form-grid"><FormField label="メーカー" required><input autoFocus required value={form.maker} onChange={(event) => onChange({ ...form, maker: event.target.value })} placeholder="例：トヨタ" /></FormField><FormField label="車名" required><input required value={form.model} onChange={(event) => onChange({ ...form, model: event.target.value })} placeholder="例：プリウス" /></FormField><FormField label="型式"><input value={form.modelType} onChange={(event) => onChange({ ...form, modelType: event.target.value })} placeholder="例：6AA-ZVW60" /></FormField><FormField label="登録番号"><input value={form.plate} onChange={(event) => onChange({ ...form, plate: event.target.value })} placeholder="例：品川 500 あ 1234" /></FormField><FormField label="車台番号"><input value={form.vin} onChange={(event) => onChange({ ...form, vin: event.target.value })} placeholder="例：ZVW5000001" /></FormField><FormField label="年式"><NormalizedInput field="modelYear" value={form.year} onChange={(year) => onChange({ ...form, year })} placeholder="例：2024年" /></FormField><FormField label="車検満了日"><input type="date" value={form.inspectionDate.replace(/\//g, '-')} onChange={(event) => onChange({ ...form, inspectionDate: event.target.value.replace(/-/g, '/') })} /></FormField><FormField label="走行距離"><NormalizedInput field="mileage" value={form.mileage} onChange={(mileage) => onChange({ ...form, mileage })} placeholder="例：12,500 km" /></FormField><FormField label="車体色"><input value={form.color} onChange={(event) => onChange({ ...form, color: event.target.value })} placeholder="例：パールホワイト" /></FormField><FormField label="排気量"><NormalizedInput field="displacement" inputMode="numeric" value={form.displacement} onChange={(displacement) => onChange({ ...form, displacement })} placeholder="例：1800 cc" /></FormField><FormField label="ミッション"><input value={form.transmission} onChange={(event) => onChange({ ...form, transmission: event.target.value })} placeholder="例：CVT" /></FormField><FormField label="自由項目1"><input value={form.freeItem1} onChange={(event) => onChange({ ...form, freeItem1: event.target.value })} placeholder="例：駆動方式" /></FormField><FormField label="自由項目2"><input value={form.freeItem2} onChange={(event) => onChange({ ...form, freeItem2: event.target.value })} placeholder="自由項目" /></FormField><FormField label="自由項目3"><input value={form.freeItem3} onChange={(event) => onChange({ ...form, freeItem3: event.target.value })} placeholder="自由項目" /></FormField><FormField label="備考"><textarea value={form.note} onChange={(event) => onChange({ ...form, note: event.target.value })} placeholder="車両に関するメモ" /></FormField></div><ModalFooter leading={<AutosaveStatus status={autosaveStatus} lastSavedAt={autosaveLastSavedAt} />} onClose={onClose} cancelLabel={cancelLabel} submitLabel={submitLabel} disabled={saving} deleteLoading={deleteLoading} onDelete={onDelete} deleteLabel={deleteLoading ? '削除確認中…' : '車両を削除'} /></form></Modal>
}

function ModalDateInput({ ariaLabel, value, onChange, placeholder }: { ariaLabel: string; value: string; onChange: (value: string) => void; placeholder?: string }) {
  return <div className="modal-date-input"><input aria-label={ariaLabel} value={value} onChange={(event) => onChange(event.target.value)} placeholder={placeholder} /><DateCalendarButton ariaLabel={ariaLabel} value={value} onChange={onChange} /></div>
}

function FormField({ label, required, children }: { label: string; required?: boolean; children: ReactNode }) {
  return <div className="form-field"><span>{label}{required && <em>必須</em>}</span>{children}</div>
}

function Modal({ title, titleId = 'modal-title', backdropClassName = '', onClose, children }: { title: string; titleId?: string; backdropClassName?: string; onClose: () => void; children: ReactNode }) {
  return <div className={`modal-backdrop ${backdropClassName}`.trim()} role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose() }}><section className="modal" role="dialog" aria-modal="true" aria-labelledby={titleId}><div className="modal-header"><h2 id={titleId}>{title}</h2><button className="modal-close" type="button" aria-label="閉じる" onClick={onClose}><X size={19} /></button></div>{children}</section></div>
}

function ModalFooter({ onClose, cancelLabel = 'キャンセル', submitLabel, disabled, leading, onDelete, deleteLabel = '削除', deleteLoading = false }: { onClose: () => void; cancelLabel?: string; submitLabel: string; disabled: boolean; leading?: ReactNode; onDelete?: () => void; deleteLabel?: string; deleteLoading?: boolean }) {
  return <div className="modal-footer"><div className="modal-footer-leading">{onDelete && <button className="button button-danger modal-delete-button" type="button" onClick={onDelete} disabled={disabled || deleteLoading}>{deleteLabel}</button>}{leading}</div><div className="modal-footer-actions"><button className="button button-secondary" type="button" onClick={onClose} disabled={disabled || deleteLoading}>{cancelLabel}</button><button className="button button-primary" type="submit" disabled={disabled || deleteLoading}>{submitLabel}</button></div></div>
}

function MasterDeletionDialog({ impact, loading, onClose, onConfirm }: { impact: MasterDeletionImpact; loading: boolean; onClose: () => void; onConfirm: () => void }) {
  const isCustomer = impact.kind === 'customer'
  return <Modal title="削除内容の確認" titleId="master-deletion-modal-title" backdropClassName="master-deletion-backdrop" onClose={onClose}><div className="master-deletion-content"><p className="master-deletion-warning">{isCustomer ? `顧客「${impact.label}」を削除します。` : `車両「${impact.label}」を削除します。`}</p><p>{isCustomer ? '顧客と所有車両は通常の一覧から非表示になり、関連書類はアーカイブされます。' : '車両は通常の一覧から非表示になり、関連書類はアーカイブされます。'}</p><dl className="master-deletion-impact-list"><div><dt>車両</dt><dd>{impact.vehicleCount}台</dd></div><div><dt>関連書類</dt><dd>{impact.documentCount}件</dd></div>{impact.archivedDocumentCount > 0 && <div><dt>うち既存アーカイブ</dt><dd>{impact.archivedDocumentCount}件</dd></div>}<div><dt>点検予定</dt><dd>{impact.inspectionCount}件</dd></div><div><dt>添付ファイル</dt><dd>{impact.attachmentCount}件</dd></div></dl><p className="master-deletion-note">削除後も、アーカイブ画面から書類を1件ずつ復元できます。書類を復元した場合は、その書類に必要な顧客・車両だけが表示に戻ります。</p><div className="master-deletion-footer"><button className="button button-secondary" type="button" onClick={onClose} disabled={loading}>キャンセル</button><button className="button button-danger" type="button" onClick={onConfirm} disabled={loading}>{loading ? '削除しています…' : '確認して削除'}</button></div></div></Modal>
}

function formatFileSize(bytes: number) {
  if (bytes < 1024 * 1024) return `${Math.max(1, Math.round(bytes / 1024))} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

function mapCustomerSummaryToRecord(summary: { id: string; name: string; kana: string; phone: string; updatedAt: string }): Customer {
  return { id: summary.id, name: summary.name, kana: summary.kana, phone: summary.phone, email: '', postalCode: '', address: '', birthDate: '', employer: '', memo: '', updatedAt: summary.updatedAt, vehicles: [], isSummary: true }
}

function getErrorMessage(reason: unknown) {
  return reason instanceof Error ? reason.message : '顧客・車両データの処理に失敗しました。'
}
