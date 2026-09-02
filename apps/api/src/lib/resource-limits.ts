import { HttpError } from '../http'

export const maximumDocumentItemCount = 100
export const maximumCsvDetailItemCount = 100
// 現行の大規模なグラフ確定パッケージは2,062文を必要とするため、
// 同じD1バッチの原子性を保ったまま、今後の明細増加分を含めて3,000文まで許可する。
export const maximumD1BatchStatementCount = 3_000

export function assertArrayLength(value: unknown, maximum: number, message: string) {
  if (Array.isArray(value) && value.length > maximum) throw new HttpError(413, message)
}

export function assertD1BatchStatementCount(count: number) {
  if (count > maximumD1BatchStatementCount) throw new HttpError(413, '一度に処理できるデータ量が上限を超えています。')
}

export function pushD1BatchStatement<T>(statements: T[], statement: T) {
  if (statements.length >= maximumD1BatchStatementCount) throw new HttpError(413, '一度に処理できるデータ量が上限を超えています。')
  statements.push(statement)
}
