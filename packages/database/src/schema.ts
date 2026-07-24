import { sql } from 'drizzle-orm'
import { index, integer, real, sqliteTable, text, uniqueIndex } from 'drizzle-orm/sqlite-core'

const timestamps = {
  createdAt: text('created_at').notNull().default(sql`CURRENT_TIMESTAMP`),
  updatedAt: text('updated_at').notNull().default(sql`CURRENT_TIMESTAMP`),
}

export const staffProfiles = sqliteTable('staff_profiles', {
  uid: text('uid').primaryKey(),
  displayName: text('display_name').notNull(),
  email: text('email'),
  role: text('role').notNull().default('staff'),
  ...timestamps,
})

export const customers = sqliteTable('customers', {
  id: text('id').primaryKey(),
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
  uniqueIndex('customers_customer_number_uq').on(table.customerNumber),
  index('customers_name_idx').on(table.name),
  index('customers_phone_idx').on(table.phone),
])

export const vehicles = sqliteTable('vehicles', {
  id: text('id').primaryKey(),
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
  index('vehicles_customer_id_idx').on(table.customerId),
  index('vehicles_registration_number_idx').on(table.registrationNumber),
  index('vehicles_inspection_date_idx').on(table.inspectionDate),
])

export const vehicleFiles = sqliteTable('vehicle_files', {
  id: text('id').primaryKey(),
  vehicleId: text('vehicle_id').notNull().references(() => vehicles.id, { onDelete: 'cascade' }),
  objectKey: text('object_key').notNull(),
  fileName: text('file_name').notNull(),
  contentType: text('content_type').notNull(),
  sizeBytes: integer('size_bytes').notNull(),
  fileKind: text('file_kind').notNull().default('other'),
  ...timestamps,
}, (table) => [
  uniqueIndex('vehicle_files_object_key_uq').on(table.objectKey),
  index('vehicle_files_vehicle_id_idx').on(table.vehicleId),
])

export const salesDocuments = sqliteTable('sales_documents', {
  id: text('id').primaryKey(),
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
  ...timestamps,
}, (table) => [
  uniqueIndex('sales_documents_number_uq').on(table.number),
  index('sales_documents_customer_id_idx').on(table.customerId),
  index('sales_documents_vehicle_id_idx').on(table.vehicleId),
  index('sales_documents_status_idx').on(table.status),
])

export const salesDocumentItems = sqliteTable('sales_document_items', {
  id: text('id').primaryKey(),
  documentId: text('document_id').notNull().references(() => salesDocuments.id, { onDelete: 'cascade' }),
  description: text('description').notNull(),
  quantity: real('quantity').notNull().default(1),
  unit: text('unit').notNull().default('式'),
  unitPrice: integer('unit_price').notNull().default(0),
  amount: integer('amount').notNull().default(0),
  sortOrder: integer('sort_order').notNull().default(0),
}, (table) => [
  index('sales_document_items_document_id_idx').on(table.documentId),
])

export const maintenanceDocuments = sqliteTable('maintenance_documents', {
  id: text('id').primaryKey(),
  number: text('number').notNull(),
  category: text('category').notNull(),
  status: text('status').notNull().default('下書き'),
  customerId: text('customer_id').notNull().references(() => customers.id),
  vehicleId: text('vehicle_id').notNull().references(() => vehicles.id),
  intakeDate: text('intake_date'),
  completionDate: text('completion_date'),
  issuedAt: text('issued_at').notNull(),
  dueDate: text('due_date'),
  taxRate: integer('tax_rate').notNull().default(10),
  subtotal: integer('subtotal').notNull().default(0),
  tax: integer('tax').notNull().default(0),
  total: integer('total').notNull().default(0),
  note: text('note'),
  ...timestamps,
}, (table) => [
  uniqueIndex('maintenance_documents_number_uq').on(table.number),
  index('maintenance_documents_customer_id_idx').on(table.customerId),
  index('maintenance_documents_vehicle_id_idx').on(table.vehicleId),
  index('maintenance_documents_status_idx').on(table.status),
])

export const maintenanceItems = sqliteTable('maintenance_items', {
  id: text('id').primaryKey(),
  documentId: text('document_id').notNull().references(() => maintenanceDocuments.id, { onDelete: 'cascade' }),
  itemType: text('item_type').notNull().default('作業'),
  description: text('description').notNull(),
  quantity: real('quantity').notNull().default(1),
  unit: text('unit').notNull().default('式'),
  unitPrice: integer('unit_price').notNull().default(0),
  amount: integer('amount').notNull().default(0),
  sortOrder: integer('sort_order').notNull().default(0),
}, (table) => [
  index('maintenance_items_document_id_idx').on(table.documentId),
])

export const paymentRecords = sqliteTable('payment_records', {
  id: text('id').primaryKey(),
  documentType: text('document_type').notNull(),
  documentId: text('document_id').notNull(),
  invoiceAmount: integer('invoice_amount').notNull().default(0),
  paidAmount: integer('paid_amount').notNull().default(0),
  paymentDate: text('payment_date'),
  method: text('method'),
  note: text('note'),
  ...timestamps,
}, (table) => [
  uniqueIndex('payment_records_document_uq').on(table.documentType, table.documentId),
  index('payment_records_payment_date_idx').on(table.paymentDate),
])

export const inspectionSchedules = sqliteTable('inspection_schedules', {
  id: text('id').primaryKey(),
  customerId: text('customer_id').notNull().references(() => customers.id, { onDelete: 'cascade' }),
  vehicleId: text('vehicle_id').notNull().references(() => vehicles.id, { onDelete: 'cascade' }),
  inspectionType: text('inspection_type').notNull(),
  dueDate: text('due_date').notNull(),
  status: text('status').notNull().default('予定'),
  notifiedAt: text('notified_at'),
  note: text('note'),
  ...timestamps,
}, (table) => [
  index('inspection_schedules_vehicle_id_idx').on(table.vehicleId),
  index('inspection_schedules_due_date_idx').on(table.dueDate),
])

export const appSettings = sqliteTable('app_settings', {
  key: text('key').primaryKey(),
  value: text('value').notNull(),
  ...timestamps,
})

export const databaseSchema = {
  staffProfiles,
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
}
