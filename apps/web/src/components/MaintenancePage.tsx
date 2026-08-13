import { useEffect, useMemo, useRef, useState, type FormEvent } from 'react'
import {
  Archive,
  CarFront,
  ChevronLeft,
  ChevronRight,
  ChevronDown,
  ClipboardCheck,
  Eye,
  FileDown,
  FileText,
  Plus,
  Save,
  Search,
  UserRound,
  X,
} from 'lucide-react'
import { fetchCustomers, type Customer } from '../lib/customerApi'
import { fetchSyncPreview, type SyncPreviewInput, type SyncPreviewResponse } from '../lib/masterSyncApi'
import { downloadMaintenanceDocumentPdf, previewMaintenanceDocumentPdf } from '../lib/pdf'
import {
  archiveMaintenanceDocument,
  createMaintenanceDocument,
  fetchMaintenanceDocuments,
  updateMaintenanceDocument,
  defaultMaintenanceDocumentDetails,
  type IntakeCategory,
  type MandatoryFees,
  type MaintenanceFeeKey,
  type MaintenanceCustomerDetails,
  type MaintenanceDocument,
  type MaintenanceDocumentLike,
  type MaintenanceDocumentDetails,
  type MaintenanceDocumentInput,
  type MaintenanceDocumentType,
  type MaintenanceItemKind,
  type MaintenanceLineItem,
  type MaintenanceVehicleDetails,
  type MaintenanceStatus,
} from '../lib/maintenanceApi'
import { buildMaintenanceStatementSvg, calculateMaintenanceStatementTotals } from '../lib/maintenanceStatement'
import { defaultSettings, fetchSettings, type AppSettings } from '../lib/settingsApi'
import { DocumentFilterGroup, type DocumentFilterOption } from './DocumentFilterGroup'
import { compareSortableDocuments, type DocumentSortDirection, type DocumentSortKey } from './DocumentSort'
import { DocumentSortControls } from './DocumentSortControls'
import { DocumentTaxSettings } from './DocumentTaxSettings'
import { MaintenanceStatementEditor, type MaintenanceStatementItemField } from './MaintenanceStatementEditor'
import { MasterSyncConfirmationDialog, type MasterSyncConfirmationResult } from './MasterSyncConfirmationDialog'
import { MaintenanceDuplicateConfirmationDialog, type MaintenanceDuplicateDialogState } from './MaintenanceDuplicateConfirmationDialog'
import { OptionalDateField } from './OptionalDateField'
import { AbacusLinkProvenance } from './AbacusLinkProvenance'

type CategoryFilter = 'すべて' | IntakeCategory
type MaintenanceTypeFilter = 'すべて' | MaintenanceDocumentType
type MaintenanceStatusFilter = 'すべて' | Exclude<MaintenanceStatus, 'アーカイブ済み'>
type MaintenanceDocumentView = 'edit' | 'preview'
type MaintenanceCreateForm = { type: MaintenanceDocumentType; category: IntakeCategory; customerMode: 'existing' | 'new' | null; customerId: string; vehicleMode: 'existing' | 'new' | null; vehicleId: string }
type CompletedMaintenanceGroup = { key: string; label: string; documents: MaintenanceDocument[] }

type MasterSnapshot =
  | { state: 'loading' }
  | { state: 'ready'; customerId: string; vehicleId: string; customerUpdatedAt: string; vehicleUpdatedAt: string; mileage: number | null }
  | { state: 'invalid' }

type MaintenanceDraftContext = {
  customerMode: 'existing' | 'new'
  vehicleMode: 'existing' | 'new'
  customerId: string | null
  customerUpdatedAt: string | null
  vehicleId: string | null
  vehicleUpdatedAt: string | null
  openedMileage: number | null
}

type MaintenanceDuplicateConfirmation = { registrationNumberConfirmed: true; confirmedVehicleId: string }
type MaintenanceMasterSync = NonNullable<MaintenanceDocumentInput['masterSync']>
type MaintenanceMileageSync = NonNullable<MaintenanceDocumentInput['mileageSync']>

const maintenanceDocumentTypeOptions: MaintenanceDocumentType[] = ['整備見積書', '整備請求書']
const maintenanceCategoryOptions: IntakeCategory[] = ['車検', '板金', '一般整備']
const maintenanceStatusOptions: Exclude<MaintenanceStatus, 'アーカイブ済み'>[] = ['下書き', '入金待ち', '完了']
const maintenanceTypeFilterOptions: DocumentFilterOption<MaintenanceTypeFilter>[] = [
  { value: 'すべて', label: 'すべて', tone: 'all' },
  { value: '整備見積書', label: '見積書', tone: 'estimate' },
  { value: '整備請求書', label: '請求書', tone: 'invoice' },
]
const maintenanceStatusFilterOptions: DocumentFilterOption<MaintenanceStatusFilter>[] = [
  { value: 'すべて', label: 'すべて', tone: 'all' },
  { value: '下書き', label: '下書き', tone: 'draft' },
  { value: '入金待ち', label: '入金待ち', tone: 'pending' },
  { value: '完了', label: '完了', tone: 'completed' },
]
const maintenanceCategoryFilterOptions: DocumentFilterOption<CategoryFilter>[] = [
  { value: 'すべて', label: 'すべて', tone: 'all' },
  { value: '車検', label: '車検', tone: 'inspection' },
  { value: '板金', label: '板金', tone: 'bodywork' },
  { value: '一般整備', label: '一般整備', tone: 'general' },
]
const emptyFees: MandatoryFees = { 自賠責: 0, 重量税: 0, 印紙代: 0, リサイクル料金: 0 }
const emptyCreateForm: MaintenanceCreateForm = { type: '整備見積書', category: '一般整備', customerMode: null, customerId: '', vehicleMode: null, vehicleId: '' }
const NEW_CUSTOMER_VALUE = '__new_customer__'
const NEW_VEHICLE_VALUE = '__new_vehicle__'

export function MaintenancePage({ initialDocumentId }: { initialDocumentId?: string } = {}) {
  const [documents, setDocuments] = useState<MaintenanceDocument[]>([])
  const [customers, setCustomers] = useState<Customer[]>([])
  const [settings, setSettings] = useState<AppSettings>(defaultSettings)
  const [query, setQuery] = useState('')
  const [sortKey, setSortKey] = useState<DocumentSortKey>('dueDate')
  const [sortDirection, setSortDirection] = useState<DocumentSortDirection>('asc')
  const [typeFilter, setTypeFilter] = useState<MaintenanceTypeFilter>('すべて')
  const [statusFilter, setStatusFilter] = useState<MaintenanceStatusFilter>('すべて')
  const [categoryFilter, setCategoryFilter] = useState<CategoryFilter>('すべて')
  const [selectedDocumentId, setSelectedDocumentId] = useState(initialDocumentId ?? '')
  const [mobileWorkspaceView, setMobileWorkspaceView] = useState<'list' | 'detail'>(initialDocumentId ? 'detail' : 'list')
  const [createDialogOpen, setCreateDialogOpen] = useState(false)
  const [createForm, setCreateForm] = useState<MaintenanceCreateForm>(emptyCreateForm)
  const [draftDocument, setDraftDocument] = useState<MaintenanceDocumentLike | null>(null)
  const [draftDirty, setDraftDirty] = useState(false)
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [savedDocumentId, setSavedDocumentId] = useState('')
  const [error, setError] = useState('')
  const [documentView, setDocumentView] = useState<MaintenanceDocumentView>('edit')
  const [masterSyncDialogResult, setMasterSyncDialogResult] = useState<SyncPreviewResponse | null>(null)
  const [maintenanceDuplicateDialog, setMaintenanceDuplicateDialog] = useState<MaintenanceDuplicateDialogState | null>(null)
  const [pendingDraftPreview, setPendingDraftPreview] = useState<SyncPreviewResponse | null>(null)
  const [pendingDraftDuplicateConfirmation, setPendingDraftDuplicateConfirmation] = useState<MaintenanceDuplicateConfirmation | undefined>(undefined)
  const draftDocumentRef = useRef<MaintenanceDocumentLike | null>(null)
  const draftContextRef = useRef<MaintenanceDraftContext | null>(null)
  const draftCustomerDuplicateConfirmedRef = useRef(false)
  const documentOpenedMileageRef = useRef<number | null>(null)
  const lastOpenedDocumentIdRef = useRef<string | null>(null)
  const openedMasterSnapshotRef = useRef<MasterSnapshot | null>(null)

  useEffect(() => {
    let cancelled = false
    Promise.all([fetchMaintenanceDocuments(), fetchCustomers(), fetchSettings()])
      .then(([nextDocuments, nextCustomers, nextSettings]) => {
        if (cancelled) return
        setDocuments(nextDocuments)
        setCustomers(nextCustomers)
        setSettings(nextSettings)
        const nextSelectedDocumentId = initialDocumentId && nextDocuments.some((document) => document.id === initialDocumentId) ? initialDocumentId : ''
        setSelectedDocumentId(nextSelectedDocumentId)
        if (nextSelectedDocumentId) setMobileWorkspaceView('detail')
        setError('')
      })
      .catch((reason: unknown) => { if (!cancelled) setError(reason instanceof Error ? reason.message : '整備データを読み込めませんでした。') })
      .finally(() => { if (!cancelled) setLoading(false) })
    return () => { cancelled = true }
  }, [initialDocumentId])

  const filteredDocuments = useMemo(() => {
    const normalizedQuery = query.trim().toLocaleLowerCase()
    return documents.filter((document) => {
      const matchesType = typeFilter === 'すべて' || document.type === typeFilter
      const matchesStatus = statusFilter === 'すべて' || document.status === statusFilter
      const matchesCategory = categoryFilter === 'すべて' || document.category === categoryFilter
      const searchableText = `${document.number} ${document.customerName} ${document.vehicle} ${document.plate}`.toLocaleLowerCase()
      return matchesType && matchesStatus && matchesCategory && (!normalizedQuery || searchableText.includes(normalizedQuery))
    }).sort((left, right) => compareSortableDocuments(left, right, sortKey, sortDirection))
  }, [categoryFilter, documents, query, sortDirection, sortKey, statusFilter, typeFilter])

  const incompleteDocuments = useMemo(() => filteredDocuments.filter((document) => document.status === '下書き' || document.status === '入金待ち'), [filteredDocuments])
  const completedGroups = useMemo(() => groupCompletedDocuments(filteredDocuments.filter((document) => document.status === '完了')), [filteredDocuments])

  const selectedPersistedDocument = filteredDocuments.find((document) => document.id === selectedDocumentId) ?? incompleteDocuments[0] ?? filteredDocuments[0] ?? null
  const selectedDocument: MaintenanceDocumentLike | null = draftDocument ?? selectedPersistedDocument
  const totals = selectedDocument ? calculateMaintenanceStatementTotals(selectedDocument) : null

  function setActiveDraft(nextDraft: MaintenanceDocumentLike | null) {
    draftDocumentRef.current = nextDraft
    setDraftDocument(nextDraft)
  }

  function replaceActiveDocument(updater: (document: MaintenanceDocumentLike) => MaintenanceDocumentLike) {
    if (draftDocument) {
      setDraftDocument((current) => {
        const nextDraft = current ? updater(current) : current
        draftDocumentRef.current = nextDraft
        return nextDraft
      })
      return
    }
    if (!selectedPersistedDocument) return
    if (selectedDocumentId !== selectedPersistedDocument.id) {
      // 一覧の先頭をフォールバック表示している場合も、編集対象をIDで固定する。
      // 支払期限の変更で一覧順が変わっても別書類へ切り替わらないようにする。
      setSelectedDocumentId(selectedPersistedDocument.id)
    }
    setDocuments((current) => current.map((document) => document.id === selectedPersistedDocument.id ? updater(document) as MaintenanceDocument : document))
  }

  function openMobileDetail() {
    setMobileWorkspaceView('detail')
    if (window.matchMedia('(max-width: 1169px)').matches) window.scrollTo(0, 0)
  }

  function openMobileList() {
    setMobileWorkspaceView('list')
    if (window.matchMedia('(max-width: 1169px)').matches) window.scrollTo(0, 0)
  }

  function discardDraftIfConfirmed(action: string) {
    if (!draftDocument) return true
    if (draftDirty && !window.confirm(`入力中の未保存書類を破棄して${action}しますか？`)) return false
    setActiveDraft(null)
    draftContextRef.current = null
    draftCustomerDuplicateConfirmedRef.current = false
    setDraftDirty(false)
    setMasterSyncDialogResult(null)
    setMaintenanceDuplicateDialog(null)
    setPendingDraftPreview(null)
    setPendingDraftDuplicateConfirmation(undefined)
    return true
  }

  function selectPersistedDocument(documentId: string) {
    if (!discardDraftIfConfirmed('別の書類を表示')) return
    setSelectedDocumentId(documentId)
    setDocumentView('edit')
    openMobileDetail()
  }

  // Reset documentOpenedMileage and openedMasterSnapshot only when selected document ID changes
  useEffect(() => {
    if (draftDocument) {
      lastOpenedDocumentIdRef.current = null
      documentOpenedMileageRef.current = null
      openedMasterSnapshotRef.current = null
      return
    }
    const currentDocumentId = selectedPersistedDocument?.id ?? null
    if (currentDocumentId === lastOpenedDocumentIdRef.current) return
    lastOpenedDocumentIdRef.current = currentDocumentId
    if (!selectedPersistedDocument) {
      documentOpenedMileageRef.current = null
      openedMasterSnapshotRef.current = null
      return
    }
    const overrideMileage = parseMileageString(selectedPersistedDocument.details.vehicleOverride?.mileage)
    documentOpenedMileageRef.current = overrideMileage ?? parseMileageString(selectedPersistedDocument.mileage)

    // Initialize openedMasterSnapshot when document, customer, and vehicle are all available
    const foundCustomer = customers.find((c) => c.id === selectedPersistedDocument.customerId)
    const foundVehicle = foundCustomer?.vehicles.find((v) => v.id === selectedPersistedDocument.vehicleId)
    if (foundCustomer && foundVehicle) {
      openedMasterSnapshotRef.current = {
        state: 'ready',
        customerId: foundCustomer.id,
        vehicleId: foundVehicle.id,
        customerUpdatedAt: foundCustomer.updatedAt,
        vehicleUpdatedAt: foundVehicle.updatedAt,
        mileage: parseMileageString(foundVehicle.mileage),
      }
    } else {
      openedMasterSnapshotRef.current = { state: 'loading' }
    }
  }, [draftDocument, selectedPersistedDocument, customers])

  function updateItem(itemId: string, field: 'kind' | 'description' | 'quantity' | 'unit' | 'unitPrice' | 'technicalFee' | 'summary', value: string) {
    if (!selectedDocument) return
    const nextValue = field === 'kind' ? value as MaintenanceItemKind : field === 'description' || field === 'unit' || field === 'summary' ? value : Number(value) || 0
    replaceActiveDocument((document) => ({ ...document, items: document.items.map((item) => item.id === itemId ? { ...item, [field]: nextValue, abacusDetail: null, isAbacusMigration: false } : item) }))
    markChanged()
  }

  function addItem() {
    if (!selectedDocument || selectedDocument.items.length >= 18) return
    const newItem: MaintenanceLineItem = { id: `maintenance-item-${Date.now()}`, kind: '作業', description: '', quantity: 1, unit: '式', unitPrice: 0, technicalFee: 0, summary: '' }
    replaceActiveDocument((document) => ({ ...document, items: [...document.items, newItem] }))
    markChanged()
  }

  function removeItem(itemId: string) {
    if (!selectedDocument) return
    replaceActiveDocument((document) => ({ ...document, items: document.items.filter((item) => item.id !== itemId) }))
    markChanged()
  }

  function updateFee(key: MaintenanceFeeKey, value: string) {
    if (!selectedDocument) return
    const nextValue = Number(value) || 0
    replaceActiveDocument((document) => key === '調整額' ? { ...document, adjustment: nextValue } : { ...document, fees: { ...document.fees, [key]: nextValue } })
    markChanged()
  }

  function updateDetails(details: MaintenanceDocumentDetails) {
    if (!selectedDocument) return
    replaceActiveDocument((document) => ({ ...document, details }))
    markChanged()
  }

  function updateTaxRate(value: number) {
    if (!selectedDocument) return
    replaceActiveDocument((document) => ({ ...document, taxRate: value / 100 }))
    markChanged()
  }

  function updateHeader(field: 'number' | 'type' | 'status' | 'category' | 'customerId' | 'vehicleId' | 'intakeDate' | 'plannedReleaseDate' | 'issuedAt' | 'dueDate' | 'note', value: string) {
    if (!selectedDocument) return
    if (draftDocument && (field === 'customerId' || field === 'vehicleId')) return
    if (draftDocument) {
      replaceActiveDocument((document) => ({ ...document, [field]: value }))
    } else {
      setDocuments((current) => current.map((document) => document.id !== selectedDocument.id ? document : updateMaintenanceHeader(document, field, value, customers)))
    }
    markChanged()
  }

  function markChanged() {
    if (draftDocumentRef.current) {
      draftCustomerDuplicateConfirmedRef.current = false
      setMasterSyncDialogResult(null)
      setMaintenanceDuplicateDialog(null)
      setPendingDraftPreview(null)
      setPendingDraftDuplicateConfirmation(undefined)
      setDraftDirty(true)
    }
    setSavedDocumentId('')
  }

  async function archiveSelectedDocument() {
    if (draftDocument || !selectedPersistedDocument || saving) return
    if (!window.confirm(`${selectedPersistedDocument.number}をアーカイブしますか？`)) return
    setSaving(true)
    setError('')
    try {
      await archiveMaintenanceDocument(selectedPersistedDocument.id)
      setDocuments((current) => current.filter((document) => document.id !== selectedPersistedDocument.id))
      setSelectedDocumentId('')
      setSavedDocumentId('')
    } catch (reason: unknown) {
      setError(reason instanceof Error ? reason.message : '整備書類をアーカイブできませんでした。')
    } finally {
      setSaving(false)
    }
  }

  async function saveSelectedDocument(mileageSync?: MaintenanceMileageSync, masterSync?: MaintenanceMasterSync) {
    if (draftDocument || !selectedPersistedDocument) return
    setSaving(true)
    setError('')
    try {
      const input = toMaintenanceInput(selectedPersistedDocument, mileageSync)
      const inputWithMasterSync = masterSync ? { ...input, masterSync } : input
      const saved = await updateMaintenanceDocument(selectedPersistedDocument.id, inputWithMasterSync)
      setDocuments((current) => current.map((document) => document.id === saved.id ? saved : document))
      setSavedDocumentId(saved.id)
      // Update documentOpenedMileage after successful save
      documentOpenedMileageRef.current = mileageSync?.inputMileage ?? documentOpenedMileageRef.current

      // Re-fetch customers to get latest updatedAt and mileage
      try {
        const nextCustomers = await fetchCustomers()
        setCustomers(nextCustomers)
        // Update openedMasterSnapshot with latest values
        const foundCustomer = nextCustomers.find((c) => c.id === selectedPersistedDocument.customerId)
        const foundVehicle = foundCustomer?.vehicles.find((v) => v.id === selectedPersistedDocument.vehicleId)
        if (foundCustomer && foundVehicle) {
          openedMasterSnapshotRef.current = {
            state: 'ready',
            customerId: foundCustomer.id,
            vehicleId: foundVehicle.id,
            customerUpdatedAt: foundCustomer.updatedAt,
            vehicleUpdatedAt: foundVehicle.updatedAt,
            mileage: parseMileageString(foundVehicle.mileage),
          }
        } else {
          openedMasterSnapshotRef.current = { state: 'loading' }
        }
      } catch {
        // Re-fetch failed but document was saved successfully
        setError('書類は保存されましたが、最新の顧客・車両情報を再取得できませんでした。画面を再読み込みしてください。')
        openedMasterSnapshotRef.current = { state: 'invalid' }
      }
    } catch (reason) {
      if (reason instanceof Error && reason.message.includes('顧客または車両情報が更新されました')) {
        setError('顧客または車両情報が更新されました。再読み込み後にもう一度保存してください。')
      } else {
        setError(reason instanceof Error ? reason.message : '整備書類を保存できませんでした。')
      }
    } finally {
      setSaving(false)
    }
  }

  async function handleSaveClick() {
    if (saving || !selectedDocument) return
    if (draftDocument) {
      if (!draftDirty) return
      const currentDraft = draftDocumentRef.current ?? draftDocument
      const context = draftContextRef.current
      if (!context) {
        setError('未保存書類の顧客・車両情報を確認できません。書類を開き直してください。')
        return
      }
      setError('')
      setSaving(true)
      await runMaintenanceDraftSyncPreview(currentDraft, context)
      return
    }
    if (!selectedPersistedDocument) return
    if (openedMasterSnapshotRef.current?.state === 'invalid') {
      setError('最新の顧客・車両情報を確認できないため保存できません。画面を再読み込みしてください。')
      return
    }

    const openedMileage = documentOpenedMileageRef.current

    // Call sync-preview to check for differences
    const snapshot = openedMasterSnapshotRef.current
    try {
      const preview = await fetchSyncPreview({
        documentType: 'maintenance',
        documentId: selectedPersistedDocument.id,
        customerId: selectedPersistedDocument.customerId || undefined,
        vehicleId: selectedPersistedDocument.vehicleId || undefined,
        customerOverride: maintenanceCustomerValuesForSave(selectedPersistedDocument),
        vehicleOverride: selectedPersistedDocument.details.vehicleOverride ?? undefined,
        issuedAt: selectedPersistedDocument.issuedAt.replaceAll('/', '-'),
        openedCustomerUpdatedAt: snapshot?.state === 'ready' ? snapshot.customerUpdatedAt : undefined,
        openedVehicleUpdatedAt: snapshot?.state === 'ready' ? snapshot.vehicleUpdatedAt : undefined,
        mileageContext: { openedMileage: openedMileage },
      })

      const hasDiffs = preview.customerDiffs.length > 0 || preview.vehicleDiffs.length > 0 || Boolean(preview.mileageDiff?.isChanged)
      if (hasDiffs) {
        setMasterSyncDialogResult(preview)
        return
      }

      // No differences - save directly
      void saveSelectedDocument()
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : '同期プレビューの取得に失敗しました。')
    }
  }

  async function runMaintenanceDraftSyncPreview(document: MaintenanceDocumentLike, context: MaintenanceDraftContext, duplicateConfirmation?: MaintenanceDuplicateConfirmation) {
    try {
      const preview = await fetchSyncPreview(buildMaintenanceDraftSyncPreviewInput(document, context))
      await processMaintenanceDraftSyncPreview(preview, duplicateConfirmation)
    } catch (reason) {
      setSaving(false)
      setError(reason instanceof Error ? reason.message : '同期プレビューの取得に失敗しました。')
    }
  }

  async function processMaintenanceDraftSyncPreview(preview: SyncPreviewResponse, duplicateConfirmation?: MaintenanceDuplicateConfirmation) {
    const context = draftContextRef.current
    if (!context) {
      setSaving(false)
      setError('未保存書類の顧客・車両情報を確認できません。書類を開き直してください。')
      return
    }

    setPendingDraftPreview(preview)
    setPendingDraftDuplicateConfirmation(duplicateConfirmation)

    const duplicateCustomers = preview.duplicateCustomers ?? []
    if (context.customerMode === 'new' && !draftCustomerDuplicateConfirmedRef.current && duplicateCustomers.length > 0) {
      setSaving(false)
      setMaintenanceDuplicateDialog({ kind: 'customer', candidates: duplicateCustomers })
      return
    }

    const chassisCandidates = (preview.duplicateVehicles ?? []).filter((candidate) => candidate.matchReason === 'chassis_number')
    if (chassisCandidates.length > 0) {
      setSaving(false)
      setMaintenanceDuplicateDialog({ kind: 'vehicle', matchReason: 'chassis_number', candidates: chassisCandidates })
      return
    }

    const registrationCandidates = (preview.duplicateVehicles ?? []).filter((candidate) => candidate.matchReason === 'registration_number')
    const hasValidRegistrationConfirmation = Boolean(
      duplicateConfirmation?.registrationNumberConfirmed
      && duplicateConfirmation.confirmedVehicleId
      && registrationCandidates.some((candidate) => candidate.id === duplicateConfirmation.confirmedVehicleId),
    )
    if (registrationCandidates.length > 0 && !hasValidRegistrationConfirmation) {
      setSaving(false)
      setMaintenanceDuplicateDialog({ kind: 'vehicle', matchReason: 'registration_number', candidates: registrationCandidates })
      return
    }

    const hasMasterDiffs = preview.customerDiffs.length > 0 || preview.vehicleDiffs.length > 0 || Boolean(preview.mileageDiff?.isChanged)
    if (hasMasterDiffs) {
      setMasterSyncDialogResult(preview)
      setSaving(false)
      return
    }

    const currentDraft = draftDocumentRef.current
    if (!currentDraft) {
      setSaving(false)
      setError('未保存書類が見つかりません。')
      return
    }
    setPendingDraftPreview(null)
    await createMaintenanceDraftDocument(currentDraft, context, duplicateConfirmation)
  }

  async function createMaintenanceDraftDocument(document: MaintenanceDocumentLike, context: MaintenanceDraftContext, duplicateConfirmation?: MaintenanceDuplicateConfirmation, masterSync?: MaintenanceMasterSync, mileageSync?: MaintenanceMileageSync) {
    setSaving(true)
    setError('')
    try {
      const currentDraft = draftDocumentRef.current ?? document
      const currentContext = draftContextRef.current ?? context
      const input = buildMaintenanceCreateInput(currentDraft, currentContext, duplicateConfirmation, masterSync, mileageSync)
      const saved = await createMaintenanceDocument(input)
      setDocuments((current) => [saved, ...current])
      setSelectedDocumentId(saved.id)
      setActiveDraft(null)
      draftContextRef.current = null
      draftCustomerDuplicateConfirmedRef.current = false
      setDraftDirty(false)
      setSavedDocumentId(saved.id)
      setMasterSyncDialogResult(null)
      setMaintenanceDuplicateDialog(null)
      setPendingDraftPreview(null)
      setPendingDraftDuplicateConfirmation(undefined)
      setDocumentView('edit')

      try {
        const nextCustomers = await fetchCustomers()
        setCustomers(nextCustomers)
        lastOpenedDocumentIdRef.current = saved.id
        const foundCustomer = nextCustomers.find((customer) => customer.id === saved.customerId)
        const foundVehicle = saved.vehicleId ? foundCustomer?.vehicles.find((vehicle) => vehicle.id === saved.vehicleId) : undefined
        if (foundCustomer && foundVehicle) {
          const mileage = parseMileageString(foundVehicle.mileage)
          documentOpenedMileageRef.current = mileage
          openedMasterSnapshotRef.current = {
            state: 'ready',
            customerId: foundCustomer.id,
            vehicleId: foundVehicle.id,
            customerUpdatedAt: foundCustomer.updatedAt,
            vehicleUpdatedAt: foundVehicle.updatedAt,
            mileage,
          }
        } else {
          openedMasterSnapshotRef.current = { state: 'invalid' }
        }
      } catch {
        setError('書類は保存されましたが、最新の顧客・車両情報を再取得できませんでした。画面を再読み込みしてください。')
        lastOpenedDocumentIdRef.current = saved.id
        openedMasterSnapshotRef.current = { state: 'invalid' }
      }
    } catch (reason) {
      setPendingDraftDuplicateConfirmation(undefined)
      setError(reason instanceof Error ? reason.message : '整備書類を保存できませんでした。')
    } finally {
      setSaving(false)
    }
  }

  async function handleUseExistingCustomer(customerId: string) {
    const currentDraft = draftDocumentRef.current
    const context = draftContextRef.current
    if (!currentDraft || !context || context.customerMode !== 'new') return

    setSaving(true)
    setError('')
    try {
      let nextCustomers = customers
      let customer = nextCustomers.find((item) => item.id === customerId)
      if (!customer) {
        nextCustomers = await fetchCustomers()
        setCustomers(nextCustomers)
        customer = nextCustomers.find((item) => item.id === customerId)
      }
      if (!customer) throw new Error('選択した既存顧客を確認できません。顧客一覧を再読み込みしてください。')

      const nextDraft: MaintenanceDocumentLike = {
        ...currentDraft,
        customerId: customer.id,
        customerName: customer.name,
        phone: customer.phone,
        customerDetails: mapMaintenanceCustomerDetails(customer),
      }
      const nextContext: MaintenanceDraftContext = {
        ...context,
        customerMode: 'existing',
        customerId: customer.id,
        customerUpdatedAt: customer.updatedAt,
      }
      draftContextRef.current = nextContext
      draftCustomerDuplicateConfirmedRef.current = false
      setActiveDraft(nextDraft)
      setMasterSyncDialogResult(null)
      setMaintenanceDuplicateDialog(null)
      setPendingDraftPreview(null)
      setPendingDraftDuplicateConfirmation(undefined)
      await runMaintenanceDraftSyncPreview(nextDraft, nextContext)
    } catch (reason) {
      setSaving(false)
      setError(reason instanceof Error ? reason.message : '既存顧客への切り替えに失敗しました。')
    }
  }

  async function handleContinueAsNewCustomer() {
    const preview = pendingDraftPreview
    if (!preview || !draftContextRef.current) return
    draftCustomerDuplicateConfirmedRef.current = true
    setMaintenanceDuplicateDialog(null)
    setPendingDraftPreview(null)
    setSaving(true)
    setPendingDraftDuplicateConfirmation(undefined)
    await processMaintenanceDraftSyncPreview(preview)
  }

  function canUseExistingVehicleForDraft(vehicleId: string) {
    const context = draftContextRef.current
    if (!context || context.customerMode !== 'existing' || !context.customerId) return false
    return customers.some((customer) => customer.id === context.customerId && customer.vehicles.some((vehicle) => vehicle.id === vehicleId))
  }

  async function handleUseExistingVehicle(vehicleId: string) {
    const currentDraft = draftDocumentRef.current
    const context = draftContextRef.current
    if (!currentDraft || !context || !canUseExistingVehicleForDraft(vehicleId)) return
    const customer = customers.find((item) => item.id === context.customerId)
    const vehicle = customer?.vehicles.find((item) => item.id === vehicleId)
    if (!customer || !vehicle) {
      setError('選択した既存車両を確認できません。顧客一覧を再読み込みしてください。')
      return
    }

    const nextDraft: MaintenanceDocumentLike = {
      ...currentDraft,
      vehicleId: vehicle.id,
      vehicle: [vehicle.maker, vehicle.model].filter(Boolean).join(' '),
      plate: vehicle.plate,
      mileage: vehicle.mileage,
      vehicleDetails: mapMaintenanceVehicleDetails(vehicle),
    }
    const nextContext: MaintenanceDraftContext = {
      ...context,
      vehicleMode: 'existing',
      vehicleId: vehicle.id,
      vehicleUpdatedAt: vehicle.updatedAt,
      openedMileage: parseMileageString(vehicle.mileage),
    }
    draftContextRef.current = nextContext
    draftCustomerDuplicateConfirmedRef.current = false
    setActiveDraft(nextDraft)
    setMasterSyncDialogResult(null)
    setMaintenanceDuplicateDialog(null)
    setPendingDraftPreview(null)
    setPendingDraftDuplicateConfirmation(undefined)
    setSaving(true)
    setError('')
    await runMaintenanceDraftSyncPreview(nextDraft, nextContext)
  }

  async function handleContinueAsNewVehicle(vehicleId: string) {
    const preview = pendingDraftPreview
    const isCurrentRegistrationCandidate = preview?.duplicateVehicles?.some((candidate) => candidate.id === vehicleId && candidate.matchReason === 'registration_number')
    if (!preview || !isCurrentRegistrationCandidate) {
      setError('登録番号の重複候補が更新されています。もう一度保存してください。')
      setSaving(false)
      return
    }
    const duplicateConfirmation: MaintenanceDuplicateConfirmation = { registrationNumberConfirmed: true, confirmedVehicleId: vehicleId }
    setMaintenanceDuplicateDialog(null)
    setPendingDraftPreview(null)
    setSaving(true)
    await processMaintenanceDraftSyncPreview(preview, duplicateConfirmation)
  }

  function handleMaintenanceDuplicateCancel() {
    setMaintenanceDuplicateDialog(null)
    setMasterSyncDialogResult(null)
    setPendingDraftPreview(null)
    setPendingDraftDuplicateConfirmation(undefined)
    setSaving(false)
  }

  function handleMaintenanceMasterSyncCancel() {
    setMasterSyncDialogResult(null)
    setPendingDraftPreview(null)
    setPendingDraftDuplicateConfirmation(undefined)
    setSaving(false)
  }

  function handleMasterSyncConfirm(result: MasterSyncConfirmationResult) {
    const preview = masterSyncDialogResult
    setMasterSyncDialogResult(null)
    if (draftDocumentRef.current) {
      const context = draftContextRef.current
      if (!context || !preview) {
        setSaving(false)
        setError('未保存書類の保存状態を確認できません。')
        return
      }
      try {
        const masterSync = buildMaintenanceMasterSync(result, preview)
        const mileageSync = buildMaintenanceMileageSync(draftDocumentRef.current, context, preview)
        const duplicateConfirmation = pendingDraftDuplicateConfirmation
        setPendingDraftPreview(null)
        setPendingDraftDuplicateConfirmation(undefined)
        void createMaintenanceDraftDocument(draftDocumentRef.current, context, duplicateConfirmation, masterSync, mileageSync)
      } catch (reason) {
        setSaving(false)
        setError(reason instanceof Error ? reason.message : '走行距離の保存値を確認できません。')
      }
      return
    }
    if (!selectedPersistedDocument) return

    const inputMileage = parseMileageString(selectedPersistedDocument.details.vehicleOverride?.mileage)
    const openedMileage = documentOpenedMileageRef.current
    const mileageChanged = inputMileage !== null && inputMileage !== openedMileage

    let mileageSync: MaintenanceMileageSync | undefined
    if (mileageChanged) {
      mileageSync = { confirmed: true, openedMileage: openedMileage ?? 0, inputMileage }
    }

    let masterSync: MaintenanceMasterSync | undefined
    if (result.customerFields.length > 0 || result.vehicleFields.length > 0) {
      masterSync = {
        confirmed: true,
        customerFields: result.customerFields,
        vehicleFields: result.vehicleFields,
        expectedCustomerUpdatedAt: result.customerFields.length > 0 ? (preview?.expectedCustomerUpdatedAt ?? undefined) : undefined,
        expectedVehicleUpdatedAt: result.vehicleFields.length > 0 ? (preview?.expectedVehicleUpdatedAt ?? undefined) : undefined,
      }
    }

    void saveSelectedDocument(mileageSync, masterSync)
  }


  function openCreateDialog() {
    if (!discardDraftIfConfirmed('新しい書類を作成')) return
    setCreateForm(emptyCreateForm)
    setCreateDialogOpen(true)
  }

  function startDraft(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!isValidCreateSelection(createForm)) return
    const customer = createForm.customerMode === 'existing' ? customers.find((item) => item.id === createForm.customerId) : undefined
    const vehicle = createForm.vehicleMode === 'existing' ? customer?.vehicles.find((item) => item.id === createForm.vehicleId) : undefined
    const draft: MaintenanceDocumentLike = {
      number: '未採番',
      type: createForm.type,
      status: '下書き',
      category: createForm.category,
      customerId: customer?.id ?? null,
      customerName: customer?.name ?? '',
      phone: customer?.phone ?? '',
      customerDetails: customer ? mapMaintenanceCustomerDetails(customer) : mapMaintenanceCustomerDetails(undefined),
      vehicleId: vehicle?.id ?? null,
      vehicle: vehicle ? [vehicle.maker, vehicle.model].filter(Boolean).join(' ') : '',
      plate: vehicle?.plate ?? '',
      mileage: vehicle?.mileage ?? '',
      vehicleDetails: vehicle ? mapMaintenanceVehicleDetails(vehicle) : null,
      details: structuredClone(defaultMaintenanceDocumentDetails),
      intakeDate: todayDisplay(),
      plannedReleaseDate: addDaysDisplay(2),
      completionDate: '',
      issuedAt: todayDisplay(),
      dueDate: '',
      taxRate: settings.tax.consumptionTaxRate / 100,
      taxRounding: settings.tax.rounding,
      fees: { ...emptyFees },
      adjustment: 0,
      note: '',
      archivedAt: null,
      archivedPreviousStatus: null,
      archivedBy: null,
      purgeAt: null,
      keepForever: false,
      items: [{ id: 'draft-maintenance-item-1', kind: '作業', description: '', quantity: 1, unit: '式', unitPrice: 0, technicalFee: 0, summary: '' }],
    }
    draftContextRef.current = {
      customerMode: createForm.customerMode!,
      vehicleMode: createForm.vehicleMode!,
      customerId: customer?.id ?? null,
      customerUpdatedAt: customer?.updatedAt ?? null,
      vehicleId: vehicle?.id ?? null,
      vehicleUpdatedAt: vehicle?.updatedAt ?? null,
      openedMileage: vehicle ? parseMileageString(vehicle.mileage) : null,
    }
    draftCustomerDuplicateConfirmedRef.current = false
    setActiveDraft(draft)
    setDraftDirty(false)
    setSelectedDocumentId('')
    setSavedDocumentId('')
    setMasterSyncDialogResult(null)
    setMaintenanceDuplicateDialog(null)
    setPendingDraftPreview(null)
    setPendingDraftDuplicateConfirmation(undefined)
    setError('')
    setDocumentView('edit')
    openMobileDetail()
    setCreateDialogOpen(false)
  }

  return <>
    <div className="page-header maintenance-page-header"><div><span className="page-eyebrow">整備書類</span><h1>車検・点検・一般</h1><p>整備の受付から作業明細、見積書・請求書まで管理します。</p></div><button className="button button-primary" type="button" onClick={openCreateDialog}><Plus size={18} />整備書類を作成</button></div>
    {error && <div className="customer-sync-status is-error"><span>{error}</span><button className="text-button" type="button" onClick={() => window.location.reload()}>再読み込み</button></div>}
    {loading && <div className="customer-sync-status"><span>整備書類を読み込んでいます。</span></div>}
    <div className="maintenance-toolbar"><label className="maintenance-search"><Search size={18} /><span className="sr-only">整備書類を検索</span><input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="書類番号、顧客名、車名で検索" /></label><DocumentSortControls sortKey={sortKey} sortDirection={sortDirection} onSortKeyChange={setSortKey} onSortDirectionChange={setSortDirection} /></div>
    <div className="document-filter-panel maintenance-document-filter-panel mobile-filter-panel"><DocumentFilterGroup label="書類種別" value={typeFilter} options={maintenanceTypeFilterOptions} onChange={setTypeFilter} /><DocumentFilterGroup label="状態" value={statusFilter} options={maintenanceStatusFilterOptions} onChange={setStatusFilter} /><DocumentFilterGroup label="入庫区分" value={categoryFilter} options={maintenanceCategoryFilterOptions} onChange={setCategoryFilter} /><button className="text-button document-filter-reset" type="button" onClick={() => { setTypeFilter('すべて'); setStatusFilter('すべて'); setCategoryFilter('すべて') }} disabled={typeFilter === 'すべて' && statusFilter === 'すべて' && categoryFilter === 'すべて'}>条件をリセット</button></div>
    <div className={`maintenance-workspace mobile-workspace mobile-workspace-${mobileWorkspaceView}`}><div className="mobile-workspace-list"><MaintenanceDocumentList incompleteDocuments={incompleteDocuments} completedGroups={completedGroups} selectedDocumentId={draftDocument ? '' : selectedPersistedDocument?.id ?? ''} onSelect={selectPersistedDocument} /></div><div className="mobile-workspace-detail"><button className="mobile-workspace-back" type="button" onClick={openMobileList}><ChevronLeft size={16} />整備書類一覧へ戻る</button>{selectedDocument && totals ? <MaintenanceDocumentDetail document={selectedDocument} isDraft={!selectedDocument.id} draftDirty={draftDirty} customers={customers} settings={settings} itemPresets={settings.maintenanceItemPresets} view={documentView} saving={saving} saved={!draftDocument && savedDocumentId === selectedDocument.id} onViewChange={setDocumentView} onUpdateHeader={updateHeader} onUpdateDetails={updateDetails} onUpdateTaxRate={updateTaxRate} onSave={() => void handleSaveClick()} onArchive={() => void archiveSelectedDocument()} onPdfDownload={() => { if (selectedPersistedDocument) void downloadMaintenanceDocumentPdf(selectedPersistedDocument, settings) }} onPdfPreview={() => { if (selectedPersistedDocument) void previewMaintenanceDocumentPdf(selectedPersistedDocument, settings) }} onUpdateItem={updateItem} onAddItem={addItem} onRemoveItem={removeItem} onUpdateFee={updateFee} /> : <div className="panel maintenance-empty"><ClipboardCheck size={30} /><strong>整備書類が見つかりません</strong><span>{loading ? '読み込み中です。' : '検索条件または絞り込み条件を変更してください。'}</span></div>}</div></div>
    {createDialogOpen && <MaintenanceDocumentDialog form={createForm} customers={customers} onChange={setCreateForm} onClose={() => setCreateDialogOpen(false)} onSubmit={startDraft} />}
    {maintenanceDuplicateDialog && <MaintenanceDuplicateConfirmationDialog state={maintenanceDuplicateDialog} canUseExistingVehicle={canUseExistingVehicleForDraft} onUseExistingCustomer={(customerId) => { void handleUseExistingCustomer(customerId) }} onContinueAsNewCustomer={() => { void handleContinueAsNewCustomer() }} onUseExistingVehicle={(vehicleId) => { void handleUseExistingVehicle(vehicleId) }} onContinueAsNewVehicle={(vehicleId) => { void handleContinueAsNewVehicle(vehicleId) }} onCancel={handleMaintenanceDuplicateCancel} />}
    {masterSyncDialogResult && <MasterSyncConfirmationDialog isOlderThanLatestDocument={masterSyncDialogResult.isOlderThanLatestDocument} customerDiffs={masterSyncDialogResult.customerDiffs} vehicleDiffs={masterSyncDialogResult.vehicleDiffs} mileageDiff={masterSyncDialogResult.mileageDiff} hasCustomerConflict={masterSyncDialogResult.customerDiffs.some((d) => d.isConflict)} hasVehicleConflict={masterSyncDialogResult.vehicleDiffs.some((d) => d.isConflict)} onConfirm={handleMasterSyncConfirm} onCancel={handleMaintenanceMasterSyncCancel} />}
  </>
}

function MaintenanceDocumentList({ incompleteDocuments, completedGroups, selectedDocumentId, onSelect }: { incompleteDocuments: MaintenanceDocument[]; completedGroups: CompletedMaintenanceGroup[]; selectedDocumentId: string; onSelect: (id: string) => void }) {
  return <div className="maintenance-list-stack">
    <section className="panel maintenance-list-panel">
      <div className="maintenance-list-header"><div><h2>整備書類（未完了）</h2><span>書類を選択すると詳細を表示します</span></div><span className="results-count">{incompleteDocuments.length}件</span></div>
      {incompleteDocuments.length > 0 ? <MaintenanceDocumentCards documents={incompleteDocuments} selectedDocumentId={selectedDocumentId} onSelect={onSelect} /> : <div className="maintenance-list-empty">未完了の整備書類はありません。</div>}
    </section>
    {completedGroups.length > 0 && <section className="panel maintenance-list-panel maintenance-completed-panel">
      <div className="maintenance-list-header"><div><h2>完了書類</h2><span>書類の作成月ごとに表示します</span></div><span className="results-count">{completedGroups.reduce((total, group) => total + group.documents.length, 0)}件</span></div>
      <div className="maintenance-completed-groups">{completedGroups.map((group) => <details className="maintenance-completed-group" key={group.key}><summary><span>{group.label}</span><span className="results-count">{group.documents.length}件</span></summary><MaintenanceDocumentCards documents={group.documents} selectedDocumentId={selectedDocumentId} onSelect={onSelect} /></details>)}</div>
    </section>}
  </div>
}

function MaintenanceDocumentCards({ documents, selectedDocumentId, onSelect }: { documents: MaintenanceDocument[]; selectedDocumentId: string; onSelect: (id: string) => void }) {
  return <div className="maintenance-document-list">{documents.map((document) => <button className={`maintenance-document-card${document.id === selectedDocumentId ? ' is-selected' : ''}`} key={document.id} type="button" onClick={() => onSelect(document.id)}><div className="maintenance-card-top"><MaintenanceDocumentTypeTag type={document.type} /><span className={`maintenance-category-badge maintenance-category-${document.category}`}>{document.category}</span><MaintenanceStatusTag status={document.status} />{document.abacusImport?.vehicleless && <span className="document-abacus-badge">ABACUS・車両なし</span>}<ChevronRight size={16} /></div><strong className="maintenance-card-number">{document.number}</strong><span className="maintenance-card-customer"><UserRound size={14} />{document.customerName}</span><span className="maintenance-card-vehicle"><CarFront size={14} />{document.vehicle} ・ {document.plate}</span><div className="maintenance-card-bottom"><span>入庫 {document.intakeDate || '未定'}</span></div></button>)}</div>
}

function groupCompletedDocuments(documents: MaintenanceDocument[]): CompletedMaintenanceGroup[] {
  const grouped = new Map<string, { label: string; documents: MaintenanceDocument[] }>()
  for (const document of documents) {
    const month = maintenanceDocumentMonth(document.issuedAt)
    const group = grouped.get(month.key) ?? { label: month.label, documents: [] }
    group.documents.push(document)
    grouped.set(month.key, group)
  }
  return Array.from(grouped, ([key, group]) => ({ key, ...group })).sort((left, right) => {
    if (left.key === 'unknown') return 1
    if (right.key === 'unknown') return -1
    return right.key.localeCompare(left.key)
  })
}

function maintenanceDocumentMonth(issuedAt: string) {
  const match = issuedAt.replaceAll('-', '/').match(/^(\d{4})\/(\d{1,2})/)
  if (!match) return { key: 'unknown', label: '年月不明（完了）' }
  const [, year, month] = match
  return { key: `${year}-${month.padStart(2, '0')}`, label: `${year}年${Number(month)}月（完了）` }
}

type MaintenanceHeaderField = 'number' | 'type' | 'status' | 'category' | 'customerId' | 'vehicleId' | 'intakeDate' | 'plannedReleaseDate' | 'issuedAt' | 'dueDate' | 'note'

function MaintenanceDocumentDetail({ document, isDraft, draftDirty, customers, settings, itemPresets, view, saving, saved, onViewChange, onUpdateHeader, onUpdateDetails, onUpdateTaxRate, onSave, onArchive, onPdfDownload, onPdfPreview, onUpdateItem, onAddItem, onRemoveItem, onUpdateFee }: MaintenanceDocumentDetailProps) {
  return <section className="panel maintenance-detail-panel">
    <div className="maintenance-detail-header">
      <div className="maintenance-detail-title"><div><div className="maintenance-detail-badges"><MaintenanceDocumentTypeTag type={document.type} /><span className={`maintenance-category-badge maintenance-category-${document.category}`}>{document.category}</span><MaintenanceStatusTag status={document.status} />{document.abacusImport?.vehicleless && <span className="document-abacus-badge">ABACUS・車両なし</span>}{isDraft && <span className="document-draft-badge">新規・未保存</span>}</div><h2>{document.id ? document.number : '未採番'}</h2><small>{document.type} ・ 発行元 {settings.shop.name}</small>{document.abacusImport?.vehicleless && <small className="document-abacus-source">ABACUS互換：顧客にのみ紐付く書類（{document.abacusImport.sourceLocation}）</small>}<AbacusLinkProvenance metadata={document.abacusImport} /></div></div>
      <div className="maintenance-detail-actions"><button className="button button-secondary" type="button" disabled={isDraft} onClick={onPdfPreview}><Eye size={16} />PDFで確認</button><button className="button button-secondary" type="button" disabled={saving || (isDraft && !draftDirty)} onClick={onSave}><Save size={16} />{saving ? '保存中…' : saved ? '保存済み' : '保存'}</button><button className="button button-secondary" type="button" disabled={isDraft} onClick={onPdfDownload}><FileDown size={16} />出力</button><button className="button button-danger" type="button" disabled={isDraft || saving} onClick={onArchive}><Archive size={16} />アーカイブ</button></div>
    </div>
    <div className="maintenance-document-tabs" role="tablist" aria-label="整備書類の表示"><button id="maintenance-document-edit-tab" className={view === 'edit' ? 'is-active' : ''} type="button" role="tab" aria-selected={view === 'edit'} aria-controls="maintenance-document-edit-panel" onClick={() => onViewChange('edit')}><FileText size={16} />入力</button><button id="maintenance-document-preview-tab" className={view === 'preview' ? 'is-active' : ''} type="button" role="tab" aria-selected={view === 'preview'} aria-controls="maintenance-document-preview-panel" onClick={() => onViewChange('preview')}><Eye size={16} />プレビュー</button></div>
    {view === 'edit'
      ? <div id="maintenance-document-edit-panel" className="maintenance-detail-content" role="tabpanel" aria-labelledby="maintenance-document-edit-tab"><MaintenanceDocumentEditor document={document} isDraft={isDraft} customers={customers} defaultDueDate={addDaysDisplay(settings.document.defaultDueDays)} onUpdateHeader={onUpdateHeader} onUpdateTaxRate={onUpdateTaxRate} /></div>
      : <div id="maintenance-document-preview-panel" className="maintenance-detail-content maintenance-preview-content" role="tabpanel" aria-labelledby="maintenance-document-preview-tab"><MaintenancePreview document={document} settings={settings} itemPresets={itemPresets} onUpdateHeader={onUpdateHeader} onUpdateDetails={onUpdateDetails} onUpdateItem={onUpdateItem} onRemoveItem={onRemoveItem} onUpdateFee={onUpdateFee} onAddItem={onAddItem} /></div>}
  </section>
}

type MaintenanceItemField = MaintenanceStatementItemField
type MaintenanceDocumentDetailProps = {
  document: MaintenanceDocumentLike
  isDraft: boolean
  draftDirty: boolean
  customers: Customer[]
  settings: AppSettings
  itemPresets: string[]
  view: MaintenanceDocumentView
  saving: boolean
  saved: boolean
  onViewChange: (view: MaintenanceDocumentView) => void
  onUpdateHeader: (field: MaintenanceHeaderField, value: string) => void
  onUpdateTaxRate: (value: number) => void
  onUpdateDetails: (details: MaintenanceDocumentDetails) => void
  onSave: () => void
  onArchive: () => void
  onPdfDownload: () => void
  onPdfPreview: () => void
  onUpdateItem: (itemId: string, field: MaintenanceItemField, value: string) => void
  onAddItem: () => void
  onRemoveItem: (itemId: string) => void
  onUpdateFee: (key: MaintenanceFeeKey, value: string) => void
}

function MaintenanceDocumentEditor({ document, isDraft, customers, defaultDueDate, onUpdateHeader, onUpdateTaxRate }: { document: MaintenanceDocumentLike; isDraft: boolean; customers: Customer[]; defaultDueDate: string; onUpdateHeader: (field: MaintenanceHeaderField, value: string) => void; onUpdateTaxRate: (value: number) => void }) {
  const selectedCustomer = customers.find((customer) => customer.id === document.customerId)
  return <>
    <section className="document-header-editor maintenance-input-panel"><div className="document-header-editor-title"><div><h3>整備書類基本情報</h3><span>書類種別、顧客・車両、入庫日・出庫予定日などの基本情報を入力できます。</span></div></div><div className="form-grid"><label className="form-field"><span>書類種別</span><select value={document.type} onChange={(event) => onUpdateHeader('type', event.target.value)}>{maintenanceDocumentTypeOptions.map((type) => <option key={type}>{type}</option>)}</select></label><label className="form-field"><span>状態</span><select value={document.status} onChange={(event) => onUpdateHeader('status', event.target.value)}>{maintenanceStatusOptions.map((status) => <option key={status}>{status}</option>)}</select></label><label className="form-field"><span>入庫区分</span><select value={document.category} onChange={(event) => onUpdateHeader('category', event.target.value)}>{maintenanceCategoryOptions.map((category) => <option key={category}>{category}</option>)}</select></label><label className="form-field"><span>顧客</span><select value={document.customerId ?? ''} disabled={isDraft} onChange={(event) => onUpdateHeader('customerId', event.target.value)}>{isDraft && !document.customerId && <option value="">新規顧客（書類本体で入力）</option>}{customers.map((customer) => <option key={customer.id} value={customer.id}>{customer.name}</option>)}</select></label><label className="form-field"><span>対象車両</span><select value={document.vehicleId ?? ''} disabled={isDraft} onChange={(event) => onUpdateHeader('vehicleId', event.target.value)}>{!isDraft && document.abacusImport?.vehicleless && <option value="">ABACUS互換：車両なし</option>}{isDraft && !document.vehicleId && <option value="">新規車両（書類本体で入力）</option>}{selectedCustomer?.vehicles.map((vehicle) => <option key={vehicle.id} value={vehicle.id}>{vehicle.maker} {vehicle.model} ・ {vehicle.plate || '登録番号なし'}</option>)}</select>{!isDraft && document.vehicleId === null && !document.abacusImport?.vehicleless && <small className="form-field-hint">通常のWeb書類は車両必須です。</small>}</label><label className="form-field"><span>書類日付</span><input type="date" value={document.issuedAt.replaceAll('/', '-')} onChange={(event) => onUpdateHeader('issuedAt', event.target.value.replaceAll('-', '/'))} /></label><label className="form-field"><span>入庫日</span><input type="date" value={document.intakeDate.replaceAll('/', '-')} onChange={(event) => onUpdateHeader('intakeDate', event.target.value.replaceAll('-', '/'))} /></label><label className="form-field"><span>出庫予定日</span><input type="date" value={document.plannedReleaseDate.replaceAll('/', '-')} onChange={(event) => onUpdateHeader('plannedReleaseDate', event.target.value.replaceAll('-', '/'))} /></label><OptionalDateField id="maintenance-due-date" label="支払期限" value={document.dueDate} defaultValue={defaultDueDate} onChange={(value) => onUpdateHeader('dueDate', value)} /></div></section>
    <details className="maintenance-details-accordion"><summary><span>詳細設定</span><ChevronDown size={16} aria-hidden="true" /></summary><div className="maintenance-details-accordion-content"><DocumentTaxSettings documentId={document.id} taxRate={Math.round(document.taxRate * 100)} onTaxRateChange={onUpdateTaxRate} /></div></details>
  </>
}

function MaintenancePreview({ document, settings, itemPresets, onUpdateHeader, onUpdateDetails, onUpdateItem, onRemoveItem, onUpdateFee, onAddItem }: { document: MaintenanceDocumentLike; settings: AppSettings; itemPresets: string[]; onUpdateHeader: (field: MaintenanceHeaderField, value: string) => void; onUpdateDetails: (details: MaintenanceDocumentDetails) => void; onUpdateItem: (itemId: string, field: MaintenanceItemField, value: string) => void; onRemoveItem: (itemId: string) => void; onUpdateFee: (key: MaintenanceFeeKey, value: string) => void; onAddItem: () => void }) {
  const svg = useMemo(() => buildMaintenanceStatementSvg(document, settings, { hideEditableValues: true }), [document, settings])
  return <div className="maintenance-preview-shell">
    {document.isAbacusMigration && <div className="abacus-detail-notice" role="status">ABACUS移行明細：元データの未入力項目は空欄で表示しています。{document.abacusDetailReport?.amountOnlyRowCount ? ` 金額のみの行 ${document.abacusDetailReport.amountOnlyRowCount}件。` : ''}{document.abacusDetailReport?.warning ? ` ${document.abacusDetailReport.warning}` : ''}</div>}
    <div className="maintenance-statement-frame"><div className="maintenance-statement"><div dangerouslySetInnerHTML={{ __html: svg }} /><MaintenanceStatementEditor document={document} itemPresets={itemPresets} onUpdateHeader={onUpdateHeader} onUpdateDetails={onUpdateDetails} onUpdateItem={onUpdateItem} onRemoveItem={onRemoveItem} onUpdateFee={onUpdateFee} onAddItem={onAddItem} /></div></div>
  </div>
}

function MaintenanceStatusTag({ status }: { status: MaintenanceStatus }) { const tone = status === '完了' ? 'normal' : status === '入金待ち' ? 'warning' : status === 'アーカイブ済み' ? 'danger' : 'open'; return <span className={`maintenance-status-tag maintenance-status-${tone}`}><span className="status-dot" />{status}</span> }

function MaintenanceDocumentTypeTag({ type }: { type: MaintenanceDocumentType }) { const tone = type === '整備請求書' ? 'invoice' : 'estimate'; return <span className={`maintenance-document-type-badge maintenance-document-type-${tone}`}>{type === '整備請求書' ? '請求書' : '見積書'}</span> }


function MaintenanceDocumentDialog({ form, customers, onChange, onClose, onSubmit }: { form: MaintenanceCreateForm; customers: Customer[]; onChange: (form: MaintenanceCreateForm) => void; onClose: () => void; onSubmit: (event: FormEvent<HTMLFormElement>) => void }) {
  const selectedCustomer = form.customerMode === 'existing' ? customers.find((customer) => customer.id === form.customerId) : undefined
  const vehicles = selectedCustomer?.vehicles ?? []
  const canStart = isValidCreateSelection(form)

  function selectCustomer(value: string) {
    if (value === NEW_CUSTOMER_VALUE) {
      onChange({ ...form, customerMode: 'new', customerId: '', vehicleMode: 'new', vehicleId: '' })
      return
    }
    if (!value || value === '__separator__') return
    onChange({ ...form, customerMode: 'existing', customerId: value, vehicleMode: null, vehicleId: '' })
  }

  function selectVehicle(value: string) {
    if (value === NEW_VEHICLE_VALUE) {
      onChange({ ...form, vehicleMode: 'new', vehicleId: '' })
      return
    }
    if (!value || value === '__separator__') return
    onChange({ ...form, vehicleMode: 'existing', vehicleId: value })
  }

  return <div className="modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose() }}><section className="modal" role="dialog" aria-modal="true" aria-labelledby="maintenance-modal-title"><div className="modal-header"><h2 id="maintenance-modal-title">整備書類を作成</h2><button className="modal-close" type="button" aria-label="閉じる" onClick={onClose}><X size={19} /></button></div><form className="modal-form" onSubmit={onSubmit}><p className="modal-description"><ClipboardCheck size={16} />入庫区分と顧客・車両を選択して入力を開始します。</p><div className="form-grid"><label className="form-field"><span>書類種別<em>必須</em></span><select required value={form.type} onChange={(event) => onChange({ ...form, type: event.target.value as MaintenanceDocumentType })}>{maintenanceDocumentTypeOptions.map((type) => <option key={type}>{type}</option>)}</select></label><label className="form-field"><span>入庫区分<em>必須</em></span><select required value={form.category} onChange={(event) => onChange({ ...form, category: event.target.value as IntakeCategory })}>{maintenanceCategoryOptions.map((category) => <option key={category}>{category}</option>)}</select></label><label className="form-field"><span>顧客<em>必須</em></span><select required autoFocus aria-label="顧客" value={form.customerMode === 'new' ? NEW_CUSTOMER_VALUE : form.customerId} onChange={(event) => selectCustomer(event.target.value)}><option value="" disabled hidden>顧客を選択してください</option><option value={NEW_CUSTOMER_VALUE}>＋ 新規顧客</option><option value="__separator__" disabled>────────────</option>{customers.map((customer) => <option key={customer.id} value={customer.id}>{customer.name}（{customer.phone || '電話番号未登録'}）</option>)}</select></label><label className="form-field"><span>車両<em>必須</em></span><select required aria-label="車両" value={form.vehicleMode === 'new' ? NEW_VEHICLE_VALUE : form.vehicleId} disabled={form.customerMode === null} onChange={(event) => selectVehicle(event.target.value)}><option value="" disabled hidden>車両を選択してください</option><option value={NEW_VEHICLE_VALUE}>＋ 新規車両</option>{form.customerMode === 'existing' && <><option value="__separator__" disabled>────────────</option>{vehicles.map((vehicle) => <option key={vehicle.id} value={vehicle.id}>{vehicle.maker} {vehicle.model}{vehicle.plate ? `（${vehicle.plate}）` : ''}</option>)}</>}</select></label></div><div className="modal-footer"><button className="button button-secondary" type="button" onClick={onClose}>キャンセル</button><button className="button button-primary" type="submit" disabled={!canStart}><Plus size={16} />入力を開始</button></div></form></section></div>
}

function buildMaintenanceDraftSyncPreviewInput(document: MaintenanceDocumentLike, context: MaintenanceDraftContext): SyncPreviewInput {
  validateMaintenanceDraftContext(document, context)
  const customerValues = maintenanceCustomerValuesForSave(document)
  const input: SyncPreviewInput = {
    documentType: 'maintenance',
    issuedAt: normalizeMaintenanceDocumentDate(document.issuedAt),
    openedCustomerUpdatedAt: context.customerMode === 'existing' ? context.customerUpdatedAt ?? undefined : undefined,
    openedVehicleUpdatedAt: context.vehicleMode === 'existing' ? context.vehicleUpdatedAt ?? undefined : undefined,
  }

  if (context.customerMode === 'new') {
    input.newCustomer = buildNewMaintenanceCustomer(customerValues)
  } else {
    if (!document.customerId) throw new Error('既存顧客が選択されていません。')
    input.customerId = document.customerId
    input.customerOverride = customerValues
  }

  if (context.vehicleMode === 'new') {
    input.newVehicle = buildNewMaintenanceVehicle(currentMaintenanceVehicleValues(document))
  } else {
    if (!document.vehicleId) throw new Error('既存車両が選択されていません。')
    input.vehicleId = document.vehicleId
    if (document.details.vehicleOverride) input.vehicleOverride = { ...document.details.vehicleOverride }
    input.mileageContext = { openedMileage: context.openedMileage }
  }

  return input
}

function buildMaintenanceCreateInput(document: MaintenanceDocumentLike, context: MaintenanceDraftContext, duplicateConfirmation?: MaintenanceDuplicateConfirmation, masterSync?: MaintenanceMasterSync, mileageSync?: MaintenanceMileageSync): MaintenanceDocumentInput {
  validateMaintenanceDraftContext(document, context)
  const customerValues = maintenanceCustomerValuesForSave(document)
  const input: MaintenanceDocumentInput = {
    type: document.type,
    status: document.status,
    category: document.category,
    issuedAt: document.issuedAt,
    intakeDate: document.intakeDate,
    plannedReleaseDate: document.plannedReleaseDate,
    completionDate: document.completionDate,
    dueDate: document.dueDate,
    taxRate: document.taxRate,
    taxRounding: document.taxRounding,
    fees: document.fees,
    adjustment: document.adjustment,
    note: document.note,
    details: {
      ...document.details,
      customerBirthDate: customerValues.birthDate,
      customerEmployer: customerValues.employer,
      ...(context.customerMode === 'existing' ? { customerOverride: customerValues } : {}),
    },
    items: document.items.map(({ id: _id, ...item }) => item),
  }

  if (context.customerMode === 'new') {
    input.newCustomer = buildNewMaintenanceCustomer(customerValues)
  } else {
    if (!document.customerId) throw new Error('顧客を選択してください。')
    input.customerId = document.customerId
  }

  if (context.vehicleMode === 'new') {
    input.newVehicle = buildNewMaintenanceVehicle(currentMaintenanceVehicleValues(document))
  } else {
    if (!document.vehicleId) throw new Error('車両を選択してください。')
    input.vehicleId = document.vehicleId
  }

  if (duplicateConfirmation) input.duplicateConfirmation = duplicateConfirmation
  if (masterSync) input.masterSync = masterSync
  if (mileageSync) input.mileageSync = mileageSync
  return input
}

function validateMaintenanceDraftContext(document: MaintenanceDocumentLike, context: MaintenanceDraftContext) {
  if (context.customerMode === 'new' && context.vehicleMode === 'existing') throw new Error('新規顧客には既存車両を指定できません。')
  if (context.customerMode === 'existing' && (!document.customerId || document.customerId !== context.customerId)) throw new Error('顧客の選択状態を確認してください。')
  if (context.customerMode === 'new' && document.customerId) throw new Error('新規顧客の選択状態を確認してください。')
  if (context.vehicleMode === 'existing' && (!document.vehicleId || document.vehicleId !== context.vehicleId)) throw new Error('車両の選択状態を確認してください。')
  if (context.vehicleMode === 'new' && document.vehicleId) throw new Error('新規車両の選択状態を確認してください。')
}

function buildMaintenanceMasterSync(result: MasterSyncConfirmationResult, preview: SyncPreviewResponse): MaintenanceMasterSync | undefined {
  if (result.customerFields.length === 0 && result.vehicleFields.length === 0) return undefined
  return {
    confirmed: true,
    customerFields: result.customerFields,
    vehicleFields: result.vehicleFields,
    expectedCustomerUpdatedAt: result.customerFields.length > 0 ? preview.expectedCustomerUpdatedAt ?? undefined : undefined,
    expectedVehicleUpdatedAt: result.vehicleFields.length > 0 ? preview.expectedVehicleUpdatedAt ?? undefined : undefined,
  }
}

function buildMaintenanceMileageSync(document: MaintenanceDocumentLike, context: MaintenanceDraftContext, preview: SyncPreviewResponse): MaintenanceMileageSync | undefined {
  if (context.vehicleMode !== 'existing' || !preview.mileageDiff?.isChanged) return undefined
  const inputMileage = parseMileageString(currentMaintenanceVehicleValues(document).mileage)
  if (inputMileage === null) throw new Error('走行距離を入力してください。')
  return { confirmed: true, openedMileage: context.openedMileage, inputMileage }
}

function currentMaintenanceCustomerValues(document: MaintenanceDocumentLike): NonNullable<MaintenanceDocumentDetails['customerOverride']> {
  const override = document.details.customerOverride
  const base = { ...document.customerDetails, ...(override ?? {}) }
  return {
    ...base,
    birthDate: normalizeMaintenanceCustomerBirthDate(document.details.customerBirthDate || override?.birthDate || document.customerDetails.birthDate),
    employer: normalizeMaintenanceCustomerEmployer(document.details.customerEmployer || override?.employer || document.customerDetails.employer),
  }
}

function maintenanceCustomerValuesForSave(document: MaintenanceDocumentLike): NonNullable<MaintenanceDocumentDetails['customerOverride']> {
  const values = currentMaintenanceCustomerValues(document)
  return { ...values, birthDate: normalizeMaintenanceCustomerBirthDateOnBlur(values.birthDate) }
}

function currentMaintenanceVehicleValues(document: MaintenanceDocumentLike): NonNullable<MaintenanceDocumentDetails['vehicleOverride']> {
  return { ...emptyMaintenanceVehicleDetails(), ...(document.vehicleDetails ?? {}), ...(document.details.vehicleOverride ?? {}) }
}

function buildNewMaintenanceCustomer(values: NonNullable<MaintenanceDocumentInput['newCustomer']> & Partial<MaintenanceCustomerDetails>): NonNullable<MaintenanceDocumentInput['newCustomer']> {
  const name = values.name.trim()
  if (!name) throw new Error('顧客名を入力してください。')
  return {
    name,
    nameKana: trimMaintenanceOptional(values.kana),
    phone: trimMaintenanceOptional(values.phone),
    email: trimMaintenanceOptional(values.email),
    postalCode: trimMaintenanceOptional(values.postalCode),
    address: trimMaintenanceOptional(values.address),
    birthDate: trimMaintenanceOptional(values.birthDate),
    employer: trimMaintenanceOptional(values.employer),
  }
}

function buildNewMaintenanceVehicle(values: NonNullable<MaintenanceDocumentDetails['vehicleOverride']>): NonNullable<MaintenanceDocumentInput['newVehicle']> {
  const maker = values.maker.trim()
  const name = values.name.trim()
  if (!maker) throw new Error('メーカーを入力してください。')
  if (!name) throw new Error('車名を入力してください。')

  const vehicle: NonNullable<MaintenanceDocumentInput['newVehicle']> = { maker, name }
  const model = trimMaintenanceOptional(values.modelType)
  const registrationNumber = trimMaintenanceOptional(values.plate)
  const chassisNumber = trimMaintenanceOptional(values.vin)
  const modelYear = parseMaintenanceNumber(values.year)
  const inspectionDate = normalizeMaintenanceDocumentDate(values.inspectionDate)
  const mileage = parseMaintenanceNumber(values.mileage)
  const bodyColor = trimMaintenanceOptional(values.color)
  const displacement = parseMaintenanceNumber(values.displacement)
  const transmission = trimMaintenanceOptional(values.transmission)
  if (model) vehicle.model = model
  if (registrationNumber) vehicle.registrationNumber = registrationNumber
  if (chassisNumber) vehicle.chassisNumber = chassisNumber
  if (modelYear !== undefined) vehicle.modelYear = modelYear
  if (inspectionDate) vehicle.inspectionDate = inspectionDate
  if (mileage !== undefined) vehicle.mileage = mileage
  if (bodyColor) vehicle.bodyColor = bodyColor
  if (displacement !== undefined) vehicle.displacement = displacement
  if (transmission) vehicle.transmission = transmission
  return vehicle
}

function emptyMaintenanceVehicleDetails(): MaintenanceVehicleDetails {
  return { maker: '', name: '', modelType: '', plate: '', vin: '', year: '', inspectionDate: '', mileage: '', color: '', displacement: '', transmission: '', inspectionRecordAvailable: false }
}

function trimMaintenanceOptional(value: string | undefined | null) {
  const trimmed = value?.trim() ?? ''
  return trimmed || undefined
}

function normalizeMaintenanceCustomerBirthDate(value: string | null | undefined) {
  const normalized = typeof value === 'string' ? value.trim() : ''
  return normalized === 'birth_date' ? '' : normalized
}

function normalizeMaintenanceCustomerBirthDateOnBlur(value: string) {
  return normalizeMaintenanceCustomerBirthDate(value).replaceAll('-', '/')
}

function normalizeMaintenanceCustomerEmployer(value: string | null | undefined) {
  const normalized = typeof value === 'string' ? value.normalize('NFKC').trim() : ''
  return normalized === 'employer' ? '' : normalized
}

function normalizeMaintenanceDocumentDate(value: string) {
  const trimmed = value.trim()
  return trimmed ? trimmed.replaceAll('/', '-') : undefined
}

function parseMaintenanceNumber(value: string | undefined | null) {
  const trimmed = value?.trim() ?? ''
  if (!trimmed) return undefined
  const digits = trimmed.replace(/[^0-9]/g, '')
  if (!digits) return undefined
  const parsed = Number(digits)
  return Number.isFinite(parsed) && parsed >= 0 ? parsed : undefined
}

function toMaintenanceInput(document: MaintenanceDocument, mileageSync?: MaintenanceMileageSync): MaintenanceDocumentInput {
  const customerValues = maintenanceCustomerValuesForSave(document)
  return {
    number: document.number,
    type: document.type,
    status: document.status,
    category: document.category,
    customerId: document.customerId,
    vehicleId: document.vehicleId ?? undefined,
    issuedAt: document.issuedAt,
    intakeDate: document.intakeDate,
    plannedReleaseDate: document.plannedReleaseDate,
    completionDate: document.completionDate,
    dueDate: document.dueDate,
    taxRate: document.taxRate,
    taxRounding: document.taxRounding,
    fees: document.fees,
    adjustment: document.adjustment,
    note: document.note,
    details: { ...document.details, customerBirthDate: customerValues.birthDate, customerEmployer: customerValues.employer, customerOverride: customerValues },
    items: document.items.map(({ id: _id, ...item }) => item),
    mileageSync,
  }
}
function updateMaintenanceHeader(document: MaintenanceDocument, field: MaintenanceHeaderField, value: string, customers: Customer[]): MaintenanceDocument {
  if (field !== 'customerId' && field !== 'vehicleId') return { ...document, [field]: value }

  const nextCustomer = customers.find((customer) => customer.id === (field === 'customerId' ? value : document.customerId))
  const nextVehicleId = field === 'customerId' ? nextCustomer?.vehicles[0]?.id ?? '' : value
  const nextVehicle = nextCustomer?.vehicles.find((vehicle) => vehicle.id === nextVehicleId)
  const nextDetails = field === 'customerId'
    ? { ...document.details, customerBirthDate: normalizeMaintenanceCustomerBirthDate(nextCustomer?.birthDate), customerEmployer: normalizeMaintenanceCustomerEmployer(nextCustomer?.employer), customerOverride: null, vehicleOverride: null }
    : { ...document.details, vehicleOverride: null }

  return {
    ...document,
    [field]: value,
    customerId: field === 'customerId' ? value : document.customerId,
    vehicleId: nextVehicleId,
    customerName: nextCustomer?.name ?? '',
    phone: nextCustomer?.phone ?? '',
    customerDetails: mapMaintenanceCustomerDetails(nextCustomer),
    vehicle: nextVehicle ? [nextVehicle.maker, nextVehicle.model].filter(Boolean).join(' ') : '',
    plate: nextVehicle?.plate ?? '',
    mileage: nextVehicle?.mileage ?? '',
    vehicleDetails: mapMaintenanceVehicleDetails(nextVehicle),
    details: nextDetails,
  }
}

function mapMaintenanceCustomerDetails(customer: Customer | undefined): MaintenanceCustomerDetails {
  return {
    name: customer?.name ?? '',
    kana: customer?.kana ?? '',
    phone: customer?.phone ?? '',
    email: customer?.email ?? '',
    postalCode: customer?.postalCode ?? '',
    address: customer?.address ?? '',
    birthDate: normalizeMaintenanceCustomerBirthDate(customer?.birthDate),
    employer: normalizeMaintenanceCustomerEmployer(customer?.employer),
  }
}

function mapMaintenanceVehicleDetails(vehicle: Customer['vehicles'][number] | undefined): MaintenanceVehicleDetails | null {
  if (!vehicle) return null
  return {
    maker: vehicle.maker,
    name: vehicle.model,
    modelType: vehicle.modelType,
    plate: vehicle.plate,
    vin: vehicle.vin,
    year: vehicle.year,
    inspectionDate: vehicle.inspectionDate,
    mileage: vehicle.mileage,
    color: vehicle.color,
    displacement: vehicle.displacement,
    transmission: vehicle.transmission,
    inspectionRecordAvailable: vehicle.inspectionRecordAvailable,
  }
}
function todayDisplay() { return new Date().toISOString().slice(0, 10).replaceAll('-', '/') }
function addDaysDisplay(days: number) { const date = new Date(); date.setDate(date.getDate() + days); return date.toISOString().slice(0, 10).replaceAll('-', '/') }
function parseMileageString(value: string | undefined | null): number | null {
  if (!value) return null
  const digits = value.replace(/[^0-9]/g, '')
  if (!digits) return null
  const parsed = Number(digits)
  return Number.isFinite(parsed) ? parsed : null
}

function isValidCreateSelection(form: MaintenanceCreateForm) {
  if (form.customerMode === 'new') return form.vehicleMode === 'new'
  if (form.customerMode !== 'existing') return false
  if (!form.customerId) return false
  return form.vehicleMode === 'new' || (form.vehicleMode === 'existing' && Boolean(form.vehicleId))
}
