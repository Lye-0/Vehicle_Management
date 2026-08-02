export type DocumentSortKey = 'issuedAt' | 'dueDate' | 'customerName' | 'vehicle'
export type DocumentSortDirection = 'asc' | 'desc'

type SortableDocument = { issuedAt: string; dueDate: string; customerName: string; vehicle: string }

export function compareSortableDocuments(left: SortableDocument, right: SortableDocument, key: DocumentSortKey, direction: DocumentSortDirection) {
  const leftValue = left[key]
  const rightValue = right[key]
  if (!leftValue || !rightValue) return !leftValue && !rightValue ? 0 : !leftValue ? 1 : -1
  const comparison = leftValue.localeCompare(rightValue, 'ja-JP', { numeric: true, sensitivity: 'base' })
  return direction === 'asc' ? comparison : -comparison
}
