import { sql } from 'drizzle-orm'
import { index, integer, primaryKey, real, sqliteTable, text, uniqueIndex } from 'drizzle-orm/sqlite-core'

const timestamps = {
  createdAt: text('created_at').notNull().default(sql`CURRENT_TIMESTAMP`),
  updatedAt: text('updated_at').notNull().default(sql`CURRENT_TIMESTAMP`),
}

export const staffProfiles = sqliteTable('staff_profiles', {
  uid: text('uid').primaryKey(),
  displayName: text('display_name').notNull(),
  email: text('email'),
  role: text('role').notNull().default('employee'),
  ...timestamps,
})

export const organizations = sqliteTable('organizations', {
  id: text('id').primaryKey(),
  name: text('name').notNull(),
  ownerUid: text('owner_uid'),
  setupCompleted: integer('setup_completed', { mode: 'boolean' }).notNull().default(false),
  ...timestamps,
})

export const documentNumberSequences = sqliteTable('document_number_sequences', {
  organizationId: text('organization_id').notNull().default('org-default'),
  prefix: text('prefix').notNull(),
  year: integer('year').notNull(),
  month: integer('month').notNull(),
  nextSequence: integer('next_sequence').notNull().default(1),
  updatedAt: text('updated_at').notNull().default(sql`CURRENT_TIMESTAMP`),
}, (table) => [
  primaryKey({ columns: [table.organizationId, table.prefix, table.year, table.month] }),
  index('document_number_sequences_organization_id_idx').on(table.organizationId),
])

export const organizationMemberships = sqliteTable('organization_memberships', {
  id: text('id').primaryKey(),
  organizationId: text('organization_id').notNull(),
  uid: text('uid').notNull(),
  role: text('role').notNull().default('employee'),
  status: text('status').notNull().default('active'),
  ...timestamps,
}, (table) => [
  uniqueIndex('organization_memberships_organization_uid_uq').on(table.organizationId, table.uid),
  index('organization_memberships_uid_idx').on(table.uid),
])

export const authAccounts = sqliteTable('auth_accounts', {
  uid: text('uid').primaryKey(),
  mustChangePassword: integer('must_change_password', { mode: 'boolean' }).notNull().default(false),
  initialPasswordIssuedAt: text('initial_password_issued_at'),
  initialPasswordChangedAt: text('initial_password_changed_at'),
  ...timestamps,
})

export const customers = sqliteTable('customers', {
  id: text('id').primaryKey(),
  organizationId: text('organization_id').notNull().default('org-default'),
  customerNumber: text('customer_number').notNull(),
  name: text('name').notNull(),
  nameKana: text('name_kana'),
  postalCode: text('postal_code'),
  address: text('address'),
  phone: text('phone'),
  email: text('email'),
  memo: text('memo'),
  ...timestamps,
}, (table) => [
  uniqueIndex('customers_organization_number_uq').on(table.organizationId, table.customerNumber),
  index('customers_organization_id_idx').on(table.organizationId),
  index('customers_name_idx').on(table.name),
  index('customers_phone_idx').on(table.phone),
])

export const vehicles = sqliteTable('vehicles', {
  id: text('id').primaryKey(),
  organizationId: text('organization_id').notNull().default('org-default'),
  customerId: text('customer_id').notNull().references(() => customers.id, { onDelete: 'cascade' }),
  maker: text('maker'),
  name: text('name').notNull(),
  model: text('model'),
  chassisNumber: text('chassis_number'),
  registrationNumber: text('registration_number'),
  modelYear: integer('model_year'),
  inspectionDate: text('inspection_date'),
  mileage: integer('mileage'),
  bodyColor: text('body_color'),
  displacement: integer('displacement'),
  transmission: text('transmission'),
  inspectionRecordAvailable: integer('inspection_record_available', { mode: 'boolean' }).notNull().default(false),
  freeItem1: text('free_item_1'),
  freeItem2: text('free_item_2'),
  freeItem3: text('free_item_3'),
  memo: text('memo'),
  ...timestamps,
}, (table) => [
  index('vehicles_organization_id_idx').on(table.organizationId),
  index('vehicles_customer_id_idx').on(table.customerId),
  index('vehicles_registration_number_idx').on(table.registrationNumber),
  index('vehicles_inspection_date_idx').on(table.inspectionDate),
])

export const vehicleFiles = sqliteTable('vehicle_files', {
  id: text('id').primaryKey(),
  organizationId: text('organization_id').notNull().default('org-default'),
  vehicleId: text('vehicle_id').notNull().references(() => vehicles.id, { onDelete: 'cascade' }),
  objectKey: text('object_key').notNull(),
  fileName: text('file_name').notNull(),
  contentType: text('content_type').notNull(),
  sizeBytes: integer('size_bytes').notNull(),
  fileKind: text('file_kind').notNull().default('other'),
  ...timestamps,
}, (table) => [
  uniqueIndex('vehicle_files_object_key_uq').on(table.objectKey),
  index('vehicle_files_organization_id_idx').on(table.organizationId),
  index('vehicle_files_vehicle_id_idx').on(table.vehicleId),
])

export const salesDocuments = sqliteTable('sales_documents', {
  id: text('id').primaryKey(),
  organizationId: text('organization_id').notNull().default('org-default'),
  number: text('number').notNull(),
  type: text('type').notNull(),
  status: text('status').notNull().default('下書き'),
  customerId: text('customer_id').notNull().references(() => customers.id),
  vehicleId: text('vehicle_id').references(() => vehicles.id),
  issuedAt: text('issued_at').notNull(),
  dueDate: text('due_date'),
  taxRate: integer('tax_rate').notNull().default(10),
  subtotal: integer('subtotal').notNull().default(0),
  tax: integer('tax').notNull().default(0),
  total: integer('total').notNull().default(0),
  note: text('note'),
  detailsJson: text('details_json').notNull().default('{}'),
  archivedAt: text('archived_at'),
  ...timestamps,
}, (table) => [
  uniqueIndex('sales_documents_organization_number_uq').on(table.organizationId, table.number),
  index('sales_documents_organization_id_idx').on(table.organizationId),
  index('sales_documents_customer_id_idx').on(table.customerId),
  index('sales_documents_vehicle_id_idx').on(table.vehicleId),
  index('sales_documents_status_idx').on(table.status),
])

export const salesDocumentItems = sqliteTable('sales_document_items', {
  id: text('id').primaryKey(),
  organizationId: text('organization_id').notNull().default('org-default'),
  documentId: text('document_id').notNull().references(() => salesDocuments.id, { onDelete: 'cascade' }),
  itemType: text('item_type').notNull().default('その他'),
  description: text('description').notNull(),
  quantity: real('quantity').notNull().default(1),
  unit: text('unit').notNull().default('式'),
  unitPrice: integer('unit_price').notNull().default(0),
  taxCategory: text('tax_category').notNull().default('課税'),
  otherAmount: integer('other_amount').notNull().default(0),
  summary: text('summary').notNull().default(''),
  amount: integer('amount').notNull().default(0),
  sortOrder: integer('sort_order').notNull().default(0),
}, (table) => [
  index('sales_document_items_organization_id_idx').on(table.organizationId),
  index('sales_document_items_document_id_idx').on(table.documentId),
])

export const maintenanceDocuments = sqliteTable('maintenance_documents', {
  id: text('id').primaryKey(),
  organizationId: text('organization_id').notNull().default('org-default'),
  number: text('number').notNull(),
  type: text('type').notNull().default('整備請求書'),
  category: text('category').notNull(),
  status: text('status').notNull().default('下書き'),
  customerId: text('customer_id').notNull().references(() => customers.id),
  vehicleId: text('vehicle_id').notNull().references(() => vehicles.id),
  intakeDate: text('intake_date'),
  plannedReleaseDate: text('planned_release_date'),
  completionDate: text('completion_date'),
  issuedAt: text('issued_at').notNull(),
  dueDate: text('due_date'),
  taxRate: integer('tax_rate').notNull().default(10),
  subtotal: integer('subtotal').notNull().default(0),
  tax: integer('tax').notNull().default(0),
  total: integer('total').notNull().default(0),
  note: text('note'),
  detailsJson: text('details_json').notNull().default('{}'),
  archivedAt: text('archived_at'),
  ...timestamps,
}, (table) => [
  uniqueIndex('maintenance_documents_organization_number_uq').on(table.organizationId, table.number),
  index('maintenance_documents_organization_id_idx').on(table.organizationId),
  index('maintenance_documents_customer_id_idx').on(table.customerId),
  index('maintenance_documents_vehicle_id_idx').on(table.vehicleId),
  index('maintenance_documents_status_idx').on(table.status),
])

export const maintenanceItems = sqliteTable('maintenance_items', {
  id: text('id').primaryKey(),
  organizationId: text('organization_id').notNull().default('org-default'),
  documentId: text('document_id').notNull().references(() => maintenanceDocuments.id, { onDelete: 'cascade' }),
  itemType: text('item_type').notNull().default('作業'),
  description: text('description').notNull(),
  quantity: real('quantity').notNull().default(1),
  unit: text('unit').notNull().default('式'),
  unitPrice: integer('unit_price').notNull().default(0),
  technicalFee: integer('technical_fee').notNull().default(0),
  summary: text('summary').notNull().default(''),
  amount: integer('amount').notNull().default(0),
  sortOrder: integer('sort_order').notNull().default(0),
}, (table) => [
  index('maintenance_items_organization_id_idx').on(table.organizationId),
  index('maintenance_items_document_id_idx').on(table.documentId),
])

export const paymentRecords = sqliteTable('payment_records', {
  id: text('id').primaryKey(),
  organizationId: text('organization_id').notNull().default('org-default'),
  documentType: text('document_type').notNull(),
  documentId: text('document_id').notNull(),
  invoiceAmount: integer('invoice_amount').notNull().default(0),
  paidAmount: integer('paid_amount').notNull().default(0),
  paymentDate: text('payment_date'),
  method: text('method'),
  note: text('note'),
  ...timestamps,
}, (table) => [
  uniqueIndex('payment_records_organization_document_uq').on(table.organizationId, table.documentType, table.documentId),
  index('payment_records_organization_id_idx').on(table.organizationId),
  index('payment_records_payment_date_idx').on(table.paymentDate),
])

export const inspectionSchedules = sqliteTable('inspection_schedules', {
  id: text('id').primaryKey(),
  organizationId: text('organization_id').notNull().default('org-default'),
  customerId: text('customer_id').notNull().references(() => customers.id, { onDelete: 'cascade' }),
  vehicleId: text('vehicle_id').notNull().references(() => vehicles.id, { onDelete: 'cascade' }),
  inspectionType: text('inspection_type').notNull(),
  dueDate: text('due_date').notNull(),
  status: text('status').notNull().default('予定'),
  notifiedAt: text('notified_at'),
  note: text('note'),
  ...timestamps,
}, (table) => [
  index('inspection_schedules_organization_id_idx').on(table.organizationId),
  index('inspection_schedules_vehicle_id_idx').on(table.vehicleId),
  index('inspection_schedules_due_date_idx').on(table.dueDate),
])

export const appSettings = sqliteTable('app_settings', {
  organizationId: text('organization_id').notNull().default('org-default'),
  key: text('key').notNull(),
  value: text('value').notNull(),
  ...timestamps,
}, (table) => [
  primaryKey({ columns: [table.organizationId, table.key] }),
  index('app_settings_organization_id_idx').on(table.organizationId),
])

export const backupRecords = sqliteTable('backup_records', {
  id: text('id').primaryKey(),
  organizationId: text('organization_id').notNull(),
  manifestKey: text('manifest_key').notNull(),
  fileCount: integer('file_count').notNull().default(0),
  rowCount: integer('row_count').notNull().default(0),
  status: text('status').notNull().default('completed'),
  ...timestamps,
}, (table) => [
  uniqueIndex('backup_records_manifest_key_uq').on(table.manifestKey),
  index('backup_records_organization_id_idx').on(table.organizationId),
])

export const databaseSchema = {
  staffProfiles,
  organizations,
  documentNumberSequences,
  organizationMemberships,
  authAccounts,
  customers,
  vehicles,
  vehicleFiles,
  salesDocuments,
  salesDocumentItems,
  maintenanceDocuments,
  maintenanceItems,
  paymentRecords,
  inspectionSchedules,
  appSettings,
  backupRecords,
}
