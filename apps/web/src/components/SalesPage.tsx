import { useEffect, useMemo, useRef, useState, type ChangeEvent, type CSSProperties, type FormEvent } from 'react'
import { normalizeDisplacement, normalizeMileage, normalizeModelYear, normalizePhone, normalizePostalCode, type NormalizableField } from '@vehicle-management/shared'
import {
  Archive,
  CarFront,
  ChevronLeft,
  ChevronDown,
  ChevronRight,
  Eye,
  FileDown,
  FileText,
  Image as ImageIcon,
  Plus,
  RefreshCw,
  Save,
  Search,
  Trash2,
  UserRound,
  X,
} from 'lucide-react'
import { fetchCustomerDetail, fetchCustomerSummaries, fetchVehicleFile, type Customer, type Vehicle } from '../lib/customerApi'
import { fetchSyncPreview, type SyncPreviewInput, type SyncPreviewResponse } from '../lib/masterSyncApi'
import { downloadSalesDocumentPdf, previewSalesDocumentPdf } from '../lib/pdf'
import {
  createSalesDocument,
  archiveSalesDocument,
  fetchSalesDocument,
  fetchSalesDocumentSummaries,
  updateSalesDocument,
  type SalesDocument,
  type SalesDocumentLike,
  type SalesDocumentDetails,
  type SalesDocumentType,
  type SalesStatus,
  type SalesLineItem,
  type SalesTaxCategory,
  type SalesCreateInput,
  type SalesDocumentInput,
  defaultSalesDocumentDetails,
} from '../lib/salesApi'
import { defaultSettings, fetchSettings, type AppSettings, type SalesItemPresetGroupKey, type SalesItemPresetGroups } from '../lib/settingsApi'
import { buildSalesEstimateSections, calculateSalesEstimateTotals, calculateSalesLineAmount, type SalesEstimateEditableBucket, type SalesEstimateSections, type SalesTotals } from '../lib/salesEstimate'
import { buildSalesEstimateSheetSvg, salesEstimateSheetLayout } from '../lib/salesEstimateSheet'
import { DocumentFilterGroup, type DocumentFilterOption } from './DocumentFilterGroup'
import { compareSortableDocuments, type DocumentSortDirection, type DocumentSortKey } from './DocumentSort'
import { DocumentSortControls } from './DocumentSortControls'
import { DocumentTaxSettings } from './DocumentTaxSettings'
import { DateCalendarButton } from './DateCalendarButton'
import { sanitizeNormalizedDraft, toEditableNormalizedValue } from './normalizedInput'
import { toNativeDateValue } from './dateInput'
import { MasterSyncConfirmationDialog, type MasterSyncConfirmationResult } from './MasterSyncConfirmationDialog'
import { OptionalDateField } from './OptionalDateField'
import { SalesDuplicateConfirmationDialog, type SalesDuplicateDialogState } from './SalesDuplicateConfirmationDialog'
import { AbacusLinkProvenance } from './AbacusLinkProvenance'

type DocumentFilter = 'すべて' | SalesDocumentType
type SalesStatusFilter = 'すべて' | Exclude<SalesStatus, 'アーカイブ済み'>
type SalesDocumentView = 'edit' | 'preview'
type CompletedSalesGroup = { key: string; label: string; documents: SalesDocument[] }
type SalesHeaderField = 'number' | 'type' | 'status' | 'customerId' | 'vehicleId' | 'issuedAt' | 'dueDate' | 'note'
type SalesItemField = 'itemType' | 'description' | 'quantity' | 'unit' | 'unitPrice' | 'taxCategory' | 'otherAmount' | 'summary'
type SalesTaxCategoryField = keyof SalesDocumentDetails['requiredDocuments']

type SalesMasterSnapshot =
  | { state: 'loading' }
  | { state: 'ready'; customerId: string; customerUpdatedAt: string; vehicleId: string | null; vehicleUpdatedAt: string | null }
  | { state: 'invalid' }

type SalesDraftContext = {
  customerMode: 'existing' | 'new'
  vehicleMode: 'existing' | 'new'
  customerId: string | null
  customerUpdatedAt: string | null
  vehicleId: string | null
  vehicleUpdatedAt: string | null
}

type SalesDuplicateConfirmation = NonNullable<SalesCreateInput['duplicateConfirmation']>
type SalesMasterSync = NonNullable<SalesCreateInput['masterSync']>

const salesDocumentTypeFilterOptions: DocumentFilterOption<DocumentFilter>[] = [
  { value: 'すべて', label: 'すべて', tone: 'all' },
  { value: '見積書', label: '見積書', tone: 'estimate' },
  { value: '請求書', label: '請求書', tone: 'invoice' },
]
const salesStatusFilterOptions: DocumentFilterOption<SalesStatusFilter>[] = [
  { value: 'すべて', label: 'すべて', tone: 'all' },
  { value: '下書き', label: '下書き', tone: 'draft' },
  { value: '入金待ち', label: '入金待ち', tone: 'pending' },
  { value: '完了', label: '完了', tone: 'completed' },
]
const salesTaxCategories: SalesTaxCategory[] = ['課税', '非課税', '対象外']
const sheetYenFormatter = new Intl.NumberFormat('ja-JP')
const requiredDocumentFields: Array<{ key: keyof SalesDocumentDetails['requiredDocuments']; label: string }> = [
  { key: 'sealCertificate', label: '印鑑証明' },
  { key: 'selfDeclaration', label: '自認書・承諾書' },
  { key: 'residentCard', label: '住民票' },
  { key: 'powerOfAttorney', label: '委任状' },
  { key: 'lightVehicleCertificate', label: '軽自動車住所証明' },
  { key: 'transferCertificate', label: '譲渡証明' },
  { key: 'taxPaymentCertificate', label: '納税証明（下取車）' },
  { key: 'guarantorSealCertificate', label: '保証人印鑑証明' },
]

const estimateBucketDefaults: Record<SalesEstimateEditableBucket, { itemType: string; label: string; taxCategory: SalesTaxCategory }> = {
  vehicleBase: { itemType: '車両本体価格', label: '車両本体価格', taxCategory: '課税' },
  discounts: { itemType: '値引き', label: '値引等', taxCategory: '課税' },
  accessories: { itemType: '付属品・特別仕様', label: '付属品・特別仕様', taxCategory: '課税' },
  vehicleSideLabor: { itemType: '車両販売工賃', label: '工賃', taxCategory: '課税' },
  legalNonTaxable: { itemType: '法定費用', label: '法定費用', taxCategory: '非課税' },
  taxableFees: { itemType: '手続代行費用', label: '手続代行費用', taxCategory: '課税' },
  nonTaxableFees: { itemType: '実費・預託金', label: '実費・預託金', taxCategory: '非課税' },
  tradeIns: { itemType: '下取車', label: '下取車価格', taxCategory: '対象外' },
}

type SalesCreateForm = {
  type: SalesDocumentType
  customerMode: 'existing' | 'new' | null
  customerId: string
  vehicleMode: 'existing' | 'new' | null
  vehicleId: string
}

const NEW_CUSTOMER_VALUE = '__new_customer__'
const NEW_VEHICLE_VALUE = '__new_vehicle__'
type SalesDraftState = SalesDocumentLike | null

export function SalesPage({ initialDocumentId }: { initialDocumentId?: string } = {}) {
  const [documents, setDocuments] = useState<SalesDocument[]>([])
  const [customers, setCustomers] = useState<Customer[]>([])
  const [settings, setSettings] = useState<AppSettings>(defaultSettings)
  const [query, setQuery] = useState('')
  const [sortKey, setSortKey] = useState<DocumentSortKey>('dueDate')
  const [sortDirection, setSortDirection] = useState<DocumentSortDirection>('asc')
  const [filterType, setFilterType] = useState<DocumentFilter>('すべて')
  const [statusFilter, setStatusFilter] = useState<SalesStatusFilter>('すべて')
  const [selectedDocumentId, setSelectedDocumentId] = useState(initialDocumentId ?? '')
  const [mobileWorkspaceView, setMobileWorkspaceView] = useState<'list' | 'detail'>(initialDocumentId ? 'detail' : 'list')
  const [createDialogOpen, setCreateDialogOpen] = useState(false)
  const [createForm, setCreateForm] = useState<SalesCreateForm>(emptyCreateForm())
  const [draftDocument, setDraftDocument] = useState<SalesDraftState>(null)
  const [loading, setLoading] = useState(true)
  const [documentNextCursor, setDocumentNextCursor] = useState<string | null>(null)
  const [documentHasMore, setDocumentHasMore] = useState(false)
  const [loadingMoreDocuments, setLoadingMoreDocuments] = useState(false)
  const [loadingDocumentDetailId, setLoadingDocumentDetailId] = useState('')
  const [syncError, setSyncError] = useState('')
  const [saving, setSaving] = useState(false)
  const [dirty, setDirty] = useState(false)
  const [saved, setSaved] = useState(false)
  const [documentView, setDocumentView] = useState<SalesDocumentView>('edit')
  const [masterSyncDialogResult, setMasterSyncDialogResult] = useState<SyncPreviewResponse | null>(null)
  const [salesDuplicateDialog, setSalesDuplicateDialog] = useState<SalesDuplicateDialogState | null>(null)
  const [pendingDraftPreview, setPendingDraftPreview] = useState<SyncPreviewResponse | null>(null)
  const [pendingDraftDuplicateConfirmation, setPendingDraftDuplicateConfirmation] = useState<SalesDuplicateConfirmation | undefined>(undefined)
  const documentsRef = useRef<SalesDocument[]>([])
  const draftDocumentRef = useRef<SalesDocumentLike | null>(null)
  const draftContextRef = useRef<SalesDraftContext | null>(null)
  const draftCustomerDuplicateConfirmedRef = useRef(false)
  const openedMasterSnapshotRef = useRef<SalesMasterSnapshot | null>(null)
  const lastOpenedDocumentIdRef = useRef<string | null>(null)
  const summaryFilterInitializedRef = useRef(false)
  const initialSortRef = useRef({ sortKey, sortDirection })

  function replaceDocuments(updater: (current: SalesDocument[]) => SalesDocument[]) {
    const nextDocuments = updater(documentsRef.current)
    documentsRef.current = nextDocuments
    setDocuments(nextDocuments)
  }

  useEffect(() => {
    let cancelled = false
    setLoading(true)
    Promise.all([fetchSalesDocumentSummaries({ limit: 100, sortKey: initialSortRef.current.sortKey, sortDirection: initialSortRef.current.sortDirection }), fetchCustomerSummaries({ limit: 100 }), fetchSettings()])
      .then(([documentPage, customerPage, nextSettings]) => {
        if (cancelled) return
        const nextDocuments = documentPage.documents
        const nextCustomers = customerPage.customers.map(mapCustomerSummaryToRecord)
        const detailedInitialDocument = documentsRef.current.find((document) => document.id === initialDocumentId && !document.isSummary)
        const documentsWithInitialDetail = detailedInitialDocument ? [detailedInitialDocument, ...nextDocuments.filter((document) => document.id !== detailedInitialDocument.id)] : nextDocuments
        documentsRef.current = documentsWithInitialDetail
        setDocuments(documentsWithInitialDetail)
        setDocumentNextCursor(documentPage.nextCursor)
        setDocumentHasMore(documentPage.hasMore)
        setCustomers(nextCustomers)
        setSettings(nextSettings)
        const nextSelectedDocumentId = initialDocumentId ?? documentsWithInitialDetail[0]?.id ?? ''
        setSelectedDocumentId(nextSelectedDocumentId)
        if (nextSelectedDocumentId) setMobileWorkspaceView('detail')
        setSyncError('')
      })
      .catch((error: unknown) => {
        if (!cancelled) setSyncError(error instanceof Error ? error.message : '販売書類を読み込めませんでした。')
      })
      .finally(() => {
        if (!cancelled) setLoading(false)
      })
    return () => { cancelled = true }
  }, [initialDocumentId])

  useEffect(() => {
    if (!summaryFilterInitializedRef.current) {
      summaryFilterInitializedRef.current = true
      return
    }
    let active = true
    const timer = window.setTimeout(() => {
      void fetchSalesDocumentSummaries({ q: query, type: filterType, status: statusFilter, sortKey, sortDirection, limit: 100 }).then((page) => {
        if (!active) return
        documentsRef.current = page.documents
        setDocuments(page.documents)
        setDocumentNextCursor(page.nextCursor)
        setDocumentHasMore(page.hasMore)
        setSelectedDocumentId((current) => current && page.documents.some((document) => document.id === current) ? current : page.documents[0]?.id ?? '')
      }).catch((reason: unknown) => {
        if (active) setSyncError(reason instanceof Error ? reason.message : '販売書類を検索できませんでした。')
      })
    }, query.trim() ? 280 : 0)
    return () => { active = false; window.clearTimeout(timer) }
  }, [filterType, query, sortDirection, sortKey, statusFilter])

  async function loadMoreDocuments() {
    if (!documentHasMore || !documentNextCursor || loadingMoreDocuments) return
    setLoadingMoreDocuments(true)
    try {
      const page = await fetchSalesDocumentSummaries({ q: query, type: filterType, status: statusFilter, sortKey, sortDirection, cursor: documentNextCursor, limit: 100 })
      replaceDocuments((current) => [...current, ...page.documents.filter((document) => !current.some((item) => item.id === document.id))])
      setDocumentNextCursor(page.nextCursor)
      setDocumentHasMore(page.hasMore)
    } catch (reason: unknown) {
      setSyncError(reason instanceof Error ? reason.message : '販売書類を追加で読み込めませんでした。')
    } finally {
      setLoadingMoreDocuments(false)
    }
  }

  const filteredDocuments = useMemo(() => {
    const normalizedQuery = query.trim().toLocaleLowerCase()
    return documents.filter((document) => {
      const matchesType = filterType === 'すべて' || document.type === filterType
      const matchesStatus = statusFilter === 'すべて' || document.status === statusFilter
      const searchableText = `${document.number} ${document.customerName} ${document.vehicle} ${document.plate}`.toLocaleLowerCase()
      return matchesType && matchesStatus && (!normalizedQuery || searchableText.includes(normalizedQuery))
    }).sort((left, right) => compareSortableDocuments(left, right, sortKey, sortDirection))
  }, [documents, filterType, query, sortDirection, sortKey, statusFilter])

  const incompleteDocuments = useMemo(() => filteredDocuments.filter((document) => document.status === '下書き' || document.status === '入金待ち'), [filteredDocuments])
  const completedGroups = useMemo(() => groupCompletedSalesDocuments(filteredDocuments.filter((document) => document.status === '完了')), [filteredDocuments])

  const selectedPersistedDocument = filteredDocuments.find((document) => document.id === selectedDocumentId) ?? (initialDocumentId ? null : filteredDocuments[0] ?? null)
  const selectedDocument: SalesDocumentLike | null = draftDocument ?? selectedPersistedDocument
  const selectedTotals = selectedDocument ? calculateSalesEstimateTotals(selectedDocument) : null

  useEffect(() => {
    if (!initialDocumentId || documents.some((document) => document.id === initialDocumentId)) return
    let active = true
    void fetchSalesDocument(initialDocumentId).then((detail) => {
      if (!active) return
      documentsRef.current = [detail, ...documentsRef.current]
      setDocuments((current) => [detail, ...current])
      setSelectedDocumentId(detail.id)
    }).catch((reason: unknown) => {
      if (active) setSyncError(reason instanceof Error ? reason.message : '指定された販売書類を読み込めませんでした。')
    })
    return () => { active = false }
  }, [documents, initialDocumentId])

  useEffect(() => {
    const target = selectedPersistedDocument
    if (!target?.isSummary || loadingDocumentDetailId === target.id) return
    let active = true
    setLoadingDocumentDetailId(target.id)
    void fetchSalesDocument(target.id).then((detail) => {
      if (!active) return
      replaceDocuments((current) => current.map((document) => document.id === detail.id ? detail : document))
    }).catch((reason: unknown) => {
      if (active) setSyncError(reason instanceof Error ? reason.message : '販売書類の詳細を読み込めませんでした。')
    }).finally(() => {
      if (active) setLoadingDocumentDetailId((current) => current === target.id ? '' : current)
    })
    return () => { active = false }
  }, [loadingDocumentDetailId, selectedPersistedDocument, selectedPersistedDocument?.id, selectedPersistedDocument?.isSummary])

  useEffect(() => {
    const customerId = selectedPersistedDocument?.customerId
    if (!customerId) return
    const current = customers.find((customer) => customer.id === customerId)
    if (!current?.isSummary) return
    let active = true
    void fetchCustomerDetail(customerId).then((detail) => {
      if (active) setCustomers((items) => items.map((customer) => customer.id === detail.id ? detail : customer))
    }).catch((reason: unknown) => {
      if (active) setSyncError(reason instanceof Error ? reason.message : '顧客情報を読み込めませんでした。')
    })
    return () => { active = false }
  }, [selectedPersistedDocument?.customerId, customers])

  useEffect(() => {
    const customerId = createForm.customerMode === 'existing' ? createForm.customerId : ''
    if (!customerId) return
    const current = customers.find((customer) => customer.id === customerId)
    if (!current?.isSummary) return
    let active = true
    void fetchCustomerDetail(customerId).then((detail) => {
      if (active) setCustomers((items) => items.map((customer) => customer.id === detail.id ? detail : customer))
    }).catch(() => undefined)
    return () => { active = false }
  }, [createForm.customerId, createForm.customerMode, customers])

  function setActiveDraft(nextDraft: SalesDocumentLike | null) {
    draftDocumentRef.current = nextDraft
    setDraftDocument(nextDraft)
  }

  function replaceActiveDocument(updater: (document: SalesDocumentLike) => SalesDocumentLike) {
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
    replaceDocuments((current) => current.map((document) => document.id === selectedPersistedDocument.id ? updater(document) as SalesDocument : document))
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
    if (dirty && !window.confirm(`入力中の未保存書類を破棄して${action}しますか？`)) return false
    setActiveDraft(null)
    draftContextRef.current = null
    draftCustomerDuplicateConfirmedRef.current = false
    setDirty(false)
    setSaved(false)
    setMasterSyncDialogResult(null)
    setSalesDuplicateDialog(null)
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

  // Initialize openedMasterSnapshot when document changes
  useEffect(() => {
    if (draftDocument) {
      lastOpenedDocumentIdRef.current = null
      openedMasterSnapshotRef.current = null
      return
    }
    const currentDocumentId = selectedPersistedDocument?.id ?? null
    if (currentDocumentId === lastOpenedDocumentIdRef.current) return
    lastOpenedDocumentIdRef.current = currentDocumentId
    if (!selectedPersistedDocument) {
      openedMasterSnapshotRef.current = null
      return
    }
    const foundCustomer = customers.find((c) => c.id === selectedPersistedDocument.customerId)
    const foundVehicle = selectedPersistedDocument.vehicleId ? foundCustomer?.vehicles.find((v) => v.id === selectedPersistedDocument.vehicleId) : null
    if (foundCustomer) {
      openedMasterSnapshotRef.current = {
        state: 'ready',
        customerId: foundCustomer.id,
        customerUpdatedAt: foundCustomer.updatedAt,
        vehicleId: selectedPersistedDocument.vehicleId,
        vehicleUpdatedAt: foundVehicle?.updatedAt ?? null,
      }
    } else {
      openedMasterSnapshotRef.current = { state: 'loading' }
    }
  }, [draftDocument, selectedPersistedDocument, customers])

  function updateLineItem(itemId: string, field: SalesItemField, value: string) {
    if (!selectedDocument) return
    const nextValue = field === 'description' || field === 'itemType' || field === 'unit' || field === 'taxCategory' || field === 'summary' ? value : Number(value)
    replaceActiveDocument((document) => ({
      ...document,
      items: document.items.map((item) => item.id === itemId ? { ...item, [field]: nextValue, abacusDetail: null, isAbacusMigration: false } : item),
    }))
    markDirty()
  }

  function updateEstimateSheetLine(bucket: SalesEstimateEditableBucket, index: number, patch: { label?: string; amount?: number }) {
    if (!selectedDocument) return
    const defaults = estimateBucketDefaults[bucket]
    replaceActiveDocument((document) => {
      const line = buildSalesEstimateSections(document)[bucket][index]
      if (line) {
        const nextLabel = patch.label ?? line.label
        const nextAmount = patch.amount ?? line.amount
        if (line.id === 'recycle-fee') {
          if (patch.label !== undefined && !patch.label.trim()) return { ...document, details: { ...document.details, recycleFee: 0 } }
          if (patch.label !== undefined && patch.label.trim() !== line.label) {
            const item: SalesLineItem = { id: `item-${Date.now()}-${bucket}-${index}`, itemType: defaults.itemType, description: patch.label.trim(), quantity: 1, unit: '式', unitPrice: nextAmount, taxCategory: defaults.taxCategory, otherAmount: 0, summary: '' }
            return { ...document, details: { ...document.details, recycleFee: 0 }, items: [...document.items, item] }
          }
          return { ...document, details: { ...document.details, recycleFee: nextAmount } }
        }
        if ((patch.label !== undefined && !patch.label.trim()) || (!nextLabel.trim() && nextAmount === 0)) return { ...document, items: document.items.filter((item) => item.id !== line.id) }
        return {
          ...document,
          items: document.items.map((item) => item.id === line.id ? {
            ...item,
            abacusDetail: null,
            isAbacusMigration: false,
            itemType: defaults.itemType,
            description: nextLabel,
            quantity: 1,
            unit: item.unit || '式',
            unitPrice: nextAmount,
            otherAmount: 0,
            taxCategory: defaults.taxCategory,
          } : item),
        }
      }
      const nextLabel = patch.label?.trim() || defaults.label
      const nextAmount = patch.amount ?? 0
      if (!patch.label?.trim() && patch.amount === undefined) return document
      const newItem: SalesLineItem = {
        id: `item-${Date.now()}-${bucket}-${index}`,
        itemType: defaults.itemType,
        description: nextLabel,
        quantity: 1,
        unit: '式',
        unitPrice: nextAmount,
        taxCategory: defaults.taxCategory,
        otherAmount: 0,
        summary: '',
      }
      return { ...document, items: [...document.items, newItem] }
    })
    markDirty()
  }

  function addLineItem() {
    if (!selectedDocument) return
    const newItem: SalesLineItem = { id: `item-${Date.now()}`, itemType: 'その他', description: '', quantity: 1, unit: '式', unitPrice: 0, taxCategory: '課税', otherAmount: 0, summary: '' }
    replaceActiveDocument((document) => ({ ...document, items: [...document.items, newItem] }))
    markDirty()
  }

  function updateHeader(field: SalesHeaderField, value: string) {
    if (!selectedDocument || field === 'number') return
    if (draftDocument && (field === 'customerId' || field === 'vehicleId')) return
    const nextCustomer = customers.find((customer) => customer.id === (field === 'customerId' ? value : selectedDocument.customerId))
    const nextVehicleId = field === 'customerId' ? nextCustomer?.vehicles[0]?.id ?? null : field === 'vehicleId' ? value || null : selectedDocument.vehicleId
    const nextVehicle = nextCustomer?.vehicles.find((vehicle) => vehicle.id === nextVehicleId)
    const relationChanged = field === 'customerId' || field === 'vehicleId'
    const relationPatch = relationChanged ? {
      customerName: nextCustomer?.name ?? '',
      phone: nextCustomer?.phone ?? '',
      vehicleId: nextVehicleId,
      vehicle: nextVehicle ? `${nextVehicle.maker} ${nextVehicle.model}`.trim() : '',
      plate: nextVehicle?.plate ?? '',
      customerDetails: nextCustomer ? mapCustomerDetails(nextCustomer) : emptyCustomerDetails(),
      vehicleDetails: nextVehicle ? mapVehicleDetails(nextVehicle) : null,
      details: { ...selectedDocument.details, selectedImageAttachmentId: '', customerOverride: null, vehicleOverride: null },
    } : {}
    replaceActiveDocument((document) => ({ ...document, [field]: value, ...relationPatch }))
    markDirty()
  }

  function updateDetails(patch: Partial<SalesDocumentDetails>) {
    if (!selectedDocument) return
    replaceActiveDocument((document) => ({ ...document, details: { ...document.details, ...patch } }))
    markDirty()
  }

  function updateTaxRate(value: number) {
    if (!selectedDocument) return
    replaceActiveDocument((document) => ({ ...document, taxRate: value / 100 }))
    markDirty()
  }

  function updateTradeIn(field: keyof SalesDocumentDetails['tradeIn'], value: string) {
    if (!selectedDocument) return
    updateDetails({ tradeIn: { ...selectedDocument.details.tradeIn, [field]: value } })
  }

  function updateCredit(field: keyof SalesDocumentDetails['credit'], value: string | boolean) {
    if (!selectedDocument) return
    const nextValue = typeof value === 'boolean' || field === 'paymentCount' || field === 'bonusMonths' ? value : Number(value)
    updateDetails({ credit: { ...selectedDocument.details.credit, [field]: nextValue } })
  }

  function updateRequiredDocument(field: SalesTaxCategoryField, value: string | boolean) {
    if (!selectedDocument) return
    updateDetails({ requiredDocuments: { ...selectedDocument.details.requiredDocuments, [field]: value, ...(field === 'selfDeclaration' ? { warrantyCertificate: value === true } : {}) } })
  }

  function markDirty() {
    if (draftDocumentRef.current) {
      draftCustomerDuplicateConfirmedRef.current = false
      setMasterSyncDialogResult(null)
      setSalesDuplicateDialog(null)
      setPendingDraftPreview(null)
      setPendingDraftDuplicateConfirmation(undefined)
    }
    setDirty(true)
    setSaved(false)
  }

  async function archiveSelectedDocument() {
    if (draftDocument || !selectedPersistedDocument || saving) return
    if (!window.confirm(`${selectedPersistedDocument.number}をアーカイブしますか？`)) return
    setSaving(true)
    setSyncError('')
    try {
      await archiveSalesDocument(selectedPersistedDocument.id)
      replaceDocuments((current) => current.filter((document) => document.id !== selectedPersistedDocument.id))
      setSelectedDocumentId('')
      setDirty(false)
      setSaved(false)
    } catch (error: unknown) {
      setSyncError(error instanceof Error ? error.message : '販売書類をアーカイブできませんでした。')
    } finally {
      setSaving(false)
    }
  }

  function removeLineItem(itemId: string) {
    if (!selectedDocument) return
    replaceActiveDocument((document) => ({ ...document, items: document.items.filter((item) => item.id !== itemId) }))
    markDirty()
  }

  async function saveSelectedDocument(masterSync?: { confirmed: true; customerFields: string[]; vehicleFields: string[]; expectedCustomerUpdatedAt?: string; expectedVehicleUpdatedAt?: string }) {
    if (draftDocument || !selectedPersistedDocument || saving) return
    if (openedMasterSnapshotRef.current?.state === 'invalid') {
      setSyncError('最新の顧客・車両情報を確認できないため保存できません。画面を再読み込みしてください。')
      return
    }
    const documentToSave = documentsRef.current.find((document) => document.id === selectedPersistedDocument.id)
    if (!documentToSave) return
    setSaving(true)
    setSaved(false)
    try {
      const customerValues = salesCustomerValuesForSave(documentToSave)
      const input: SalesDocumentInput = {
        type: documentToSave.type,
        number: documentToSave.number,
        status: documentToSave.status,
        customerId: documentToSave.customerId,
        vehicleId: documentToSave.vehicleId,
        issuedAt: documentToSave.issuedAt,
        dueDate: documentToSave.dueDate,
        taxRate: documentToSave.taxRate,
        taxRounding: documentToSave.taxRounding,
        note: documentToSave.note,
        details: { ...documentToSave.details, customerBirthDate: customerValues.birthDate, customerEmployer: customerValues.employer, customerOverride: customerValues },
        items: documentToSave.items.map(({ id: _id, ...item }) => item),
        masterSync,
      }
      const nextDocument = await updateSalesDocument(documentToSave.id, input)
      replaceDocuments((current) => current.map((document) => document.id === nextDocument.id ? nextDocument : document))
      setDirty(false)
      setSaved(true)
      setSyncError('')

      // Re-fetch customers to get latest updatedAt
      try {
        const foundCustomer = await fetchCustomerDetail(selectedPersistedDocument.customerId)
        setCustomers((current) => upsertCustomer(current, foundCustomer))
        const foundVehicle = selectedPersistedDocument.vehicleId ? foundCustomer?.vehicles.find((v) => v.id === selectedPersistedDocument.vehicleId) : null
        if (foundCustomer) {
          openedMasterSnapshotRef.current = {
            state: 'ready',
            customerId: foundCustomer.id,
            customerUpdatedAt: foundCustomer.updatedAt,
            vehicleId: selectedPersistedDocument.vehicleId,
            vehicleUpdatedAt: foundVehicle?.updatedAt ?? null,
          }
        } else {
          openedMasterSnapshotRef.current = { state: 'loading' }
        }
      } catch {
        setSyncError('書類は保存されましたが、最新の顧客・車両情報を再取得できませんでした。画面を再読み込みしてください。')
        openedMasterSnapshotRef.current = { state: 'invalid' }
      }
    } catch (error: unknown) {
      if (error instanceof Error && error.message.includes('顧客または車両情報が更新されました')) {
        setSyncError('顧客または車両情報が更新されました。再読み込み後にもう一度保存してください。')
      } else {
        setSyncError(error instanceof Error ? error.message : '販売書類を保存できませんでした。')
      }
    } finally {
      setSaving(false)
    }
  }

  function openCreateDialog() {
    if (!discardDraftIfConfirmed('新しい書類を作成')) return
    setCreateForm({ type: '見積書', customerMode: null, customerId: '', vehicleMode: null, vehicleId: '' })
    setCreateDialogOpen(true)
  }

  function startDraft(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!isValidCreateSelection(createForm)) return
    const customer = createForm.customerMode === 'existing' ? customers.find((item) => item.id === createForm.customerId) : undefined
    const vehicle = createForm.vehicleMode === 'existing' ? customer?.vehicles.find((item) => item.id === createForm.vehicleId) : undefined
    const draft: SalesDocumentLike = {
      number: '未採番',
      type: createForm.type,
      status: '下書き',
      customerId: customer?.id ?? null,
      customerName: customer?.name ?? '',
      phone: customer?.phone ?? '',
      vehicleId: vehicle?.id ?? null,
      vehicle: vehicle ? `${vehicle.maker} ${vehicle.model}`.trim() : '',
      plate: vehicle?.plate ?? '',
      customerDetails: customer ? mapCustomerDetails(customer) : emptyCustomerDetails(),
      vehicleDetails: vehicle ? mapVehicleDetails(vehicle) : null,
      details: structuredClone(defaultSalesDocumentDetails),
      issuedAt: todaySalesDisplay(),
      dueDate: '',
      taxRate: settings.tax.consumptionTaxRate / 100,
      taxRounding: settings.tax.rounding,
      note: '',
      archivedAt: null,
      archivedPreviousStatus: null,
      archivedBy: null,
      purgeAt: null,
      keepForever: false,
      items: [{ id: 'draft-item-1', itemType: 'その他', description: settings.salesItemPresets[0] ?? '車両本体価格', quantity: 1, unit: '式', unitPrice: 0, taxCategory: '課税', otherAmount: 0, summary: '' }],
    }
    draftContextRef.current = {
      customerMode: createForm.customerMode!,
      vehicleMode: createForm.vehicleMode!,
      customerId: customer?.id ?? null,
      customerUpdatedAt: customer?.updatedAt ?? null,
      vehicleId: vehicle?.id ?? null,
      vehicleUpdatedAt: vehicle?.updatedAt ?? null,
    }
    draftCustomerDuplicateConfirmedRef.current = false
    setActiveDraft(draft)
    setSelectedDocumentId('')
    setDirty(false)
    setSaved(false)
    setMasterSyncDialogResult(null)
    setSalesDuplicateDialog(null)
    setPendingDraftPreview(null)
    setPendingDraftDuplicateConfirmation(undefined)
    setSyncError('')
    setDocumentView('edit')
    openMobileDetail()
    setCreateDialogOpen(false)
  }

  async function handleSaveClick() {
    if (saving || !selectedDocument) return
    if (draftDocument) {
      const currentDraft = draftDocumentRef.current ?? draftDocument
      const context = draftContextRef.current
      if (!context) {
        setSyncError('未保存書類の顧客・車両情報を確認できません。書類を開き直してください。')
        return
      }
      setSyncError('')
      setSaving(true)
      await runDraftSyncPreview(currentDraft, context)
      return
    }
    if (!selectedPersistedDocument) return
    if (openedMasterSnapshotRef.current?.state === 'invalid') {
      setSyncError('最新の顧客・車両情報を確認できないため保存できません。画面を再読み込みしてください。')
      return
    }

    const snapshot = openedMasterSnapshotRef.current
    try {
      const preview = await fetchSyncPreview({
        documentType: 'sales',
        documentId: selectedPersistedDocument.id,
        customerId: selectedPersistedDocument.customerId || undefined,
        vehicleId: selectedPersistedDocument.vehicleId || undefined,
        customerOverride: salesCustomerValuesForSave(selectedPersistedDocument),
        vehicleOverride: selectedPersistedDocument.details.vehicleOverride ?? undefined,
        issuedAt: selectedPersistedDocument.issuedAt.replaceAll('/', '-'),
        openedCustomerUpdatedAt: snapshot?.state === 'ready' ? snapshot.customerUpdatedAt : undefined,
        openedVehicleUpdatedAt: snapshot?.state === 'ready' ? snapshot.vehicleUpdatedAt ?? undefined : undefined,
      })

      const hasDiffs = preview.customerDiffs.length > 0 || preview.vehicleDiffs.length > 0
      if (hasDiffs) {
        setMasterSyncDialogResult(preview)
        return
      }

      // No differences - save directly
      void saveSelectedDocument()
    } catch (reason) {
      setSyncError(reason instanceof Error ? reason.message : '同期プレビューの取得に失敗しました。')
    }
  }

  async function runDraftSyncPreview(document: SalesDocumentLike, context: SalesDraftContext, duplicateConfirmation?: SalesDuplicateConfirmation) {
    try {
      const preview = await fetchSyncPreview(buildSalesDraftSyncPreviewInput(document, context))
      await processDraftSyncPreview(preview, duplicateConfirmation)
    } catch (reason) {
      setSaving(false)
      setSyncError(reason instanceof Error ? reason.message : '同期プレビューの取得に失敗しました。')
    }
  }

  async function processDraftSyncPreview(preview: SyncPreviewResponse, duplicateConfirmation?: SalesDuplicateConfirmation) {
    const context = draftContextRef.current
    if (!context) {
      setSaving(false)
      setSyncError('未保存書類の顧客・車両情報を確認できません。書類を開き直してください。')
      return
    }

    setPendingDraftPreview(preview)
    setPendingDraftDuplicateConfirmation(duplicateConfirmation)

    const duplicateCustomers = preview.duplicateCustomers ?? []
    if (context.customerMode === 'new' && !draftCustomerDuplicateConfirmedRef.current && duplicateCustomers.length > 0) {
      setSaving(false)
      setSalesDuplicateDialog({ kind: 'customer', candidates: duplicateCustomers })
      return
    }

    const chassisCandidates = (preview.duplicateVehicles ?? []).filter((candidate) => candidate.matchReason === 'chassis_number')
    if (chassisCandidates.length > 0) {
      setSaving(false)
      setSalesDuplicateDialog({ kind: 'vehicle', matchReason: 'chassis_number', candidates: chassisCandidates })
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
      setSalesDuplicateDialog({ kind: 'vehicle', matchReason: 'registration_number', candidates: registrationCandidates })
      return
    }

    const hasMasterDiffs = preview.customerDiffs.length > 0 || preview.vehicleDiffs.length > 0
    if (hasMasterDiffs) {
      setMasterSyncDialogResult(preview)
      setSaving(false)
      return
    }

    const currentDraft = draftDocumentRef.current
    if (!currentDraft) {
      setSaving(false)
      setSyncError('未保存書類が見つかりません。')
      return
    }
    setPendingDraftPreview(null)
    await createDraftDocument(currentDraft, context, duplicateConfirmation)
  }

  async function createDraftDocument(document: SalesDocumentLike, context: SalesDraftContext, duplicateConfirmation?: SalesDuplicateConfirmation, masterSync?: SalesMasterSync) {
    setSaving(true)
    setSyncError('')
    try {
      const currentDraft = draftDocumentRef.current ?? document
      const currentContext = draftContextRef.current ?? context
      const input = buildSalesCreateInput(currentDraft, currentContext, duplicateConfirmation, masterSync)
      const nextDocument = await createSalesDocument(input)
      replaceDocuments((current) => [nextDocument, ...current])
      setSelectedDocumentId(nextDocument.id)
      setActiveDraft(null)
      draftContextRef.current = null
      draftCustomerDuplicateConfirmedRef.current = false
      setDirty(false)
      setSaved(true)
      setMasterSyncDialogResult(null)
      setSalesDuplicateDialog(null)
      setPendingDraftPreview(null)
      setPendingDraftDuplicateConfirmation(undefined)
      setDocumentView('edit')

      try {
        const foundCustomer = await fetchCustomerDetail(nextDocument.customerId)
        setCustomers((current) => upsertCustomer(current, foundCustomer))
        lastOpenedDocumentIdRef.current = nextDocument.id
        const foundVehicle = nextDocument.vehicleId ? foundCustomer?.vehicles.find((vehicle) => vehicle.id === nextDocument.vehicleId) : null
        if (foundCustomer && (!nextDocument.vehicleId || foundVehicle)) {
          openedMasterSnapshotRef.current = {
            state: 'ready',
            customerId: foundCustomer.id,
            customerUpdatedAt: foundCustomer.updatedAt,
            vehicleId: nextDocument.vehicleId,
            vehicleUpdatedAt: foundVehicle?.updatedAt ?? null,
          }
        } else {
          openedMasterSnapshotRef.current = { state: 'invalid' }
        }
      } catch {
        setSyncError('書類は保存されましたが、最新の顧客・車両情報を再取得できませんでした。画面を再読み込みしてください。')
        lastOpenedDocumentIdRef.current = nextDocument.id
        openedMasterSnapshotRef.current = { state: 'invalid' }
      }
    } catch (reason) {
      setPendingDraftDuplicateConfirmation(undefined)
      setSyncError(reason instanceof Error ? reason.message : '販売書類を保存できませんでした。')
    } finally {
      setSaving(false)
    }
  }

  async function handleUseExistingCustomer(customerId: string) {
    const currentDraft = draftDocumentRef.current
    const context = draftContextRef.current
    if (!currentDraft || !context || context.customerMode !== 'new') return

    setSaving(true)
    setSyncError('')
    try {
      let nextCustomers = customers
      let customer = nextCustomers.find((item) => item.id === customerId)
      if (!customer) {
        customer = await fetchCustomerDetail(customerId)
        setCustomers((current) => upsertCustomer(current, customer!))
      }
      if (!customer) throw new Error('選択した既存顧客を確認できません。顧客一覧を再読み込みしてください。')

      const nextDraft: SalesDocumentLike = {
        ...currentDraft,
        customerId: customer.id,
        customerName: customer.name,
        phone: customer.phone,
        customerDetails: mapCustomerDetails(customer),
      }
      const nextContext: SalesDraftContext = {
        ...context,
        customerMode: 'existing',
        customerId: customer.id,
        customerUpdatedAt: customer.updatedAt,
      }
      draftContextRef.current = nextContext
      draftCustomerDuplicateConfirmedRef.current = false
      setActiveDraft(nextDraft)
      setMasterSyncDialogResult(null)
      setSalesDuplicateDialog(null)
      setPendingDraftPreview(null)
      setPendingDraftDuplicateConfirmation(undefined)
      await runDraftSyncPreview(nextDraft, nextContext)
    } catch (reason) {
      setSaving(false)
      setSyncError(reason instanceof Error ? reason.message : '既存顧客への切り替えに失敗しました。')
    }
  }

  async function handleContinueAsNewCustomer() {
    const preview = pendingDraftPreview
    if (!preview || !draftContextRef.current) return
    draftCustomerDuplicateConfirmedRef.current = true
    setSalesDuplicateDialog(null)
    setPendingDraftPreview(null)
    setSaving(true)
    setPendingDraftDuplicateConfirmation(undefined)
    await processDraftSyncPreview(preview)
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
      setSyncError('選択した既存車両を確認できません。顧客一覧を再読み込みしてください。')
      return
    }

    const nextDraft: SalesDocumentLike = {
      ...currentDraft,
      vehicleId: vehicle.id,
      vehicle: `${vehicle.maker} ${vehicle.model}`.trim(),
      plate: vehicle.plate,
      vehicleDetails: mapVehicleDetails(vehicle),
    }
    const nextContext: SalesDraftContext = {
      ...context,
      vehicleMode: 'existing',
      vehicleId: vehicle.id,
      vehicleUpdatedAt: vehicle.updatedAt,
    }
    draftContextRef.current = nextContext
    draftCustomerDuplicateConfirmedRef.current = false
    setActiveDraft(nextDraft)
    setMasterSyncDialogResult(null)
    setSalesDuplicateDialog(null)
    setPendingDraftPreview(null)
    setPendingDraftDuplicateConfirmation(undefined)
    setSaving(true)
    setSyncError('')
    await runDraftSyncPreview(nextDraft, nextContext)
  }

  async function handleContinueAsNewVehicle(vehicleId: string) {
    const preview = pendingDraftPreview
    const isCurrentRegistrationCandidate = preview?.duplicateVehicles?.some((candidate) => candidate.id === vehicleId && candidate.matchReason === 'registration_number')
    if (!preview || !isCurrentRegistrationCandidate) {
      setSyncError('登録番号の重複候補が更新されています。もう一度保存してください。')
      setSaving(false)
      return
    }
    const duplicateConfirmation: SalesDuplicateConfirmation = { registrationNumberConfirmed: true, confirmedVehicleId: vehicleId }
    setSalesDuplicateDialog(null)
    setPendingDraftPreview(null)
    setSaving(true)
    await processDraftSyncPreview(preview, duplicateConfirmation)
  }

  function handleSalesDuplicateCancel() {
    setSalesDuplicateDialog(null)
    setPendingDraftPreview(null)
    setPendingDraftDuplicateConfirmation(undefined)
    setSaving(false)
  }

  function handleMasterSyncCancel() {
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
        setSyncError('未保存書類の保存状態を確認できません。')
        return
      }
      const masterSync = buildSalesMasterSync(result, preview)
      const duplicateConfirmation = pendingDraftDuplicateConfirmation
      setPendingDraftPreview(null)
      setPendingDraftDuplicateConfirmation(undefined)
      void createDraftDocument(draftDocumentRef.current, context, duplicateConfirmation, masterSync)
      return
    }
    let masterSync: { confirmed: true; customerFields: string[]; vehicleFields: string[]; expectedCustomerUpdatedAt?: string; expectedVehicleUpdatedAt?: string } | undefined
    if (result.customerFields.length > 0 || result.vehicleFields.length > 0) {
      masterSync = {
        confirmed: true,
        customerFields: result.customerFields,
        vehicleFields: result.vehicleFields,
        expectedCustomerUpdatedAt: result.customerFields.length > 0 ? (preview?.expectedCustomerUpdatedAt ?? undefined) : undefined,
        expectedVehicleUpdatedAt: result.vehicleFields.length > 0 ? (preview?.expectedVehicleUpdatedAt ?? undefined) : undefined,
      }
    }
    void saveSelectedDocument(masterSync)
  }

  return (
    <>
      <div className="page-header sales-page-header"><div><span className="page-eyebrow">販売書類</span><h1>販売</h1><p>見積書・請求書を車両情報と連動して管理します。</p></div><button className="button button-primary" type="button" onClick={openCreateDialog}><Plus size={18} />販売書類を作成</button></div>
      {syncError && <div className="customer-sync-status is-error"><span>{syncError}</span><button className="text-button" type="button" onClick={() => window.location.reload()}>再読み込み</button></div>}
      {loading && <div className="customer-sync-status"><span>販売書類を読み込んでいます。</span></div>}
      <div className="sales-toolbar"><label className="sales-search"><Search size={18} /><span className="sr-only">販売書類を検索</span><input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="書類番号、顧客名、車名で検索" /></label><DocumentSortControls sortKey={sortKey} sortDirection={sortDirection} onSortKeyChange={setSortKey} onSortDirectionChange={setSortDirection} /></div>
      <div className="document-filter-panel sales-document-filter-panel mobile-filter-panel"><DocumentFilterGroup label="書類種別" value={filterType} options={salesDocumentTypeFilterOptions} onChange={setFilterType} /><DocumentFilterGroup label="状態" value={statusFilter} options={salesStatusFilterOptions} onChange={setStatusFilter} /><button className="text-button document-filter-reset" type="button" onClick={() => { setFilterType('すべて'); setStatusFilter('すべて') }} disabled={filterType === 'すべて' && statusFilter === 'すべて'}>条件をリセット</button></div>
      <div className={`sales-workspace mobile-workspace mobile-workspace-${mobileWorkspaceView}`}><div className="mobile-workspace-list"><SalesDocumentList incompleteDocuments={incompleteDocuments} completedGroups={completedGroups} selectedDocumentId={draftDocument ? '' : selectedPersistedDocument?.id ?? ''} onSelect={selectPersistedDocument} hasMore={documentHasMore} loadingMore={loadingMoreDocuments} onLoadMore={() => void loadMoreDocuments()} /></div><div className="mobile-workspace-detail"><button className="mobile-workspace-back" type="button" onClick={openMobileList}><ChevronLeft size={16} />販売書類一覧へ戻る</button>{selectedDocument && selectedTotals ? <SalesDocumentDetail document={selectedDocument} isDraft={!selectedDocument.id} totals={selectedTotals} shopName={settings.shop.name} settings={settings} itemPresets={settings.salesItemPresets} customers={customers} view={documentView} dirty={dirty} saving={saving} saved={saved} onViewChange={setDocumentView} onUpdateHeader={updateHeader} onUpdateDetails={updateDetails} onUpdateTaxRate={updateTaxRate} onUpdateTradeIn={updateTradeIn} onUpdateCredit={updateCredit} onUpdateRequiredDocument={updateRequiredDocument} onUpdateItem={updateLineItem} onUpdateSheetLine={updateEstimateSheetLine} onAddItem={addLineItem} onRemoveItem={removeLineItem} onSave={handleSaveClick} onArchive={() => void archiveSelectedDocument()} onPdfDownload={() => { if (selectedPersistedDocument) void downloadSalesDocumentPdf(selectedPersistedDocument, settings) }} onPdfPreview={() => { if (selectedPersistedDocument) void previewSalesDocumentPdf(selectedPersistedDocument, settings) }} /> : <div className="panel sales-empty"><FileText size={30} /><strong>{loading ? '販売書類を読み込んでいます' : '販売書類が見つかりません'}</strong><span>{loading ? 'しばらくお待ちください。' : '検索条件または絞り込み条件を変更してください。'}</span></div>}</div></div>
      {createDialogOpen && <SalesDocumentDialog form={createForm} customers={customers} onChange={setCreateForm} onClose={() => setCreateDialogOpen(false)} onSubmit={startDraft} />}
      {salesDuplicateDialog && <SalesDuplicateConfirmationDialog state={salesDuplicateDialog} canUseExistingVehicle={canUseExistingVehicleForDraft} onUseExistingCustomer={(customerId) => { void handleUseExistingCustomer(customerId) }} onContinueAsNewCustomer={() => { void handleContinueAsNewCustomer() }} onUseExistingVehicle={(vehicleId) => { void handleUseExistingVehicle(vehicleId) }} onContinueAsNewVehicle={(vehicleId) => { void handleContinueAsNewVehicle(vehicleId) }} onCancel={handleSalesDuplicateCancel} />}
      {masterSyncDialogResult && <MasterSyncConfirmationDialog isOlderThanLatestDocument={masterSyncDialogResult.isOlderThanLatestDocument} customerDiffs={masterSyncDialogResult.customerDiffs} vehicleDiffs={masterSyncDialogResult.vehicleDiffs} mileageDiff={undefined} hasCustomerConflict={masterSyncDialogResult.customerDiffs.some((d) => d.isConflict)} hasVehicleConflict={masterSyncDialogResult.vehicleDiffs.some((d) => d.isConflict)} onConfirm={handleMasterSyncConfirm} onCancel={handleMasterSyncCancel} />}
    </>
  )
}

function SalesDocumentList({ incompleteDocuments, completedGroups, selectedDocumentId, onSelect, hasMore, loadingMore, onLoadMore }: { incompleteDocuments: SalesDocument[]; completedGroups: CompletedSalesGroup[]; selectedDocumentId: string; onSelect: (id: string) => void; hasMore: boolean; loadingMore: boolean; onLoadMore: () => void }) {
  return <div className="sales-list-stack">
    <section className="panel sales-list-panel">
      <div className="sales-list-header"><div><h2>販売書類（未完了）</h2><span>書類を選択すると詳細を表示します</span></div><span className="results-count">{incompleteDocuments.length}件</span></div>
      {incompleteDocuments.length > 0 ? <SalesDocumentCards documents={incompleteDocuments} selectedDocumentId={selectedDocumentId} onSelect={onSelect} /> : <div className="sales-list-empty">未完了の販売書類はありません。</div>}
    </section>
    {completedGroups.length > 0 && <section className="panel sales-list-panel sales-completed-panel">
      <div className="sales-list-header"><div><h2>完了書類</h2><span>書類の作成月ごとに表示します</span></div><span className="results-count">{completedGroups.reduce((total, group) => total + group.documents.length, 0)}件</span></div>
      <div className="sales-completed-groups">{completedGroups.map((group) => <details className="sales-completed-group" key={group.key}><summary><span>{group.label}</span><span className="results-count">{group.documents.length}件</span></summary><SalesDocumentCards documents={group.documents} selectedDocumentId={selectedDocumentId} onSelect={onSelect} /></details>)}</div>
    </section>}
    {hasMore && <button className="button button-secondary document-list-load-more" type="button" onClick={onLoadMore} disabled={loadingMore}>{loadingMore ? '読み込み中…' : '次の書類を読み込む'}</button>}
  </div>
}

function SalesDocumentCards({ documents, selectedDocumentId, onSelect }: { documents: SalesDocument[]; selectedDocumentId: string; onSelect: (id: string) => void }) {
  return <div className="sales-document-list">{documents.map((document) => <button className={`sales-document-card${document.id === selectedDocumentId ? ' is-selected' : ''}`} key={document.id} type="button" onClick={() => onSelect(document.id)}><div className="sales-card-top"><span className={`sales-type-badge sales-type-${document.type}`}>{document.type}</span><StatusTag status={document.status} />{document.abacusImport?.vehicleless && <span className="document-abacus-badge">ABACUS・車両なし</span>}<ChevronRight size={16} /></div><strong className="sales-card-number">{document.number}</strong><span className="sales-card-customer"><UserRound size={14} /><strong>{document.customerName}</strong></span><span className="sales-card-vehicle"><CarFront size={14} />{document.vehicle || '車両未指定'}{document.plate ? ` ・ ${document.plate}` : ''}</span><div className="sales-card-bottom"><span>{document.issuedAt}</span><strong>{formatYen(calculateTotals(document).total)}</strong></div></button>)}</div>
}

function groupCompletedSalesDocuments(documents: SalesDocument[]): CompletedSalesGroup[] {
  const grouped = new Map<string, { label: string; documents: SalesDocument[] }>()
  for (const document of documents) {
    const month = salesDocumentMonth(document.issuedAt)
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

function salesDocumentMonth(issuedAt: string) {
  const match = issuedAt.replaceAll('-', '/').match(/^(\d{4})\/(\d{1,2})/)
  if (!match) return { key: 'unknown', label: '年月不明（完了）' }
  const [, year, month] = match
  return { key: `${year}-${month.padStart(2, '0')}`, label: `${year}年${Number(month)}月（完了）` }
}

function SalesDocumentDetail({ document, isDraft, totals, shopName, settings, itemPresets, customers, view, dirty, saving, saved, onViewChange, onUpdateHeader, onUpdateDetails, onUpdateTaxRate, onUpdateTradeIn, onUpdateCredit, onUpdateRequiredDocument, onUpdateItem, onUpdateSheetLine, onAddItem, onRemoveItem, onSave, onArchive, onPdfDownload, onPdfPreview }: { document: SalesDocumentLike; isDraft: boolean; totals: SalesTotals; shopName: string; settings: AppSettings; itemPresets: string[]; customers: Customer[]; view: SalesDocumentView; dirty: boolean; saving: boolean; saved: boolean; onViewChange: (view: SalesDocumentView) => void; onUpdateHeader: (field: SalesHeaderField, value: string) => void; onUpdateDetails: (patch: Partial<SalesDocumentDetails>) => void; onUpdateTaxRate: (value: number) => void; onUpdateTradeIn: (field: keyof SalesDocumentDetails['tradeIn'], value: string) => void; onUpdateCredit: (field: keyof SalesDocumentDetails['credit'], value: string | boolean) => void; onUpdateRequiredDocument: (field: keyof SalesDocumentDetails['requiredDocuments'], value: string | boolean) => void; onUpdateItem: (itemId: string, field: SalesItemField, value: string) => void; onUpdateSheetLine: SalesPreviewProps['onUpdateSheetLine']; onAddItem: () => void; onRemoveItem: (itemId: string) => void; onSave: () => void; onArchive: () => void; onPdfDownload: () => void; onPdfPreview: () => void }) {
  return <section className="panel sales-detail-panel"><div className="sales-detail-header"><div className="sales-detail-title"><div><div className="sales-detail-badges"><span className={`sales-type-badge sales-type-${document.type}`}>{document.type}</span><StatusTag status={document.status} />{document.abacusImport?.vehicleless && <span className="document-abacus-badge">ABACUS・車両なし</span>}{isDraft && <span className="document-draft-badge">新規・未保存</span>}</div><h2>{document.id ? document.number : '未採番'}</h2><small>{document.issuedAt} 作成 ・ 発行元 {shopName}</small>{document.abacusImport?.vehicleless && <small className="document-abacus-source">ABACUS互換：顧客にのみ紐付く書類（{document.abacusImport.sourceLocation}）</small>}<AbacusLinkProvenance metadata={document.abacusImport} />{document.isAbacusMigration && <AbacusDetailSummary document={document} />}</div></div><div className="sales-detail-actions"><button className="button button-secondary" type="button" disabled={isDraft} onClick={onPdfPreview}><Eye size={16} />PDFで確認</button><button className="button button-secondary" type="button" disabled={!dirty || saving} onClick={onSave}><Save size={16} />{saving ? '保存中…' : saved ? '保存済み' : '保存'}</button><button className="button button-secondary" type="button" disabled={isDraft} onClick={onPdfDownload}><FileDown size={16} />PDF保存</button><button className="button button-danger" type="button" disabled={isDraft || saving} onClick={onArchive}><Archive size={16} />アーカイブ</button></div></div><div className="sales-document-tabs" role="tablist" aria-label="販売書類の表示"><button id="sales-document-edit-tab" className={view === 'edit' ? 'is-active' : ''} type="button" role="tab" aria-selected={view === 'edit'} aria-controls="sales-document-edit-panel" onClick={() => onViewChange('edit')}><FileText size={16} />入力</button><button id="sales-document-preview-tab" className={view === 'preview' ? 'is-active' : ''} type="button" role="tab" aria-selected={view === 'preview'} aria-controls="sales-document-preview-panel" onClick={() => onViewChange('preview')}><Eye size={16} />プレビュー</button></div>{view === 'edit' ? <div id="sales-document-edit-panel" className="sales-detail-content" role="tabpanel" aria-labelledby="sales-document-edit-tab"><SalesDocumentEditor document={document} isDraft={isDraft} totals={totals} itemPresets={itemPresets} customers={customers} defaultDueDate={dateAfter(settings.document.defaultDueDays)} onUpdateHeader={onUpdateHeader} onUpdateDetails={onUpdateDetails} onUpdateTaxRate={onUpdateTaxRate} onUpdateTradeIn={onUpdateTradeIn} onUpdateCredit={onUpdateCredit} onUpdateRequiredDocument={onUpdateRequiredDocument} onUpdateItem={onUpdateItem} onAddItem={onAddItem} onRemoveItem={onRemoveItem} /></div> : <div id="sales-document-preview-panel" className="sales-detail-content" role="tabpanel" aria-labelledby="sales-document-preview-tab"><SalesDocumentPreview document={document} isDraft={isDraft} totals={totals} settings={settings} itemPresets={itemPresets} customers={customers} onUpdateHeader={onUpdateHeader} onUpdateDetails={onUpdateDetails} onUpdateItem={onUpdateItem} onUpdateSheetLine={onUpdateSheetLine} onAddItem={onAddItem} onRemoveItem={onRemoveItem} /></div>}</section>
}

function SalesDocumentEditor(props: { document: SalesDocumentLike; isDraft: boolean; totals: SalesTotals; itemPresets: string[]; customers: Customer[]; defaultDueDate: string; onUpdateHeader: (field: SalesHeaderField, value: string) => void; onUpdateDetails: (patch: Partial<SalesDocumentDetails>) => void; onUpdateTaxRate: (value: number) => void; onUpdateTradeIn: (field: keyof SalesDocumentDetails['tradeIn'], value: string) => void; onUpdateCredit: (field: keyof SalesDocumentDetails['credit'], value: string | boolean) => void; onUpdateRequiredDocument: (field: keyof SalesDocumentDetails['requiredDocuments'], value: string | boolean) => void; onUpdateItem: (itemId: string, field: SalesItemField, value: string) => void; onAddItem: () => void; onRemoveItem: (itemId: string) => void }) {
  const { document, isDraft, customers, defaultDueDate, onUpdateHeader } = props
  const selectedCustomer = customers.find((customer) => customer.id === document.customerId)
  return <>
    <section className="document-header-editor">
      <div className="document-header-editor-title"><div><h3>書類基本情報</h3><span>顧客・車両、日付、状態などの基本情報を入力できます。</span></div></div>
      <div className="form-grid">
        <label className="form-field"><span>書類種別</span><select value={document.type} onChange={(event) => onUpdateHeader('type', event.target.value)}><option>見積書</option><option>請求書</option></select></label>
        <label className="form-field"><span>状態</span><select value={document.status} onChange={(event) => onUpdateHeader('status', event.target.value)}><option>下書き</option><option>入金待ち</option><option>完了</option></select></label>
         <label className="form-field"><span>顧客</span><select value={document.customerId ?? ''} disabled={isDraft} onChange={(event) => onUpdateHeader('customerId', event.target.value)}>{isDraft && !document.customerId && <option value="">新規顧客（書類本体で入力）</option>}{customers.map((customer) => <option key={customer.id} value={customer.id}>{customer.name}</option>)}</select></label>
         <label className="form-field"><span>対象車両</span><select value={document.vehicleId ?? ''} disabled={isDraft} onChange={(event) => onUpdateHeader('vehicleId', event.target.value)}>{!isDraft && document.abacusImport?.vehicleless && <option value="">ABACUS互換：車両なし</option>}{isDraft && !document.vehicleId && <option value="">新規車両（書類本体で入力）</option>}{selectedCustomer?.vehicles.map((vehicle) => <option key={vehicle.id} value={vehicle.id}>{vehicle.maker} {vehicle.model} ・ {vehicle.plate || '登録番号なし'}</option>)}</select>{!isDraft && document.vehicleId === null && !document.abacusImport?.vehicleless && <small className="form-field-hint">通常のWeb書類は車両必須です。</small>}</label>
        <label className="form-field"><span>書類日付</span><input type="date" value={document.issuedAt.replaceAll('/', '-')} onChange={(event) => onUpdateHeader('issuedAt', event.target.value.replaceAll('-', '/'))} /></label>
        <OptionalDateField id="sales-due-date" label="支払期限" value={document.dueDate} defaultValue={defaultDueDate} onChange={(value) => onUpdateHeader('dueDate', value)} />
      </div>
    </section>
    <details className="sales-details-accordion">
      <summary><span>詳細</span><ChevronDown size={16} aria-hidden="true" /></summary>
      <div className="sales-details-accordion-content"><DocumentTaxSettings documentId={document.id} taxRate={Math.round(document.taxRate * 100)} onTaxRateChange={props.onUpdateTaxRate} /></div>
    </details>
  </>
}

function SalesDocumentPreview({ document, isDraft, totals, settings, itemPresets, customers, onUpdateHeader, onUpdateDetails, onUpdateItem, onUpdateSheetLine, onAddItem, onRemoveItem }: { document: SalesDocumentLike; isDraft: boolean; totals: SalesTotals; settings: AppSettings; itemPresets: string[]; customers: Customer[]; onUpdateHeader: (field: SalesHeaderField, value: string) => void; onUpdateDetails: (patch: Partial<SalesDocumentDetails>) => void; onUpdateItem: (itemId: string, field: SalesItemField, value: string) => void; onUpdateSheetLine: SalesPreviewProps['onUpdateSheetLine']; onAddItem: () => void; onRemoveItem: (itemId: string) => void }) {
  return <SalesEstimatePreview document={document} isDraft={isDraft} totals={totals} settings={settings} itemPresets={itemPresets} customers={customers} onUpdateHeader={onUpdateHeader} onUpdateDetails={onUpdateDetails} onUpdateItem={onUpdateItem} onUpdateSheetLine={onUpdateSheetLine} onAddItem={onAddItem} onRemoveItem={onRemoveItem} />
}

type SalesPreviewProps = {
  document: SalesDocumentLike
  isDraft: boolean
  totals: SalesTotals
  settings: AppSettings
  itemPresets: string[]
  customers: Customer[]
  onUpdateHeader: (field: SalesHeaderField, value: string) => void
  onUpdateDetails: (patch: Partial<SalesDocumentDetails>) => void
  onUpdateItem: (itemId: string, field: SalesItemField, value: string) => void
  onUpdateSheetLine: (bucket: SalesEstimateEditableBucket, index: number, patch: { label?: string; amount?: number }) => void
  onAddItem: () => void
  onRemoveItem: (itemId: string) => void
  onPdfPreview?: () => void
}

function SalesEstimatePreview(props: SalesPreviewProps) {
  return <SalesEstimateExactPreview {...props} />
}

function SalesEstimateExactPreview({ document, isDraft, customers, onUpdateHeader, onUpdateDetails, onUpdateSheetLine, settings }: SalesPreviewProps) {
  const selectedCustomer = customers.find((customer) => customer.id === document.customerId)
  const selectedVehicle = selectedCustomer?.vehicles.find((vehicle) => vehicle.id === document.vehicleId)
  const imageAttachments = selectedVehicle?.attachments.filter((attachment) => attachment.type === 'image') ?? []
  const selectedAttachment = imageAttachments.find((attachment) => attachment.id === document.details.selectedImageAttachmentId)
  const imageState = useVehicleAttachmentUrl(document.vehicleId, selectedAttachment?.id ?? '')
  const sheetSvg = buildSalesEstimateSheetSvg(document, settings, { imageHref: imageState.url })
  const sections = buildSalesEstimateSections(document)
  // Keep the previous implementation available while the new fixed A4 sheet is stabilized.
  void SalesEstimatePreviewLayout

  return <div className="sales-preview-area">
    <div className="sales-estimate-image-control">
      <div><strong><ImageIcon size={16} />帳票に表示する車両画像</strong><span>{selectedVehicle ? `${selectedVehicle.maker} ${selectedVehicle.model}の添付画像から選択できます。` : '対象車両を選択すると添付画像を選択できます。'}</span></div>
      <div className="sales-estimate-image-select">
        <select aria-label="帳票に表示する車両画像" value={document.details.selectedImageAttachmentId} disabled={isDraft || !imageAttachments.length} onChange={(event) => onUpdateDetails({ selectedImageAttachmentId: event.target.value })}>
          <option value="">画像なし（顧客情報を拡張）</option>
          {imageAttachments.map((attachment) => <option key={attachment.id} value={attachment.id}>{attachment.name}</option>)}
        </select>
        {imageState.loading && <small><RefreshCw size={13} className="is-spinning" />画像を読み込んでいます…</small>}
        {imageState.error && <small className="is-error">画像を表示できないため、顧客情報表示に切り替えています。</small>}
        {isDraft ? <small>未保存書類では添付画像を選択できません。</small> : !imageAttachments.length && <small>画像ファイルが登録されていません。</small>}
      </div>
    </div>
    <div className="sales-estimate-sheet-frame">
      <div className="sales-estimate-sheet" dangerouslySetInnerHTML={{ __html: sheetSvg }} />
      <SalesEstimateSheetEditor
        document={document}
        hasImage={Boolean(imageState.url)}
        sections={sections}
        itemPresetGroups={settings.salesItemPresetGroups}
        onUpdateDetails={onUpdateDetails}
        onUpdateHeader={onUpdateHeader}
        onUpdateLine={onUpdateSheetLine}
      />
    </div>
  </div>
}

function AbacusDetailSummary({ document }: { document: SalesDocumentLike }) {
  const rows = document.items
  const report = document.abacusDetailReport
  return <section className="abacus-detail-summary" aria-label="ABACUS移行明細">
    <div className="abacus-detail-summary-header"><strong>ABACUS移行明細</strong><span>未入力項目は空欄のまま表示しています。</span>{report?.amountOnlyRowCount ? <span>金額のみの行：{report.amountOnlyRowCount}件</span> : null}</div>
    <div className="abacus-detail-summary-table">
      <div className="abacus-detail-summary-row is-head"><span>行</span><span>品名</span><span>数量</span><span>単位</span><span>部品単価</span><span>部品金額</span><span>技術料・他</span><span>摘要</span></div>
      {rows.map((item, index) => {
        const detail = item.abacusDetail
        const quantity = detail ? detail.quantity : item.quantity
        const unit = detail ? detail.unit : item.unit
        const unitPrice = detail ? detail.unitPrice : item.unitPrice
        const partAmount = detail ? detail.partAmount : calculateSalesLineAmount(item)
        const technicalFees = detail ? detail.technicalFees : item.otherAmount
        const description = detail ? detail.description : item.description
        const summary = detail ? detail.summary : item.summary
        return <div className="abacus-detail-summary-row" key={item.id}><span>{detail?.sourceRowIndex ?? index + 1}</span><span>{description ?? ''}</span><span>{quantity ?? ''}</span><span>{unit ?? ''}</span><span>{unitPrice ?? ''}</span><span>{partAmount ?? ''}</span><span>{technicalFees ?? ''}</span><span>{summary ?? ''}</span></div>
      })}
    </div>
    {report?.warning && <p className="abacus-detail-summary-warning">{report.warning}</p>}
  </section>
}

type SheetLinePosition = {
  bucket: SalesEstimateEditableBucket
  presetGroup: SalesItemPresetGroupKey
  index: number
  x: number
  y: number
  width: number
  labelWidth: number
  height: number
  fixedLabel?: string
  menuUp?: boolean
}

const salesEstimateSheetLinePositions: SheetLinePosition[] = [
  { bucket: 'vehicleBase', presetGroup: 'vehiclePrice', index: 0, x: salesEstimateSheetLayout.vehicle.x, y: salesEstimateSheetLayout.lowerY + 39, width: salesEstimateSheetLayout.vehicle.width, labelWidth: 198, height: 35, fixedLabel: '車両本体価格' },
  { bucket: 'discounts', presetGroup: 'vehiclePrice', index: 0, x: salesEstimateSheetLayout.vehicle.x, y: salesEstimateSheetLayout.lowerY + 74, width: salesEstimateSheetLayout.vehicle.width, labelWidth: 198, height: 35, fixedLabel: '値引等' },
  { bucket: 'vehicleSideLabor', presetGroup: 'vehiclePrice', index: 0, x: salesEstimateSheetLayout.vehicle.x, y: salesEstimateSheetLayout.lowerY + 179, width: salesEstimateSheetLayout.vehicle.width, labelWidth: 198, height: 35 },
  ...salesEstimateSheetLayout.fee.groups.flatMap((group) => Array.from({ length: group.rows }, (_, index) => ({
    bucket: group.bucket,
    presetGroup: 'fees' as const,
    index,
    x: salesEstimateSheetLayout.fee.detailX,
    y: group.startY + index * 26,
    width: salesEstimateSheetLayout.fee.detailWidth,
    labelWidth: salesEstimateSheetLayout.fee.detailLabelWidth,
    height: 26,
    menuUp: group.startY + index * 26 >= 1040,
  }))),
  ...Array.from({ length: salesEstimateSheetLayout.accessory.rowCount }, (_, index) => ({
    bucket: 'accessories' as const,
    presetGroup: 'accessories' as const,
    index,
    x: salesEstimateSheetLayout.accessory.x,
    y: salesEstimateSheetLayout.accessory.detailY + index * salesEstimateSheetLayout.accessory.rowHeight,
    width: salesEstimateSheetLayout.accessory.width,
    labelWidth: salesEstimateSheetLayout.accessory.nameWidth,
    height: salesEstimateSheetLayout.accessory.rowHeight,
    menuUp: index > 8,
  })),
]

export function SalesEstimateSheetEditor({ document, hasImage, sections, itemPresetGroups, onUpdateDetails, onUpdateHeader, onUpdateLine }: { document: SalesDocumentLike; hasImage: boolean; sections: SalesEstimateSections; itemPresetGroups: SalesItemPresetGroups; onUpdateDetails: SalesPreviewProps['onUpdateDetails']; onUpdateHeader: SalesPreviewProps['onUpdateHeader']; onUpdateLine: SalesPreviewProps['onUpdateSheetLine'] }) {
  const customer = currentSalesCustomerValues(document)
  const vehicle = document.details.vehicleOverride ?? document.vehicleDetails ?? emptyVehicleDetails()
  const tradeInLine = sections.tradeIns[0]

  function updateCustomer(field: keyof NonNullable<SalesDocumentDetails['customerOverride']>, value: string) {
    onUpdateDetails({
      customerOverride: { ...customer, [field]: value },
      ...(field === 'birthDate' ? { customerBirthDate: value } : {}),
      ...(field === 'employer' ? { customerEmployer: value } : {}),
    })
  }

  function updateVehicle(field: keyof NonNullable<SalesDocumentDetails['vehicleOverride']>, value: string | boolean) {
    onUpdateDetails({ vehicleOverride: { ...vehicle, [field]: value } })
  }

  function updateTradeIn(field: keyof SalesDocumentDetails['tradeIn'], value: string) {
    onUpdateDetails({ tradeIn: { ...document.details.tradeIn, [field]: value } })
  }

  function updateRequiredDocument(field: keyof SalesDocumentDetails['requiredDocuments'], checked: boolean) {
    onUpdateDetails({ requiredDocuments: { ...document.details.requiredDocuments, [field]: checked, ...(field === 'selfDeclaration' ? { warrantyCertificate: checked } : {}) } })
  }

  function updateCredit(field: 'paymentCount' | 'bonusPayment' | 'fee' | 'bonusMonths', value: string) {
    const credit = document.details.credit
    const nextCredit = {
      ...credit,
      [field]: field === 'bonusPayment' || field === 'fee' ? Number(value || 0) : value,
    }
    nextCredit.enabled = Boolean(
      nextCredit.paymentCount
      || nextCredit.bonusPayment
      || nextCredit.fee
      || nextCredit.monthlyPayment
      || nextCredit.initialPayment
      || nextCredit.bonusMonths,
    )
    onUpdateDetails({ credit: nextCredit })
  }

  return <div className="sales-estimate-sheet-editor" aria-label="見積書の明細を直接編集">
    <SalesSheetCustomerEditor document={document} hasImage={hasImage} customer={customer} onUpdateCustomer={updateCustomer} onUpdateDetails={onUpdateDetails} />
    <SalesSheetVehicleEditor hasImage={hasImage} vehicle={vehicle} onUpdate={updateVehicle} />
    <SalesSheetTradeInEditor hasImage={hasImage} tradeIn={document.details.tradeIn} onUpdate={updateTradeIn} />
    <SalesSheetRequiredDocumentsEditor requiredDocuments={document.details.requiredDocuments} onUpdate={updateRequiredDocument} />
    <SalesSheetCreditEditor credit={document.details.credit} onUpdate={updateCredit} />
    <SheetTextControl multiline ariaLabel="備考" value={document.note} x={713} y={salesEstimateSheetLayout.noteY + 37} width={318} height={27} onChange={(value) => onUpdateHeader('note', value)} />
    {salesEstimateSheetLinePositions.map((position) => {
      const line = sections[position.bucket][position.index]
      const candidates = Array.from(new Set(itemPresetGroups[position.presetGroup].filter(Boolean)))
      return <SheetLineControl
        key={`${position.bucket}-${position.index}`}
        position={position}
        label={line?.label ?? ''}
        amount={line?.amount ?? 0}
        exists={Boolean(line)}
        candidates={candidates}
        onChange={(patch) => onUpdateLine(position.bucket, position.index, patch)}
      />
    })}
    <div className="sales-estimate-sheet-line-control is-amount-only" style={{ left: `${221 / 10.55}%`, top: `${salesEstimateSheetLayout.vehicle.paymentY / 14.91}%`, width: `${126 / 10.55}%`, height: `${31 / 14.91}%` }}>
      <SheetAmountInput value={tradeInLine?.amount ?? 0} exists={Boolean(tradeInLine)} onCommit={(amount) => onUpdateLine('tradeIns', 0, { amount })} />
    </div>
    <div className="sales-estimate-sheet-line-control is-amount-only" style={{ left: `${221 / 10.55}%`, top: `${(salesEstimateSheetLayout.vehicle.paymentY + 31) / 14.91}%`, width: `${126 / 10.55}%`, height: `${31 / 14.91}%` }}>
      <SheetAmountInput value={document.details.downPayment} exists={document.details.downPayment !== 0} onCommit={(downPayment) => onUpdateDetails({ downPayment })} />
    </div>
  </div>
}

function SalesSheetCustomerEditor({ document, hasImage, customer, onUpdateCustomer, onUpdateDetails }: { document: SalesDocumentLike; hasImage: boolean; customer: NonNullable<SalesDocumentDetails['customerOverride']>; onUpdateCustomer: (field: keyof NonNullable<SalesDocumentDetails['customerOverride']>, value: string) => void; onUpdateDetails: SalesPreviewProps['onUpdateDetails'] }) {
  const customerLayout = salesEstimateSheetLayout.customer
  const left = hasImage
    ? { name: [84, customerLayout.y + 16, 230, 35], postalCode: [38, customerLayout.y + 61, 312, 27], address: [38, customerLayout.y + 93, 312, 27], phone: [38, customerLayout.y + 129, 312, 28] }
    : { name: [customerLayout.x + 116, customerLayout.y + 14, 235, 35], postalCode: [customerLayout.x + 116, customerLayout.y + 86, 235, 31], address: [customerLayout.x + 116, customerLayout.y + 114, 235, 31] }
  return <>
    <SheetTextControl variant="customer-name" ariaLabel="お客様名" value={customer.name} x={left.name[0]} y={left.name[1]} width={left.name[2]} height={left.name[3]} onChange={(value) => onUpdateCustomer('name', value)} />
    <SalesSheetCustomerHonorific hasImage={hasImage} value={document.details.customerHonorific || '様'} y={left.name[1]} height={left.name[3]} />
    <SheetTextControl variant="customer-value" normalization="postalCode" displayPrefix="〒" ariaLabel="郵便番号" value={customer.postalCode} x={left.postalCode[0]} y={left.postalCode[1]} width={left.postalCode[2]} height={left.postalCode[3]} normalizeOnBlur={normalizePostalCode} onChange={(value) => onUpdateCustomer('postalCode', value)} />
    <SheetTextControl variant="customer-value" ariaLabel="住所" value={customer.address} x={left.address[0]} y={left.address[1]} width={left.address[2]} height={left.address[3]} onChange={(value) => onUpdateCustomer('address', value)} />
    {hasImage && left.phone ? <SheetTextControl variant="customer-value" normalization="phone" displayPrefix="TEL：" ariaLabel="電話番号" value={customer.phone} x={left.phone[0]} y={left.phone[1]} width={left.phone[2]} height={left.phone[3]} normalizeOnBlur={normalizePhone} onChange={(value) => onUpdateCustomer('phone', value)} /> : null}
    {!hasImage ? <>
      <SheetTextControl grid calendar ariaLabel="生年月日" value={customer.birthDate} x={478} y={customerLayout.y + 1} width={207} height={41} normalizeOnBlur={normalizeSalesCustomerBirthDateOnBlur} onChange={(value) => onUpdateCustomer('birthDate', value)} />
      <SheetTextControl grid normalization="phone" ariaLabel="お客様電話番号" value={customer.phone} x={478} y={customerLayout.y + 42} width={207} height={41} normalizeOnBlur={normalizePhone} onChange={(value) => onUpdateCustomer('phone', value)} />
      <SheetTextControl grid ariaLabel="勤務先等" value={customer.employer} x={478} y={customerLayout.y + 83} width={207} height={41} onChange={(value) => onUpdateCustomer('employer', value)} />
      <SheetTextControl grid normalization="phone" ariaLabel="連絡先電話番号" value={document.details.customerContactPhone} x={478} y={customerLayout.y + 124} width={207} height={43} normalizeOnBlur={normalizePhone} onChange={(customerContactPhone) => onUpdateDetails({ customerContactPhone })} />
    </> : null}
  </>
}

function SalesSheetCustomerHonorific({ hasImage, value, y, height }: { hasImage: boolean; value: string; y: number; height: number }) {
  const customerLayout = salesEstimateSheetLayout.customer
  const rightEdge = hasImage ? customerLayout.x + customerLayout.imageWidth - 18 : customerLayout.x + 353 - 16
  return <span className="sales-estimate-sheet-customer-honorific" style={sheetPositionStyle(rightEdge - 60, y, 60, height)}>{value}</span>
}

function SalesSheetVehicleEditor({ hasImage, vehicle, onUpdate }: { hasImage: boolean; vehicle: NonNullable<SalesDocumentDetails['vehicleOverride']>; onUpdate: (field: keyof NonNullable<SalesDocumentDetails['vehicleOverride']>, value: string | boolean) => void }) {
  const y = hasImage ? salesEstimateSheetLayout.imageVehicleY + 39 : salesEstimateSheetLayout.expandedVehicleY + 39
  const fields: Array<{ field: keyof typeof vehicle; x: number; y: number; width: number; height: number }> = [
    { field: 'maker', x: 116, y, width: 100, height: 37 },
    { field: 'name', x: 311, y, width: 100, height: 37 },
    { field: 'year', x: 469, y, width: 82, height: 37 },
    { field: 'displacement', x: 618, y, width: 67, height: 37 },
    { field: 'transmission', x: 116, y: y + 38, width: 100, height: 37 },
    { field: 'color', x: 311, y: y + 38, width: 274, height: 37 },
    { field: 'modelType', x: 116, y: y + 75, width: 277, height: 37 },
    { field: 'vin', x: 483, y: y + 75, width: 202, height: 37 },
    { field: 'plate', x: 116, y: y + 113, width: 277, height: 37 },
    { field: 'mileage', x: 483, y: y + 113, width: 202, height: 37 },
    { field: 'inspectionDate', x: 116, y: y + 150, width: 277, height: 37 },
  ]
  return <>
    {fields.map(({ field, ...position }) => <SheetTextControl grid calendar={field === 'inspectionDate'} normalization={field === 'year' ? 'modelYear' : field === 'displacement' ? 'displacement' : field === 'mileage' ? 'mileage' : undefined} key={field} ariaLabel={`車両${field}`} value={String(vehicle[field] ?? '')} {...position} normalizeOnBlur={field === 'year' ? normalizeModelYear : field === 'displacement' ? normalizeDisplacement : field === 'mileage' ? normalizeMileage : undefined} onChange={(value) => onUpdate(field, value)} />)}
    <SheetRecordControl value={vehicle.inspectionRecordAvailable} x={483} y={y + 150} width={202} height={37} onChange={(value) => onUpdate('inspectionRecordAvailable', value)} />
  </>
}

function SalesSheetTradeInEditor({ hasImage, tradeIn, onUpdate }: { hasImage: boolean; tradeIn: SalesDocumentDetails['tradeIn']; onUpdate: (field: keyof SalesDocumentDetails['tradeIn'], value: string) => void }) {
  const y = (hasImage ? salesEstimateSheetLayout.imageTradeInY : salesEstimateSheetLayout.expandedTradeInY) + 68
  const fields: Array<{ field: keyof typeof tradeIn; x: number; width: number }> = [
    { field: 'name', x: 24, width: 180 },
    { field: 'modelYear', x: 204, width: 105 },
    { field: 'inspectionDate', x: 309, width: 118 },
    { field: 'mileage', x: 427, width: 137 },
    { field: 'color', x: 564, width: 121 },
  ]
  return <>{fields.map(({ field, x, width }) => <SheetTextControl grid calendar={field === 'inspectionDate'} normalization={field === 'modelYear' ? 'modelYear' : field === 'mileage' ? 'mileage' : undefined} calendarControlClassName={field === 'inspectionDate' ? 'is-trade-in-inspection-date' : undefined} key={field} ariaLabel={`下取車${field}`} value={tradeIn[field]} x={x} y={y} width={width} height={32} centered normalizeOnBlur={field === 'modelYear' ? normalizeModelYear : field === 'mileage' ? normalizeMileage : undefined} onChange={(value) => onUpdate(field, value)} />)}</>
}

function SalesSheetRequiredDocumentsEditor({ requiredDocuments, onUpdate }: { requiredDocuments: SalesDocumentDetails['requiredDocuments']; onUpdate: (field: keyof SalesDocumentDetails['requiredDocuments'], checked: boolean) => void }) {
  const fields: Array<keyof SalesDocumentDetails['requiredDocuments']> = ['sealCertificate', 'selfDeclaration', 'residentCard', 'powerOfAttorney', 'lightVehicleCertificate', 'transferCertificate', 'taxPaymentCertificate', 'guarantorSealCertificate']
  return <>{fields.map((field, index) => {
    const col = index % 2
    const row = Math.floor(index / 2)
    return <label key={field} className="sales-estimate-sheet-checkbox" style={sheetPositionStyle(724 + col * 156, salesEstimateSheetLayout.requiredY + 50 + row * 26, 16, 16)}><input aria-label={requiredDocumentFields.find((item) => item.key === field)?.label ?? field} type="checkbox" checked={Boolean(requiredDocuments[field])} onChange={(event) => onUpdate(field, event.target.checked)} /></label>
  })}</>
}

function SalesSheetCreditEditor({ credit, onUpdate }: { credit: SalesDocumentDetails['credit']; onUpdate: (field: 'paymentCount' | 'bonusPayment' | 'fee' | 'bonusMonths', value: string) => void }) {
  const layout = salesEstimateSheetLayout.footer.credit
  const columnWidth = layout.width / layout.columnCount
  return <>
    <SheetCreditInput ariaLabel="クレジット支払回数" value={credit.paymentCount} x={layout.x} width={columnWidth} onCommit={(value) => onUpdate('paymentCount', value)} />
    <SheetCreditInput currency ariaLabel="クレジットボーナス払" value={credit.bonusPayment ? String(credit.bonusPayment) : ''} x={layout.x + columnWidth} width={columnWidth} onCommit={(value) => onUpdate('bonusPayment', value)} />
    <SheetCreditInput decimal ariaLabel="クレジット金利" value={credit.fee ? String(credit.fee) : ''} x={layout.x + columnWidth * 2} width={columnWidth} onCommit={(value) => onUpdate('fee', value)} />
    <SheetCreditInput ariaLabel="クレジット支払開始月" value={credit.bonusMonths} x={layout.x + columnWidth * 3} width={columnWidth} onCommit={(value) => onUpdate('bonusMonths', value)} />
  </>
}

function SheetCreditInput({ ariaLabel, value, x, width, currency = false, decimal = false, onCommit }: { ariaLabel: string; value: string; x: number; width: number; currency?: boolean; decimal?: boolean; onCommit: (value: string) => void }) {
  const [draft, setDraft] = useState(value)
  const [focused, setFocused] = useState(false)
  useEffect(() => setDraft(value), [value])

  function update(nextValue: string) {
    const pattern = decimal ? /^\d*(?:\.\d{0,2})?$/ : /^[\d./-]*$/
    if (pattern.test(nextValue)) setDraft(nextValue)
  }

  function finish() {
    setFocused(false)
    if (draft !== value) onCommit(draft)
  }

  return <input
    className="sales-estimate-sheet-field-control has-grid is-centered"
    aria-label={ariaLabel}
    inputMode={decimal ? 'decimal' : 'numeric'}
    value={currency && !focused && draft ? formatSheetYen(Number(draft)) : draft}
    style={sheetPositionStyle(x, salesEstimateSheetLayout.creditY + salesEstimateSheetLayout.footer.credit.valueY, width, salesEstimateSheetLayout.footer.credit.valueHeight)}
    onFocus={() => setFocused(true)}
    onChange={(event) => update(event.target.value)}
    onBlur={finish}
  />
}

function SheetTextControl({ ariaLabel, value, x, y, width, height, centered = false, multiline = false, grid = false, calendar = false, calendarControlClassName = '', normalization, variant, displayPrefix = '', normalizeOnBlur, onChange }: { ariaLabel: string; value: string; x: number; y: number; width: number; height: number; centered?: boolean; multiline?: boolean; grid?: boolean; calendar?: boolean; calendarControlClassName?: string; normalization?: NormalizableField; variant?: 'customer-name' | 'customer-value'; displayPrefix?: string; normalizeOnBlur?: (value: string) => string; onChange: (value: string) => void }) {
  const [draft, setDraft] = useState(() => normalization ? toEditableNormalizedValue(normalization, value) : value)
  const [focused, setFocused] = useState(false)
  useEffect(() => {
    if (!focused) setDraft(normalization ? toEditableNormalizedValue(normalization, value) : value)
  }, [focused, normalization, value])

  const className = `sales-estimate-sheet-field-control${centered ? ' is-centered' : ''}${multiline ? ' is-multiline' : ''}${grid ? ' has-grid' : ''}${variant ? ` is-${variant}` : ''}`
  const editableValue = normalization && focused ? draft : value
  const displayValue = editableValue ? `${displayPrefix}${editableValue}` : editableValue
  function handleChange(nextValue: string) {
    const withoutPrefix = displayPrefix && nextValue.startsWith(displayPrefix) ? nextValue.slice(displayPrefix.length) : nextValue
    if (!normalization) {
      onChange(withoutPrefix)
      return
    }
    const sanitized = sanitizeNormalizedDraft(normalization, withoutPrefix)
    if (sanitized === null) return
    setDraft(sanitized)
  }
  function beginEdit() {
    if (!normalization) return
    setFocused(true)
    setDraft(toEditableNormalizedValue(normalization, value))
  }
  function finish() {
    setFocused(false)
    if (!normalizeOnBlur) return
    if (!normalization) {
      const normalized = normalizeOnBlur(value)
      if (normalized !== value) onChange(normalized)
      return
    }
    const normalized = normalizeOnBlur(draft)
    if (normalized !== value) onChange(normalized)
  }
  const props = { className, 'aria-label': ariaLabel, spellCheck: false, value: displayValue, onFocus: normalization ? beginEdit : undefined, onChange: (event: ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) => handleChange(event.target.value), onBlur: finish }
  if (!calendar || multiline) return multiline ? <textarea {...props} style={sheetPositionStyle(x, y, width, height)} /> : <input {...props} style={sheetPositionStyle(x, y, width, height)} />
  return <div className={`sales-estimate-sheet-calendar-control${calendarControlClassName ? ` ${calendarControlClassName}` : ''}`} style={sheetPositionStyle(x, y, width, height)}>
    <input {...props} type="date" value={toNativeDateValue(value)} onChange={(event) => onChange(event.target.value.replaceAll('-', '/'))} style={{ position: 'relative', inset: 'auto', width: '100%', height: '100%' }} />
    <DateCalendarButton ariaLabel={ariaLabel} value={value} onChange={onChange} />
  </div>
}

function SheetRecordControl({ value, x, y, width, height, onChange }: { value: boolean; x: number; y: number; width: number; height: number; onChange: (value: boolean) => void }) {
  return <select
    aria-label="記録簿"
    className="sales-estimate-sheet-field-control has-grid is-select"
    value={value ? 'あり' : 'なし'}
    style={sheetPositionStyle(x, y, width, height)}
    onChange={(event) => onChange(event.target.value === 'あり')}
  >
    <option value="あり">あり</option>
    <option value="なし">なし</option>
  </select>
}

function sheetPositionStyle(x: number, y: number, width: number, height: number): CSSProperties {
  return { left: `${x / 10.55}%`, top: `${y / 14.91}%`, width: `${width / 10.55}%`, height: `${height / 14.91}%` }
}

function SheetLineControl({ position, label, amount, exists, candidates, onChange }: { position: SheetLinePosition; label: string; amount: number; exists: boolean; candidates: string[]; onChange: (patch: { label?: string; amount?: number }) => void }) {
  const style = {
    left: `${position.x / 10.55}%`,
    top: `${position.y / 14.91}%`,
    width: `${position.width / 10.55}%`,
    height: `${position.height / 14.91}%`,
    '--sheet-label-width': `${position.labelWidth / position.width * 100}%`,
  } as CSSProperties
  return <div className={`sales-estimate-sheet-line-control${position.bucket === 'accessories' ? ' is-accessory-line' : ''}`} style={style}>
    {position.fixedLabel
      ? <span className="sales-sheet-fixed-label">{position.fixedLabel}</span>
      : <SheetNameCombobox value={label} candidates={candidates} menuUp={position.menuUp} onCommit={(value) => onChange({ label: value })} />}
    <SheetAmountInput value={amount} exists={exists} onCommit={(value) => onChange({ amount: value })} />
  </div>
}

function SheetNameCombobox({ value, candidates, menuUp = false, onCommit }: { value: string; candidates: string[]; menuUp?: boolean; onCommit: (value: string) => void }) {
  const [draft, setDraft] = useState(value)
  const [open, setOpen] = useState(false)
  const rootRef = useRef<HTMLDivElement>(null)
  useEffect(() => setDraft(value), [value])
  useEffect(() => {
    if (!open) return
    const closeOnOutsidePointer = (event: PointerEvent) => {
      if (!rootRef.current?.contains(event.target as Node)) setOpen(false)
    }
    document.addEventListener('pointerdown', closeOnOutsidePointer)
    return () => document.removeEventListener('pointerdown', closeOnOutsidePointer)
  }, [open])

  function commit() {
    setOpen(false)
    if (draft !== value) onCommit(draft.trim())
  }

  return <div ref={rootRef} className="sales-sheet-name-combobox">
    <input
      aria-label="費用名・品名"
      role="combobox"
      aria-expanded={open}
      value={draft}
      onChange={(event) => setDraft(event.target.value)}
      onFocus={() => setOpen(false)}
      onBlur={commit}
    />
    <button type="button" aria-label="明細候補を表示" onMouseDown={(event) => event.preventDefault()} onClick={() => setOpen((current) => !current)}><ChevronDown size={13} /></button>
    {open ? <div className={`sales-sheet-candidate-menu${menuUp ? ' is-up' : ''}`} role="listbox">
      {candidates.map((candidate) => <button key={candidate} type="button" role="option" aria-selected={candidate === draft} onMouseDown={(event) => event.preventDefault()} onClick={() => { setDraft(candidate); setOpen(false); onCommit(candidate) }}>{candidate}</button>)}
    </div> : null}
  </div>
}

function SheetAmountInput({ value, exists, onCommit }: { value: number; exists: boolean; onCommit: (value: number) => void }) {
  const [draft, setDraft] = useState(exists ? String(value) : '')
  const [focused, setFocused] = useState(false)
  useEffect(() => setDraft(exists ? String(value) : ''), [exists, value])

  function update(nextValue: string) {
    if (!/^-?\d*$/.test(nextValue)) return
    setDraft(nextValue)
    if (nextValue && nextValue !== '-') onCommit(Number(nextValue))
  }

  function finish() {
    if (!draft || draft === '-') {
      setDraft('')
      if (exists) onCommit(0)
    }
    setFocused(false)
  }

  return <input
    className="sales-sheet-amount-input"
    aria-label="金額"
    inputMode="numeric"
    value={focused ? draft : draft ? formatSheetYen(Number(draft)) : ''}
    onFocus={() => {
      setDraft(exists ? String(value) : '')
      setFocused(true)
    }}
    onChange={(event) => update(event.target.value)}
    onBlur={finish}
  />
}

function formatSheetYen(value: number) {
  const formatted = sheetYenFormatter.format(Math.abs(Math.round(value)))
  return value < 0 ? `-¥${formatted}` : `¥${formatted}`
}

function SalesEstimatePreviewLayout({ document, totals, settings, itemPresets, customers, onUpdateHeader, onUpdateDetails, onUpdateItem, onAddItem, onRemoveItem, onPdfPreview }: SalesPreviewProps) {
  const selectedCustomer = customers.find((customer) => customer.id === document.customerId)
  const selectedVehicle = selectedCustomer?.vehicles.find((vehicle) => vehicle.id === document.vehicleId)
  const customer = document.customerDetails ?? mapCustomerDetails(selectedCustomer)
  const vehicle = document.vehicleDetails ?? (selectedVehicle ? mapVehicleDetails(selectedVehicle) : null)
  const details = document.details
  const sections = buildSalesEstimateSections(document)
  const imageAttachments = selectedVehicle?.attachments.filter((attachment) => attachment.type === 'image') ?? []
  const selectedAttachment = imageAttachments.find((attachment) => attachment.id === details.selectedImageAttachmentId)
  const imageState = useVehicleAttachmentUrl(document.vehicleId, selectedAttachment?.id ?? '')
  const hasImage = Boolean(imageState.url && selectedAttachment)
  const shopLines = [settings.shop.postalCode ? `〒${settings.shop.postalCode}` : '', settings.shop.address, settings.shop.phone ? `TEL ${settings.shop.phone}` : '', settings.shop.representative ? `担当 ${settings.shop.representative}` : '', settings.shop.registrationNumber ? `登録番号 ${settings.shop.registrationNumber}` : ''].filter(Boolean)
  const paymentNote = settings.document.paymentNote || '店頭または指定口座へお支払いください。'
  const bankAccount = [settings.shop.bankName, settings.shop.bankAccount].filter(Boolean).join(' / ') || '未設定'

  return <div className="sales-preview-area"><div className="sales-preview-toolbar"><div><strong>見積書プレビュー</strong><span>PDFと同じ帳票構成で確認できます。表示画像と明細はこの画面から変更できます。</span></div><button className="button button-secondary" type="button" onClick={onPdfPreview}><Eye size={16} />PDFで確認</button></div><div className="sales-estimate-image-control"><div><strong><ImageIcon size={16} />帳票に表示する車両画像</strong><span>{selectedVehicle ? `${selectedVehicle.maker} ${selectedVehicle.model}の添付画像から選択できます。` : '対象車両を選択すると添付画像を選択できます。'}</span></div><div className="sales-estimate-image-select"><select aria-label="帳票に表示する車両画像" value={details.selectedImageAttachmentId} disabled={!imageAttachments.length} onChange={(event) => onUpdateDetails({ selectedImageAttachmentId: event.target.value })}><option value="">画像なし（顧客情報を拡張）</option>{imageAttachments.map((attachment) => <option key={attachment.id} value={attachment.id}>{attachment.name}</option>)}</select>{imageState.loading && <small><RefreshCw size={13} className="is-spinning" />画像を読み込んでいます…</small>}{imageState.error && <small className="is-error">画像を表示できないため、顧客情報表示に切り替えています。</small>}{!imageAttachments.length && <small>画像ファイルが登録されていません。</small>}</div></div><article className={`sales-document-paper sales-estimate-paper${hasImage ? ' has-selected-image' : ' has-expanded-customer'}`}><header className="sales-estimate-paper-header"><div className="sales-estimate-title-block"><select className="sales-estimate-title" aria-label="書類種別" value={document.type} onChange={(event) => onUpdateHeader('type', event.target.value)}><option>見積書</option><option>請求書</option></select><span>{details.salesCategory || '販売書類'}</span></div><div className="sales-estimate-meta-table"><div><span>日付</span><input type="date" aria-label="発行日" value={document.issuedAt.replaceAll('/', '-')} onChange={(event) => onUpdateHeader('issuedAt', event.target.value.replaceAll('-', '/'))} /></div><div><span>販売区分</span><strong>{details.salesCategory || '未設定'}</strong></div><div><span>担当</span><strong>{details.staffName || '未設定'}</strong></div><div><span>見積番号</span><strong>{document.number}</strong></div><div><span>ページ</span><strong>1 / 1</strong></div></div></header><section className="sales-estimate-customer-grid"><div className="sales-estimate-customer-box"><div className="sales-estimate-cell-label">お名前</div><div className="sales-estimate-cell-value"><strong>{customer.name || '未設定'} {details.customerHonorific || '様'}</strong><small>{customer.kana || 'ふりがな未登録'}</small></div><div className="sales-estimate-cell-label">ご住所</div><div className="sales-estimate-cell-value"><span>{customer.postalCode ? `〒${customer.postalCode}` : ''}</span><span>{customer.address || '住所未登録'}</span></div><div className="sales-estimate-cell-label">電話番号</div><div className="sales-estimate-cell-value"><span>{customer.phone || '未登録'}</span></div></div>{hasImage ? <div className="sales-estimate-photo-box"><img src={imageState.url} alt={`${vehicle?.name || '対象車両'}の選択画像`} /><small>{selectedAttachment?.name}</small></div> : <div className="sales-estimate-contact-box"><div className="sales-estimate-cell-label">生年月日</div><div>{details.customerBirthDate || customer.birthDate || '未設定'}</div><div className="sales-estimate-cell-label">電話番号</div><div>{customer.phone || '未登録'}</div><div className="sales-estimate-cell-label">勤務先等</div><div>{details.customerEmployer || customer.employer || '未設定'}</div><div className="sales-estimate-cell-label">連絡先TEL</div><div>{details.customerContactPhone || '未設定'}</div></div>}</section><EstimateVehicleTable vehicle={vehicle} /><EstimateTradeInTable details={details} /><section className="sales-estimate-summary-top"><div className="sales-estimate-amount-card"><span>お見積金額（税込）</span><strong>{formatYen(totals.total)}</strong></div><div className="sales-estimate-tax-card"><div><span>課税対象額（{formatPercent(document.taxRate)}）</span><strong>{formatYen(totals.taxableSubtotal)}</strong></div><div><span>消費税（{formatPercent(document.taxRate)}）</span><strong>{formatYen(totals.tax)}</strong></div><div><span>非課税対象額</span><strong>{formatYen(totals.nonTaxableSubtotal + totals.outOfScopeSubtotal)}</strong></div></div></section><div className="sales-estimate-status-line"><span>支払期限：{document.dueDate || '未設定'}</span><span>状態：{document.status}</span></div><div className="sales-estimate-section-title"><h3>見積金額内訳</h3><span /></div><div className="sales-estimate-breakdown-grid"><EstimateVehicleBreakdown totals={totals} taxRate={document.taxRate} /><EstimateFeeBreakdown sections={sections} totals={totals} /><EstimateAccessoryBreakdown sections={sections} totals={totals} /></div><details className="sales-estimate-edit-details"><summary><FileText size={15} />金額明細を編集</summary><div className="sales-estimate-items-table"><div className="sales-estimate-items-head"><span>No.</span><span>作業内容／部品名等</span><span>数量</span><span>単位</span><span>部品単価</span><span>部品金額</span><span>技術料・他</span><span>摘要・課税</span></div>{document.items.map((item, index) => <div className="sales-estimate-item-row" key={item.id}><span>{index + 1}</span><input list="sales-preview-item-presets" aria-label="プレビューの明細内容" value={item.description} onChange={(event) => onUpdateItem(item.id, 'description', event.target.value)} placeholder="明細内容" /><input aria-label="プレビューの数量" type="number" min="0" value={item.quantity} onChange={(event) => onUpdateItem(item.id, 'quantity', event.target.value)} /><input aria-label="プレビューの単位" value={item.unit} onChange={(event) => onUpdateItem(item.id, 'unit', event.target.value)} /><input aria-label="プレビューの単価" type="number" value={item.unitPrice} onChange={(event) => onUpdateItem(item.id, 'unitPrice', event.target.value)} /><strong>{formatYen(calculateSalesLineAmount(item))}</strong><input aria-label="プレビューの技術料・他" type="number" value={item.otherAmount} onChange={(event) => onUpdateItem(item.id, 'otherAmount', event.target.value)} /><div className="sales-estimate-summary-cell"><input aria-label="プレビューの摘要" value={item.summary} onChange={(event) => onUpdateItem(item.id, 'summary', event.target.value)} /><select aria-label="プレビューの課税区分" value={item.taxCategory} onChange={(event) => onUpdateItem(item.id, 'taxCategory', event.target.value)}>{salesTaxCategories.map((category) => <option key={category}>{category}</option>)}</select></div><button className="sales-estimate-item-remove" type="button" aria-label="明細を削除" onClick={() => onRemoveLineItemGuard(item.id, document.items.length, onRemoveItem)}><Trash2 size={14} /></button></div>)}</div><datalist id="sales-preview-item-presets">{itemPresets.map((preset) => <option key={preset} value={preset} />)}</datalist><div className="sales-estimate-edit-actions"><button className="button button-secondary" type="button" onClick={onAddItem}><Plus size={15} />明細を追加</button><span>残金・所要資金は見積総額、下取、頭金から自動計算します。</span></div></details><section className="sales-estimate-bottom-grid"><div><div className="sales-estimate-credit"><h4>クレジットお支払いプラン</h4>{details.credit.enabled ? <div><span>{details.credit.paymentCount || '回数未設定'}</span><span>手数料 {formatYen(details.credit.fee)}</span><span>月々 {formatYen(details.credit.monthlyPayment)}</span><span>初回 {formatYen(details.credit.initialPayment)}</span><span>ボーナス {details.credit.bonusMonths || '月未設定'} / {formatYen(details.credit.bonusPayment)}</span></div> : <p>利用なし</p>}</div><div className="sales-estimate-required"><h4>必要書類</h4><p>{requiredDocumentLabels(details).join(' ／ ') || '未確認'}</p></div></div><div className="sales-estimate-company"><strong>{settings.shop.name || '店舗名未設定'}</strong>{shopLines.slice(0, 4).map((line) => <span key={line}>{line}</span>)}<div className="sales-estimate-company-payment"><span>お支払いについて</span><p>{paymentNote}</p><span>振込先</span><p>{bankAccount}</p></div></div></section><footer className="sales-paper-footer"><span>{document.note || settings.document.footerNote || '見積条件は担当者へご確認ください。'}</span><span>ページ 1 / 1</span></footer></article></div>
}

function EstimateVehicleBreakdown({ totals, taxRate }: { totals: SalesTotals; taxRate: number }) {
  return <section className="sales-estimate-breakdown-card"><h4>車両販売価格内訳</h4><EstimateBreakdownRow label="車両本体価格" amount={totals.vehicleBasePrice} /><EstimateBreakdownRow label="値引等" amount={totals.discount} tone="discount" /><EstimateBreakdownRow label="本体課税対象額" amount={totals.vehicleTaxableAmount} /><EstimateBreakdownRow label="付属品／特別仕様" amount={totals.accessoryTotal} /><EstimateBreakdownRow label="車両販売合計" amount={totals.vehicleSalesTotal} emphasis /><EstimateBreakdownRow label="諸費用合計" amount={totals.feesTotal} emphasis /><div className="sales-estimate-tax-breakdown"><div className="sales-estimate-tax-breakdown-heading"><span /><span>課税対象{formatPercent(taxRate)}</span><span>非課税対象</span></div><div className="sales-estimate-tax-breakdown-row"><span>対象額合計</span><strong>{formatYen(totals.taxableSubtotal)}</strong><strong>{formatYen(totals.nonTaxableSubtotal + totals.outOfScopeSubtotal)}</strong></div><div className="sales-estimate-tax-breakdown-row"><span>消費税（{formatPercent(taxRate)}）</span><strong>{formatYen(totals.tax)}</strong><strong>−</strong></div><div className="sales-estimate-tax-breakdown-total"><span>総額</span><strong>{formatYen(totals.total)}</strong></div></div><EstimateBreakdownRow label="下取車価格" amount={totals.tradeInPrice} /><EstimateBreakdownRow label="頭金／現金／他" amount={totals.downPayment} /><EstimateBreakdownRow label="残金／所要資金" amount={totals.remainingPayment} emphasis /></section>
}

function EstimateFeeBreakdown({ sections, totals }: { sections: SalesEstimateSections; totals: SalesTotals }) {
  return <section className="sales-estimate-breakdown-card sales-estimate-fee-breakdown"><h4>諸費用内訳</h4><EstimateFeeGroup title="税金/保険料（非課税）（非課税）" lines={sections.legalNonTaxable} total={totals.legalNonTaxable} /><EstimateFeeGroup title="手続代行費用（課税）" lines={sections.taxableFees} total={totals.taxableFeeTotal} /><EstimateFeeGroup title="実費・預託金（非課税）" lines={sections.nonTaxableFees} total={totals.nonTaxableFeeTotal} /><div className="sales-estimate-fee-total"><span>諸費用合計</span><strong>{formatYen(totals.feesTotal)}</strong></div></section>
}

function EstimateAccessoryBreakdown({ sections, totals }: { sections: SalesEstimateSections; totals: SalesTotals }) {
  return <section className="sales-estimate-breakdown-card sales-estimate-accessory-breakdown"><h4>付属品・特別仕様明細</h4><div className="sales-estimate-breakdown-heading"><span>品名</span><span>金額</span></div>{sections.accessories.length ? sections.accessories.map((line) => <EstimateBreakdownRow key={line.id} label={line.label} amount={line.amount} />) : <div className="sales-estimate-breakdown-empty">登録なし</div>}<div className="sales-estimate-accessory-total"><span>付属品・特別仕様合計</span><strong>{formatYen(totals.accessoryTotal)}</strong></div></section>
}

function EstimateFeeGroup({ title, lines, total }: { title: string; lines: Array<{ id: string; label: string; amount: number }>; total: number }) {
  return <div className="sales-estimate-fee-group"><h5>{title}</h5>{lines.length ? lines.map((line) => <EstimateBreakdownRow key={line.id} label={line.label} amount={line.amount} />) : <div className="sales-estimate-breakdown-empty">なし</div>}<div className="sales-estimate-fee-subtotal"><span>小計</span><strong>{formatYen(total)}</strong></div></div>
}

function EstimateBreakdownRow({ label, amount, tone, emphasis }: { label: string; amount: number; tone?: 'discount'; emphasis?: boolean }) {
  return <div className={`sales-estimate-breakdown-row${emphasis ? ' is-emphasis' : ''}${tone ? ` is-${tone}` : ''}`}><span>{label}</span><strong>{formatYen(amount)}</strong></div>
}

function useVehicleAttachmentUrl(vehicleId: string | null, attachmentId: string) {
  const [state, setState] = useState<{ url: string; loading: boolean; error: string }>({ url: '', loading: false, error: '' })

  useEffect(() => {
    let cancelled = false
    let objectUrl = ''
    if (!vehicleId || !attachmentId) {
      setState({ url: '', loading: false, error: '' })
      return () => { cancelled = true }
    }
    setState({ url: '', loading: true, error: '' })
    fetchVehicleFile(vehicleId, attachmentId).then((blob) => {
      if (cancelled) return
      objectUrl = URL.createObjectURL(blob)
      setState({ url: objectUrl, loading: false, error: '' })
    }).catch((error: unknown) => {
      if (!cancelled) setState({ url: '', loading: false, error: error instanceof Error ? error.message : '画像を読み込めませんでした。' })
    })
    return () => {
      cancelled = true
      if (objectUrl) URL.revokeObjectURL(objectUrl)
    }
  }, [attachmentId, vehicleId])

  return state
}

function EstimateVehicleTable({ vehicle }: { vehicle: SalesDocument['vehicleDetails'] }) {
  const values = vehicle ?? { maker: '', name: '', modelType: '', plate: '', vin: '', year: '', inspectionDate: '', mileage: '', color: '', displacement: '', transmission: '', inspectionRecordAvailable: false }
  return <section className="sales-estimate-vehicle-table"><div className="sales-estimate-vehicle-row sales-estimate-vehicle-labels"><span>メーカー</span><span>車名・仕様</span><span>年式</span><span>排気量</span><span>ミッション</span><span>車体色</span></div><div className="sales-estimate-vehicle-row"><span>{values.maker || '未設定'}</span><span>{values.name || '未設定'}</span><span>{values.year || '未設定'}</span><span>{values.displacement || '未設定'}</span><span>{values.transmission || '未設定'}</span><span>{values.color || '未設定'}</span></div><div className="sales-estimate-vehicle-row sales-estimate-vehicle-labels"><span>型式</span><span>車台番号</span><span>登録番号</span><span>走行距離</span><span>車検日</span><span>記録簿</span></div><div className="sales-estimate-vehicle-row"><span>{values.modelType || '未設定'}</span><span>{values.vin || '未設定'}</span><span>{values.plate || '未設定'}</span><span>{values.mileage || '未設定'}</span><span>{values.inspectionDate || '未設定'}</span><span>{values.inspectionRecordAvailable ? 'あり' : 'なし'}</span></div></section>
}

function EstimateTradeInTable({ details }: { details: SalesDocumentDetails }) {
  const values = details.tradeIn
  return <section className="sales-estimate-tradein-table"><div><span>下取車名（型式等）</span><span>年式</span><span>車検日</span><span>走行距離</span><span>車体色</span></div><div><strong>{values.name || 'なし'}</strong><span>{values.modelYear || '-'}</span><span>{values.inspectionDate || '-'}</span><span>{values.mileage || '-'}</span><span>{values.color || '-'}</span></div></section>
}

function onRemoveLineItemGuard(itemId: string, itemCount: number, onRemove: (itemId: string) => void) {
  if (itemCount <= 1) return
  onRemove(itemId)
}

function StatusTag({ status }: { status: SalesDocument['status'] }) {
  const tone = status === '入金待ち' ? 'warning' : status === '完了' ? 'normal' : status === 'アーカイブ済み' ? 'danger' : 'draft'
  return <span className={`sales-status-tag sales-status-${tone}`}><span className="status-dot" />{status}</span>
}

function calculateTotals(document: SalesDocument): SalesTotals {
  return calculateSalesEstimateTotals(document)
}

function formatYen(amount: number) {
  return `¥${new Intl.NumberFormat('ja-JP').format(Math.round(amount))}`
}

function formatPercent(value: number) {
  return `${Number.isInteger(value * 100) ? value * 100 : (value * 100).toFixed(2)}%`
}

function requiredDocumentLabels(details: SalesDocumentDetails) {
  return requiredDocumentFields.filter(({ key }) => details.requiredDocuments[key] === true).map(({ label }) => label).concat(details.requiredDocuments.other ? [details.requiredDocuments.other] : [])
}

function mapCustomerDetails(customer: Customer | undefined): SalesDocument['customerDetails'] {
  return customer ? { name: customer.name, kana: customer.kana, phone: customer.phone, email: customer.email, postalCode: customer.postalCode, address: customer.address, birthDate: normalizeSalesCustomerBirthDate(customer.birthDate), employer: normalizeSalesCustomerEmployer(customer.employer), contactPhone: '' } : emptyCustomerDetails()
}

function pickCustomerOverride(customer: SalesDocument['customerDetails']): NonNullable<SalesDocumentDetails['customerOverride']> {
  return { name: customer.name, kana: customer.kana, phone: customer.phone, email: customer.email ?? '', postalCode: customer.postalCode, address: customer.address, birthDate: customer.birthDate, employer: customer.employer }
}

function emptyCustomerDetails(): SalesDocument['customerDetails'] {
  return { name: '', kana: '', phone: '', email: '', postalCode: '', address: '', birthDate: '', employer: '', contactPhone: '' }
}

function emptyVehicleDetails(): NonNullable<SalesDocumentDetails['vehicleOverride']> {
  return { maker: '', name: '', modelType: '', plate: '', vin: '', year: '', inspectionDate: '', mileage: '', color: '', displacement: '', transmission: '', inspectionRecordAvailable: false }
}

function mapVehicleDetails(vehicle: Vehicle): NonNullable<SalesDocument['vehicleDetails']> {
  return { maker: vehicle.maker, name: vehicle.model, modelType: vehicle.modelType, plate: vehicle.plate, vin: vehicle.vin, year: vehicle.year, inspectionDate: vehicle.inspectionDate, mileage: vehicle.mileage, color: vehicle.color, displacement: vehicle.displacement, transmission: vehicle.transmission, inspectionRecordAvailable: vehicle.inspectionRecordAvailable }
}

function buildSalesDraftSyncPreviewInput(document: SalesDocumentLike, context: SalesDraftContext): SyncPreviewInput {
  validateSalesDraftContext(document, context)
  const customerValues = salesCustomerValuesForSave(document)
  const input: SyncPreviewInput = {
    documentType: 'sales',
    issuedAt: normalizeSalesDocumentDate(document.issuedAt),
    openedCustomerUpdatedAt: context.customerMode === 'existing' ? context.customerUpdatedAt ?? undefined : undefined,
    openedVehicleUpdatedAt: context.vehicleMode === 'existing' ? context.vehicleUpdatedAt ?? undefined : undefined,
  }

  if (context.customerMode === 'new') {
    input.newCustomer = buildNewSalesCustomer(customerValues)
  } else {
    if (!document.customerId) throw new Error('既存顧客が選択されていません。')
    input.customerId = document.customerId
    input.customerOverride = customerValues
  }

  if (context.vehicleMode === 'new') {
    input.newVehicle = buildNewSalesVehicle(currentSalesVehicleValues(document))
  } else {
    if (!document.vehicleId) throw new Error('既存車両が選択されていません。')
    input.vehicleId = document.vehicleId
    if (document.details.vehicleOverride) input.vehicleOverride = { ...document.details.vehicleOverride }
  }

  return input
}

function buildSalesCreateInput(document: SalesDocumentLike, context: SalesDraftContext, duplicateConfirmation?: SalesDuplicateConfirmation, masterSync?: SalesMasterSync): SalesCreateInput {
  validateSalesDraftContext(document, context)
  const customerValues = salesCustomerValuesForSave(document)
  const input: SalesCreateInput = {
    type: document.type,
    status: document.status,
    issuedAt: document.issuedAt,
    dueDate: document.dueDate,
    note: document.note,
    taxRate: document.taxRate,
    taxRounding: document.taxRounding,
    details: { ...document.details, customerBirthDate: customerValues.birthDate, customerEmployer: customerValues.employer, ...(context.customerMode === 'existing' ? { customerOverride: customerValues } : {}) },
    items: document.items.map(({ id: _id, ...item }) => item),
  }

  if (context.customerMode === 'new') {
    input.newCustomer = buildNewSalesCustomer(customerValues)
  } else {
    if (!document.customerId) throw new Error('顧客を選択してください。')
    input.customerId = document.customerId
  }

  if (context.vehicleMode === 'new') {
    input.newVehicle = buildNewSalesVehicle(currentSalesVehicleValues(document))
  } else {
    if (!document.vehicleId) throw new Error('車両を選択してください。')
    input.vehicleId = document.vehicleId
  }

  if (duplicateConfirmation) input.duplicateConfirmation = duplicateConfirmation
  if (masterSync) input.masterSync = masterSync
  return input
}

function validateSalesDraftContext(document: SalesDocumentLike, context: SalesDraftContext) {
  if (context.customerMode === 'new' && context.vehicleMode === 'existing') throw new Error('新規顧客には既存車両を指定できません。')
  if (context.customerMode === 'existing' && (!document.customerId || document.customerId !== context.customerId)) throw new Error('顧客の選択状態を確認してください。')
  if (context.customerMode === 'new' && document.customerId) throw new Error('新規顧客の選択状態を確認してください。')
  if (context.vehicleMode === 'existing' && (!document.vehicleId || document.vehicleId !== context.vehicleId)) throw new Error('車両の選択状態を確認してください。')
  if (context.vehicleMode === 'new' && document.vehicleId) throw new Error('新規車両の選択状態を確認してください。')
}

function buildSalesMasterSync(result: MasterSyncConfirmationResult, preview: SyncPreviewResponse): SalesMasterSync | undefined {
  if (result.customerFields.length === 0 && result.vehicleFields.length === 0) return undefined
  return {
    confirmed: true,
    customerFields: result.customerFields,
    vehicleFields: result.vehicleFields,
    expectedCustomerUpdatedAt: result.customerFields.length > 0 ? preview.expectedCustomerUpdatedAt ?? undefined : undefined,
    expectedVehicleUpdatedAt: result.vehicleFields.length > 0 ? preview.expectedVehicleUpdatedAt ?? undefined : undefined,
  }
}

function currentSalesCustomerValues(document: SalesDocumentLike): NonNullable<SalesDocumentDetails['customerOverride']> {
  const override = document.details.customerOverride
  const base = {
    ...pickCustomerOverride(document.customerDetails),
    ...(override ?? {}),
  }
  return {
    ...base,
    birthDate: normalizeSalesCustomerBirthDate(document.details.customerBirthDate || override?.birthDate || document.customerDetails.birthDate),
    employer: normalizeSalesCustomerEmployer(document.details.customerEmployer || override?.employer || document.customerDetails.employer),
  }
}

function currentSalesVehicleValues(document: SalesDocumentLike): NonNullable<SalesDocumentDetails['vehicleOverride']> {
  return { ...emptyVehicleDetails(), ...(document.vehicleDetails ?? {}), ...(document.details.vehicleOverride ?? {}) }
}

function buildNewSalesCustomer(values: NonNullable<SalesDocumentDetails['customerOverride']>): NonNullable<SalesCreateInput['newCustomer']> {
  const name = values.name.trim()
  if (!name) throw new Error('顧客名を入力してください。')
  return {
    name,
    nameKana: trimSalesOptional(values.kana),
    phone: trimSalesOptional(values.phone),
    email: trimSalesOptional(values.email),
    postalCode: trimSalesOptional(values.postalCode),
    address: trimSalesOptional(values.address),
    birthDate: trimSalesOptional(values.birthDate),
    employer: trimSalesOptional(values.employer),
  }
}

function normalizeSalesCustomerBirthDate(value: string | null | undefined) {
  const normalized = typeof value === 'string' ? value.trim() : ''
  return normalized === 'birth_date' ? '' : normalized
}

function normalizeSalesCustomerBirthDateOnBlur(value: string) {
  return normalizeSalesCustomerBirthDate(value).replaceAll('-', '/')
}

function normalizeSalesCustomerEmployer(value: string | null | undefined) {
  const normalized = typeof value === 'string' ? value.normalize('NFKC').trim() : ''
  return normalized === 'employer' ? '' : normalized
}

function salesCustomerValuesForSave(document: SalesDocumentLike): NonNullable<SalesDocumentDetails['customerOverride']> {
  const values = currentSalesCustomerValues(document)
  return { ...values, birthDate: normalizeSalesCustomerBirthDateOnBlur(values.birthDate) }
}

function buildNewSalesVehicle(values: NonNullable<SalesDocumentDetails['vehicleOverride']>): NonNullable<SalesCreateInput['newVehicle']> {
  const maker = values.maker.trim()
  const name = values.name.trim()
  if (!maker) throw new Error('メーカーを入力してください。')
  if (!name) throw new Error('車名を入力してください。')

  const vehicle: NonNullable<SalesCreateInput['newVehicle']> = { maker, name }
  const model = trimSalesOptional(values.modelType)
  const registrationNumber = trimSalesOptional(values.plate)
  const chassisNumber = trimSalesOptional(values.vin)
  const inspectionDate = normalizeSalesDocumentDate(values.inspectionDate)
  const bodyColor = trimSalesOptional(values.color)
  const transmission = trimSalesOptional(values.transmission)
  const modelYear = parseSalesNumber(values.year)
  const mileage = parseSalesNumber(values.mileage)
  const displacement = parseSalesNumber(values.displacement)
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

function trimSalesOptional(value: string | undefined | null) {
  const trimmed = value?.trim() ?? ''
  return trimmed || undefined
}

function normalizeSalesDocumentDate(value: string) {
  const trimmed = value.trim()
  return trimmed ? trimmed.replaceAll('/', '-') : undefined
}

function parseSalesNumber(value: string | undefined | null) {
  const trimmed = value?.trim() ?? ''
  if (!trimmed) return undefined
  const digits = trimmed.replace(/[^0-9]/g, '')
  if (!digits) return undefined
  const parsed = Number(digits)
  return Number.isFinite(parsed) && parsed >= 0 ? parsed : undefined
}



function SalesDocumentDialog({ form, customers, onChange, onClose, onSubmit }: { form: SalesCreateForm; customers: Customer[]; onChange: (form: SalesCreateForm) => void; onClose: () => void; onSubmit: (event: FormEvent<HTMLFormElement>) => void }) {
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

  return <div className="modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose() }}><section className="modal" role="dialog" aria-modal="true" aria-labelledby="sales-modal-title"><div className="modal-header"><h2 id="sales-modal-title">販売書類を作成</h2><button className="modal-close" type="button" aria-label="閉じる" onClick={onClose}><X size={19} /></button></div><form className="modal-form" onSubmit={onSubmit}><p className="modal-description"><FileText size={16} />顧客・車両を選択して入力を開始します。</p><div className="form-grid"><label className="form-field"><span>書類種別<em>必須</em></span><select required value={form.type} onChange={(event) => onChange({ ...form, type: event.target.value as SalesDocumentType })}><option>見積書</option><option>請求書</option></select></label><label className="form-field"><span>顧客<em>必須</em></span><select required autoFocus aria-label="顧客" value={form.customerMode === 'new' ? NEW_CUSTOMER_VALUE : form.customerId} onChange={(event) => selectCustomer(event.target.value)}><option value="" disabled hidden>顧客を選択してください</option><option value={NEW_CUSTOMER_VALUE}>＋ 新規顧客</option><option value="__separator__" disabled>────────────</option>{customers.map((customer) => <option key={customer.id} value={customer.id}>{customer.name}（{customer.phone || '電話番号未登録'}）</option>)}</select></label><label className="form-field"><span>車両<em>必須</em></span><select required aria-label="車両" value={form.vehicleMode === 'new' ? NEW_VEHICLE_VALUE : form.vehicleId} disabled={form.customerMode === null} onChange={(event) => selectVehicle(event.target.value)}><option value="" disabled hidden>車両を選択してください</option><option value={NEW_VEHICLE_VALUE}>＋ 新規車両</option>{form.customerMode === 'existing' && <><option value="__separator__" disabled>────────────</option>{vehicles.map((vehicle) => <option key={vehicle.id} value={vehicle.id}>{vehicle.maker} {vehicle.model}{vehicle.plate ? `（${vehicle.plate}）` : ''}</option>)}</>}</select></label></div><div className="modal-footer"><button className="button button-secondary" type="button" onClick={onClose}>キャンセル</button><button className="button button-primary" type="submit" disabled={!canStart}><Plus size={16} />入力を開始</button></div></form></section></div>
}

function emptyCreateForm(): SalesCreateForm {
  return { type: '見積書', customerMode: null, customerId: '', vehicleMode: null, vehicleId: '' }
}

function mapCustomerSummaryToRecord(summary: { id: string; name: string; kana: string; phone: string; updatedAt: string }): Customer {
  return { id: summary.id, name: summary.name, kana: summary.kana, phone: summary.phone, email: '', postalCode: '', address: '', birthDate: '', employer: '', memo: '', updatedAt: summary.updatedAt, vehicles: [], isSummary: true }
}

function upsertCustomer(current: Customer[], next: Customer) {
  return current.some((customer) => customer.id === next.id) ? current.map((customer) => customer.id === next.id ? next : customer) : [next, ...current]
}

function dateAfter(days: number) {
  const date = new Date()
  date.setDate(date.getDate() + days)
  return date.toISOString().slice(0, 10)
}

function isValidCreateSelection(form: SalesCreateForm) {
  if (form.customerMode === 'new') return form.vehicleMode === 'new'
  if (form.customerMode !== 'existing') return false
  if (!form.customerId) return false
  return form.vehicleMode === 'new' || (form.vehicleMode === 'existing' && Boolean(form.vehicleId))
}

function todaySalesDisplay() {
  return new Date().toISOString().slice(0, 10).replaceAll('-', '/')
}
