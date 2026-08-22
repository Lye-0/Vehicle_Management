import { describe, expect, it } from 'vitest'
import { HttpError } from '../../http'
import { maximumD1BatchStatementCount, maximumDocumentItemCount, assertD1BatchStatementCount } from '../resource-limits'
import { parseMaintenanceItems as parseMaintenanceDocumentItems } from '../../routes/maintenance-routes'
import { parseSalesItems as parseSalesDocumentItems } from '../../routes/sales-routes'
import { parseMaintenanceItems as parseMaintenanceImportItems, parseSalesItems as parseSalesImportItems } from '../../routes/import-routes'

describe('resource limits', () => {
  it('rejects a D1 batch over the application statement budget', () => {
    expect(() => assertD1BatchStatementCount(maximumD1BatchStatementCount + 1)).toThrow(HttpError)
  })

  it('rejects oversized document item arrays before statement creation', () => {
    const items = Array.from({ length: maximumDocumentItemCount + 1 }, () => ({}))
    expect(() => parseSalesDocumentItems(items)).toThrow(HttpError)
    expect(() => parseMaintenanceDocumentItems(items)).toThrow(HttpError)
  })

  it('keeps explicitly blank maintenance detail values empty while calculating them as zero', () => {
    const [item] = parseMaintenanceDocumentItems([{ description: '', quantity: 0, unit: '', unitPrice: null, technicalFee: null, summary: '' }])
    expect(item).toMatchObject({ quantity: 0, unit: '', unitPrice: 0, technicalFee: 0, amount: 0 })
  })

  it('rejects oversized CSV detail arrays and delimited fields', () => {
    const details = JSON.stringify(Array.from({ length: maximumDocumentItemCount + 1 }, () => ({})))
    const delimited = Array.from({ length: maximumDocumentItemCount + 1 }, () => '明細').join(' / ')
    expect(() => parseSalesImportItems('', details)).toThrow(HttpError)
    expect(() => parseSalesImportItems(delimited, '')).toThrow(HttpError)
    expect(() => parseMaintenanceImportItems(delimited)).toThrow(HttpError)
  })
})
