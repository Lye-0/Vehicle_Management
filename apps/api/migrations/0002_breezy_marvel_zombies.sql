CREATE TABLE `auth_accounts` (
	`uid` text PRIMARY KEY NOT NULL,
	`must_change_password` integer DEFAULT false NOT NULL,
	`initial_password_issued_at` text,
	`initial_password_changed_at` text,
	`created_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	`updated_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL
);
--> statement-breakpoint
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
DROP INDEX `customers_customer_number_uq`;--> statement-breakpoint
ALTER TABLE `customers` ADD `organization_id` text DEFAULT 'org-default' NOT NULL;--> statement-breakpoint
CREATE UNIQUE INDEX `customers_organization_number_uq` ON `customers` (`organization_id`,`customer_number`);--> statement-breakpoint
CREATE INDEX `customers_organization_id_idx` ON `customers` (`organization_id`);--> statement-breakpoint
DROP INDEX `maintenance_documents_number_uq`;--> statement-breakpoint
ALTER TABLE `maintenance_documents` ADD `organization_id` text DEFAULT 'org-default' NOT NULL;--> statement-breakpoint
CREATE UNIQUE INDEX `maintenance_documents_organization_number_uq` ON `maintenance_documents` (`organization_id`,`number`);--> statement-breakpoint
CREATE INDEX `maintenance_documents_organization_id_idx` ON `maintenance_documents` (`organization_id`);--> statement-breakpoint
DROP INDEX `sales_documents_number_uq`;--> statement-breakpoint
ALTER TABLE `sales_documents` ADD `organization_id` text DEFAULT 'org-default' NOT NULL;--> statement-breakpoint
CREATE UNIQUE INDEX `sales_documents_organization_number_uq` ON `sales_documents` (`organization_id`,`number`);--> statement-breakpoint
CREATE INDEX `sales_documents_organization_id_idx` ON `sales_documents` (`organization_id`);--> statement-breakpoint
PRAGMA foreign_keys=OFF;--> statement-breakpoint
CREATE TABLE `__new_app_settings` (
	`organization_id` text DEFAULT 'org-default' NOT NULL,
	`key` text NOT NULL,
	`value` text NOT NULL,
	`created_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	`updated_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	PRIMARY KEY(`organization_id`, `key`)
);
--> statement-breakpoint
INSERT INTO `__new_app_settings`("organization_id", "key", "value", "created_at", "updated_at") SELECT 'org-default', "key", "value", "created_at", "updated_at" FROM `app_settings`;--> statement-breakpoint
DROP TABLE `app_settings`;--> statement-breakpoint
ALTER TABLE `__new_app_settings` RENAME TO `app_settings`;--> statement-breakpoint
PRAGMA foreign_keys=ON;--> statement-breakpoint
CREATE INDEX `app_settings_organization_id_idx` ON `app_settings` (`organization_id`);--> statement-breakpoint
CREATE TABLE `__new_staff_profiles` (
	`uid` text PRIMARY KEY NOT NULL,
	`display_name` text NOT NULL,
	`email` text,
	`role` text DEFAULT 'employee' NOT NULL,
	`created_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	`updated_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL
);
--> statement-breakpoint
INSERT INTO `__new_staff_profiles`("uid", "display_name", "email", "role", "created_at", "updated_at") SELECT "uid", "display_name", "email", "role", "created_at", "updated_at" FROM `staff_profiles`;--> statement-breakpoint
DROP TABLE `staff_profiles`;--> statement-breakpoint
ALTER TABLE `__new_staff_profiles` RENAME TO `staff_profiles`;--> statement-breakpoint
ALTER TABLE `inspection_schedules` ADD `organization_id` text DEFAULT 'org-default' NOT NULL;--> statement-breakpoint
CREATE INDEX `inspection_schedules_organization_id_idx` ON `inspection_schedules` (`organization_id`);--> statement-breakpoint
ALTER TABLE `maintenance_items` ADD `organization_id` text DEFAULT 'org-default' NOT NULL;--> statement-breakpoint
CREATE INDEX `maintenance_items_organization_id_idx` ON `maintenance_items` (`organization_id`);--> statement-breakpoint
ALTER TABLE `payment_records` ADD `organization_id` text DEFAULT 'org-default' NOT NULL;--> statement-breakpoint
CREATE INDEX `payment_records_organization_id_idx` ON `payment_records` (`organization_id`);--> statement-breakpoint
ALTER TABLE `sales_document_items` ADD `organization_id` text DEFAULT 'org-default' NOT NULL;--> statement-breakpoint
CREATE INDEX `sales_document_items_organization_id_idx` ON `sales_document_items` (`organization_id`);--> statement-breakpoint
ALTER TABLE `vehicle_files` ADD `organization_id` text DEFAULT 'org-default' NOT NULL;--> statement-breakpoint
CREATE INDEX `vehicle_files_organization_id_idx` ON `vehicle_files` (`organization_id`);--> statement-breakpoint
ALTER TABLE `vehicles` ADD `organization_id` text DEFAULT 'org-default' NOT NULL;--> statement-breakpoint
CREATE INDEX `vehicles_organization_id_idx` ON `vehicles` (`organization_id`);
