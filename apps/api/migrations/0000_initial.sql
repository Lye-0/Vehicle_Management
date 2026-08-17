CREATE TABLE `app_settings` (
	`organization_id` text DEFAULT 'org-default' NOT NULL,
	`key` text NOT NULL,
	`value` text NOT NULL,
	`created_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	`updated_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	PRIMARY KEY(`organization_id`, `key`)
);
--> statement-breakpoint
CREATE INDEX `app_settings_organization_id_idx` ON `app_settings` (`organization_id`);--> statement-breakpoint
CREATE TABLE `auth_accounts` (
	`uid` text PRIMARY KEY NOT NULL,
	`must_change_password` integer DEFAULT false NOT NULL,
	`initial_password_issued_at` text,
	`initial_password_changed_at` text,
	`created_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	`updated_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL
);
--> statement-breakpoint
CREATE TABLE `backup_records` (
	`id` text PRIMARY KEY NOT NULL,
	`organization_id` text NOT NULL,
	`manifest_key` text NOT NULL,
	`file_count` integer DEFAULT 0 NOT NULL,
	`row_count` integer DEFAULT 0 NOT NULL,
	`status` text DEFAULT 'completed' NOT NULL,
	`trigger` text DEFAULT 'manual' NOT NULL,
	`note` text DEFAULT '' NOT NULL,
	`protected_until` text,
	`keep_forever` integer DEFAULT false NOT NULL,
	`created_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	`updated_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL
);
--> statement-breakpoint
CREATE UNIQUE INDEX `backup_records_manifest_key_uq` ON `backup_records` (`manifest_key`);--> statement-breakpoint
CREATE INDEX `backup_records_organization_id_idx` ON `backup_records` (`organization_id`);--> statement-breakpoint
CREATE TABLE `customers` (
	`id` text PRIMARY KEY NOT NULL,
	`organization_id` text DEFAULT 'org-default' NOT NULL,
	`customer_number` text NOT NULL,
	`name` text NOT NULL,
	`name_kana` text,
	`postal_code` text,
	`address` text,
	`phone` text,
	`email` text,
	`birth_date` text,
	`employer` text,
	`memo` text,
	`created_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	`updated_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL
);
--> statement-breakpoint
CREATE UNIQUE INDEX `customers_organization_number_uq` ON `customers` (`organization_id`,`customer_number`);--> statement-breakpoint
CREATE INDEX `customers_organization_id_idx` ON `customers` (`organization_id`);--> statement-breakpoint
CREATE INDEX `customers_name_idx` ON `customers` (`name`);--> statement-breakpoint
CREATE INDEX `customers_phone_idx` ON `customers` (`phone`);--> statement-breakpoint
CREATE TABLE `document_number_sequences` (
	`organization_id` text DEFAULT 'org-default' NOT NULL,
	`prefix` text NOT NULL,
	`year` integer NOT NULL,
	`month` integer NOT NULL,
	`next_sequence` integer DEFAULT 1 NOT NULL,
	`updated_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	PRIMARY KEY(`organization_id`, `prefix`, `year`, `month`)
);
--> statement-breakpoint
CREATE INDEX `document_number_sequences_organization_id_idx` ON `document_number_sequences` (`organization_id`);--> statement-breakpoint
CREATE TABLE `inspection_schedules` (
	`id` text PRIMARY KEY NOT NULL,
	`organization_id` text DEFAULT 'org-default' NOT NULL,
	`customer_id` text NOT NULL,
	`vehicle_id` text NOT NULL,
	`inspection_type` text NOT NULL,
	`due_date` text NOT NULL,
	`status` text DEFAULT '予定' NOT NULL,
	`notified_at` text,
	`note` text,
	`created_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	`updated_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	FOREIGN KEY (`customer_id`) REFERENCES `customers`(`id`) ON UPDATE no action ON DELETE cascade,
	FOREIGN KEY (`vehicle_id`) REFERENCES `vehicles`(`id`) ON UPDATE no action ON DELETE cascade
);
--> statement-breakpoint
CREATE INDEX `inspection_schedules_organization_id_idx` ON `inspection_schedules` (`organization_id`);--> statement-breakpoint
CREATE INDEX `inspection_schedules_vehicle_id_idx` ON `inspection_schedules` (`vehicle_id`);--> statement-breakpoint
CREATE INDEX `inspection_schedules_due_date_idx` ON `inspection_schedules` (`due_date`);--> statement-breakpoint
CREATE TABLE `maintenance_documents` (
	`id` text PRIMARY KEY NOT NULL,
	`organization_id` text DEFAULT 'org-default' NOT NULL,
	`number` text NOT NULL,
	`type` text DEFAULT '整備請求書' NOT NULL,
	`category` text NOT NULL,
	`status` text DEFAULT '下書き' NOT NULL,
	`customer_id` text NOT NULL,
	`vehicle_id` text,
	`intake_date` text,
	`planned_release_date` text,
	`completion_date` text,
	`issued_at` text NOT NULL,
	`due_date` text,
	`tax_rate` integer DEFAULT 10 NOT NULL,
	`tax_rounding` text DEFAULT '切り捨て' NOT NULL,
	`subtotal` integer DEFAULT 0 NOT NULL,
	`tax` integer DEFAULT 0 NOT NULL,
	`total` integer DEFAULT 0 NOT NULL,
	`note` text,
	`details_json` text DEFAULT '{}' NOT NULL,
	`archived_at` text,
	`archived_previous_status` text,
	`archived_by` text,
	`purge_at` text,
	`keep_forever` integer DEFAULT false NOT NULL,
	`created_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	`updated_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	FOREIGN KEY (`customer_id`) REFERENCES `customers`(`id`) ON UPDATE no action ON DELETE no action,
	FOREIGN KEY (`vehicle_id`) REFERENCES `vehicles`(`id`) ON UPDATE no action ON DELETE no action
);
--> statement-breakpoint
CREATE UNIQUE INDEX `maintenance_documents_organization_number_uq` ON `maintenance_documents` (`organization_id`,`number`);--> statement-breakpoint
CREATE INDEX `maintenance_documents_organization_id_idx` ON `maintenance_documents` (`organization_id`);--> statement-breakpoint
CREATE INDEX `maintenance_documents_customer_id_idx` ON `maintenance_documents` (`customer_id`);--> statement-breakpoint
CREATE INDEX `maintenance_documents_vehicle_id_idx` ON `maintenance_documents` (`vehicle_id`);--> statement-breakpoint
CREATE INDEX `maintenance_documents_status_idx` ON `maintenance_documents` (`status`);--> statement-breakpoint
CREATE TABLE `maintenance_items` (
	`id` text PRIMARY KEY NOT NULL,
	`organization_id` text DEFAULT 'org-default' NOT NULL,
	`document_id` text NOT NULL,
	`item_type` text DEFAULT '作業' NOT NULL,
	`description` text NOT NULL,
	`quantity` real DEFAULT 1 NOT NULL,
	`unit` text DEFAULT '式' NOT NULL,
	`unit_price` integer DEFAULT 0 NOT NULL,
	`technical_fee` integer DEFAULT 0 NOT NULL,
	`summary` text DEFAULT '' NOT NULL,
	`amount` integer DEFAULT 0 NOT NULL,
	`sort_order` integer DEFAULT 0 NOT NULL,
	FOREIGN KEY (`document_id`) REFERENCES `maintenance_documents`(`id`) ON UPDATE no action ON DELETE cascade
);
--> statement-breakpoint
CREATE INDEX `maintenance_items_organization_id_idx` ON `maintenance_items` (`organization_id`);--> statement-breakpoint
CREATE INDEX `maintenance_items_document_id_idx` ON `maintenance_items` (`document_id`);--> statement-breakpoint
CREATE TABLE `mileage_histories` (
	`id` text PRIMARY KEY NOT NULL,
	`organization_id` text NOT NULL,
	`vehicle_id` text NOT NULL,
	`maintenance_document_id` text NOT NULL,
	`mileage` integer NOT NULL,
	`created_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	`updated_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	FOREIGN KEY (`vehicle_id`) REFERENCES `vehicles`(`id`) ON UPDATE no action ON DELETE cascade,
	FOREIGN KEY (`maintenance_document_id`) REFERENCES `maintenance_documents`(`id`) ON UPDATE no action ON DELETE cascade
);
--> statement-breakpoint
CREATE INDEX `mileage_histories_organization_id_idx` ON `mileage_histories` (`organization_id`);--> statement-breakpoint
CREATE INDEX `mileage_histories_vehicle_id_idx` ON `mileage_histories` (`vehicle_id`);--> statement-breakpoint
CREATE UNIQUE INDEX `mileage_histories_organization_document_uq` ON `mileage_histories` (`organization_id`,`maintenance_document_id`);--> statement-breakpoint
CREATE TABLE `organization_invites` (
	`id` text PRIMARY KEY NOT NULL,
	`organization_id` text NOT NULL,
	`email` text NOT NULL,
	`token_hash` text NOT NULL,
	`role` text DEFAULT 'employee' NOT NULL,
	`status` text DEFAULT 'pending' NOT NULL,
	`expires_at` text NOT NULL,
	`created_by_uid` text NOT NULL,
	`accepted_uid` text,
	`created_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	`updated_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL
);
--> statement-breakpoint
CREATE UNIQUE INDEX `organization_invites_token_hash_uq` ON `organization_invites` (`token_hash`);--> statement-breakpoint
CREATE INDEX `organization_invites_organization_email_idx` ON `organization_invites` (`organization_id`,`email`);--> statement-breakpoint
CREATE INDEX `organization_invites_status_expires_idx` ON `organization_invites` (`status`,`expires_at`);--> statement-breakpoint
CREATE TABLE `organization_memberships` (
	`id` text PRIMARY KEY NOT NULL,
	`organization_id` text NOT NULL,
	`uid` text NOT NULL,
	`role` text DEFAULT 'employee' NOT NULL,
	`status` text DEFAULT 'active' NOT NULL,
	`created_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	`updated_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL
);
--> statement-breakpoint
CREATE UNIQUE INDEX `organization_memberships_organization_uid_uq` ON `organization_memberships` (`organization_id`,`uid`);--> statement-breakpoint
CREATE INDEX `organization_memberships_uid_idx` ON `organization_memberships` (`uid`);--> statement-breakpoint
CREATE TABLE `organization_permissions` (
	`organization_id` text PRIMARY KEY NOT NULL,
	`employee_can_export_csv` integer DEFAULT true NOT NULL,
	`employee_can_edit_shop` integer DEFAULT true NOT NULL,
	`employee_can_edit_tax` integer DEFAULT true NOT NULL,
	`employee_can_create_restore_backup` integer DEFAULT true NOT NULL,
	`employee_can_manage_backup_retention` integer DEFAULT false NOT NULL,
	`employee_can_manage_archive_retention` integer DEFAULT false NOT NULL,
	`created_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	`updated_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL
);
--> statement-breakpoint
CREATE TABLE `organizations` (
	`id` text PRIMARY KEY NOT NULL,
	`name` text NOT NULL,
	`owner_uid` text,
	`setup_completed` integer DEFAULT false NOT NULL,
	`created_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	`updated_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL
);
--> statement-breakpoint
INSERT OR IGNORE INTO `organizations` (`id`, `name`, `owner_uid`, `setup_completed`) VALUES ('org-default', '東京都心支店', NULL, 0);
--> statement-breakpoint
CREATE TABLE `payment_entries` (
	`id` text PRIMARY KEY NOT NULL,
	`organization_id` text DEFAULT 'org-default' NOT NULL,
	`document_type` text NOT NULL,
	`document_id` text NOT NULL,
	`amount` integer DEFAULT 0 NOT NULL,
	`payment_date` text,
	`method` text,
	`note` text DEFAULT '' NOT NULL,
	`created_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	`updated_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL
);
--> statement-breakpoint
CREATE INDEX `payment_entries_organization_document_idx` ON `payment_entries` (`organization_id`,`document_type`,`document_id`);--> statement-breakpoint
CREATE INDEX `payment_entries_payment_date_idx` ON `payment_entries` (`payment_date`);--> statement-breakpoint
CREATE TABLE `payment_records` (
	`id` text PRIMARY KEY NOT NULL,
	`organization_id` text DEFAULT 'org-default' NOT NULL,
	`document_type` text NOT NULL,
	`document_id` text NOT NULL,
	`invoice_amount` integer DEFAULT 0 NOT NULL,
	`paid_amount` integer DEFAULT 0 NOT NULL,
	`payment_date` text,
	`method` text,
	`note` text,
	`created_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	`updated_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL
);
--> statement-breakpoint
CREATE UNIQUE INDEX `payment_records_organization_document_uq` ON `payment_records` (`organization_id`,`document_type`,`document_id`);--> statement-breakpoint
CREATE INDEX `payment_records_organization_id_idx` ON `payment_records` (`organization_id`);--> statement-breakpoint
CREATE INDEX `payment_records_payment_date_idx` ON `payment_records` (`payment_date`);--> statement-breakpoint
CREATE TABLE `sales_document_items` (
	`id` text PRIMARY KEY NOT NULL,
	`organization_id` text DEFAULT 'org-default' NOT NULL,
	`document_id` text NOT NULL,
	`item_type` text DEFAULT 'その他' NOT NULL,
	`description` text NOT NULL,
	`quantity` real DEFAULT 1 NOT NULL,
	`unit` text DEFAULT '式' NOT NULL,
	`unit_price` integer DEFAULT 0 NOT NULL,
	`tax_category` text DEFAULT '課税' NOT NULL,
	`other_amount` integer DEFAULT 0 NOT NULL,
	`summary` text DEFAULT '' NOT NULL,
	`amount` integer DEFAULT 0 NOT NULL,
	`sort_order` integer DEFAULT 0 NOT NULL,
	FOREIGN KEY (`document_id`) REFERENCES `sales_documents`(`id`) ON UPDATE no action ON DELETE cascade
);
--> statement-breakpoint
CREATE INDEX `sales_document_items_organization_id_idx` ON `sales_document_items` (`organization_id`);--> statement-breakpoint
CREATE INDEX `sales_document_items_document_id_idx` ON `sales_document_items` (`document_id`);--> statement-breakpoint
CREATE TABLE `sales_documents` (
	`id` text PRIMARY KEY NOT NULL,
	`organization_id` text DEFAULT 'org-default' NOT NULL,
	`number` text NOT NULL,
	`type` text NOT NULL,
	`status` text DEFAULT '下書き' NOT NULL,
	`customer_id` text NOT NULL,
	`vehicle_id` text,
	`issued_at` text NOT NULL,
	`due_date` text,
	`tax_rate` integer DEFAULT 10 NOT NULL,
	`tax_rounding` text DEFAULT '切り捨て' NOT NULL,
	`subtotal` integer DEFAULT 0 NOT NULL,
	`tax` integer DEFAULT 0 NOT NULL,
	`total` integer DEFAULT 0 NOT NULL,
	`note` text,
	`details_json` text DEFAULT '{}' NOT NULL,
	`archived_at` text,
	`archived_previous_status` text,
	`archived_by` text,
	`purge_at` text,
	`keep_forever` integer DEFAULT false NOT NULL,
	`created_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	`updated_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	FOREIGN KEY (`customer_id`) REFERENCES `customers`(`id`) ON UPDATE no action ON DELETE no action,
	FOREIGN KEY (`vehicle_id`) REFERENCES `vehicles`(`id`) ON UPDATE no action ON DELETE no action
);
--> statement-breakpoint
CREATE UNIQUE INDEX `sales_documents_organization_number_uq` ON `sales_documents` (`organization_id`,`number`);--> statement-breakpoint
CREATE INDEX `sales_documents_organization_id_idx` ON `sales_documents` (`organization_id`);--> statement-breakpoint
CREATE INDEX `sales_documents_customer_id_idx` ON `sales_documents` (`customer_id`);--> statement-breakpoint
CREATE INDEX `sales_documents_vehicle_id_idx` ON `sales_documents` (`vehicle_id`);--> statement-breakpoint
CREATE INDEX `sales_documents_status_idx` ON `sales_documents` (`status`);--> statement-breakpoint
CREATE TABLE `shared_schedules` (
	`id` text PRIMARY KEY NOT NULL,
	`organization_id` text DEFAULT 'org-default' NOT NULL,
	`title` text NOT NULL,
	`start_date` text NOT NULL,
	`end_date` text NOT NULL,
	`detail` text DEFAULT '' NOT NULL,
	`created_by_uid` text NOT NULL,
	`created_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	`updated_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL
);
--> statement-breakpoint
CREATE INDEX `shared_schedules_organization_date_idx` ON `shared_schedules` (`organization_id`,`start_date`,`end_date`);--> statement-breakpoint
CREATE INDEX `shared_schedules_created_by_uid_idx` ON `shared_schedules` (`created_by_uid`);--> statement-breakpoint
CREATE TABLE `staff_profiles` (
	`uid` text PRIMARY KEY NOT NULL,
	`display_name` text NOT NULL,
	`email` text,
	`role` text DEFAULT 'employee' NOT NULL,
	`created_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	`updated_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL
);
--> statement-breakpoint
CREATE TABLE `vehicle_files` (
	`id` text PRIMARY KEY NOT NULL,
	`organization_id` text DEFAULT 'org-default' NOT NULL,
	`vehicle_id` text NOT NULL,
	`object_key` text NOT NULL,
	`file_name` text NOT NULL,
	`content_type` text NOT NULL,
	`size_bytes` integer NOT NULL,
	`file_kind` text DEFAULT 'other' NOT NULL,
	`created_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	`updated_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	FOREIGN KEY (`vehicle_id`) REFERENCES `vehicles`(`id`) ON UPDATE no action ON DELETE cascade
);
--> statement-breakpoint
CREATE UNIQUE INDEX `vehicle_files_object_key_uq` ON `vehicle_files` (`object_key`);--> statement-breakpoint
CREATE INDEX `vehicle_files_organization_id_idx` ON `vehicle_files` (`organization_id`);--> statement-breakpoint
CREATE INDEX `vehicle_files_vehicle_id_idx` ON `vehicle_files` (`vehicle_id`);--> statement-breakpoint
CREATE TABLE `vehicles` (
	`id` text PRIMARY KEY NOT NULL,
	`organization_id` text DEFAULT 'org-default' NOT NULL,
	`customer_id` text NOT NULL,
	`maker` text,
	`name` text NOT NULL,
	`model` text,
	`chassis_number` text,
	`registration_number` text,
	`model_year` integer,
	`inspection_date` text,
	`mileage` integer,
	`body_color` text,
	`displacement` integer,
	`transmission` text,
	`inspection_record_available` integer DEFAULT false NOT NULL,
	`free_item_1` text,
	`free_item_2` text,
	`free_item_3` text,
	`memo` text,
	`created_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	`updated_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	FOREIGN KEY (`customer_id`) REFERENCES `customers`(`id`) ON UPDATE no action ON DELETE cascade
);
--> statement-breakpoint
CREATE INDEX `vehicles_organization_id_idx` ON `vehicles` (`organization_id`);--> statement-breakpoint
CREATE INDEX `vehicles_customer_id_idx` ON `vehicles` (`customer_id`);--> statement-breakpoint
CREATE INDEX `vehicles_registration_number_idx` ON `vehicles` (`registration_number`);--> statement-breakpoint
CREATE INDEX `vehicles_inspection_date_idx` ON `vehicles` (`inspection_date`);
