CREATE TABLE `app_settings` (
	`key` text PRIMARY KEY NOT NULL,
	`value` text NOT NULL,
	`created_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	`updated_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL
);
--> statement-breakpoint
CREATE TABLE `customers` (
	`id` text PRIMARY KEY NOT NULL,
	`customer_number` text NOT NULL,
	`name` text NOT NULL,
	`name_kana` text,
	`postal_code` text,
	`address` text,
	`phone` text,
	`email` text,
	`memo` text,
	`created_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	`updated_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL
);
--> statement-breakpoint
CREATE UNIQUE INDEX `customers_customer_number_uq` ON `customers` (`customer_number`);--> statement-breakpoint
CREATE INDEX `customers_name_idx` ON `customers` (`name`);--> statement-breakpoint
CREATE INDEX `customers_phone_idx` ON `customers` (`phone`);--> statement-breakpoint
CREATE TABLE `inspection_schedules` (
	`id` text PRIMARY KEY NOT NULL,
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
CREATE INDEX `inspection_schedules_vehicle_id_idx` ON `inspection_schedules` (`vehicle_id`);--> statement-breakpoint
CREATE INDEX `inspection_schedules_due_date_idx` ON `inspection_schedules` (`due_date`);--> statement-breakpoint
CREATE TABLE `maintenance_documents` (
	`id` text PRIMARY KEY NOT NULL,
	`number` text NOT NULL,
	`category` text NOT NULL,
	`status` text DEFAULT '下書き' NOT NULL,
	`customer_id` text NOT NULL,
	`vehicle_id` text NOT NULL,
	`intake_date` text,
	`completion_date` text,
	`issued_at` text NOT NULL,
	`due_date` text,
	`tax_rate` integer DEFAULT 10 NOT NULL,
	`subtotal` integer DEFAULT 0 NOT NULL,
	`tax` integer DEFAULT 0 NOT NULL,
	`total` integer DEFAULT 0 NOT NULL,
	`note` text,
	`created_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	`updated_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	FOREIGN KEY (`customer_id`) REFERENCES `customers`(`id`) ON UPDATE no action ON DELETE no action,
	FOREIGN KEY (`vehicle_id`) REFERENCES `vehicles`(`id`) ON UPDATE no action ON DELETE no action
);
--> statement-breakpoint
CREATE UNIQUE INDEX `maintenance_documents_number_uq` ON `maintenance_documents` (`number`);--> statement-breakpoint
CREATE INDEX `maintenance_documents_customer_id_idx` ON `maintenance_documents` (`customer_id`);--> statement-breakpoint
CREATE INDEX `maintenance_documents_vehicle_id_idx` ON `maintenance_documents` (`vehicle_id`);--> statement-breakpoint
CREATE INDEX `maintenance_documents_status_idx` ON `maintenance_documents` (`status`);--> statement-breakpoint
CREATE TABLE `maintenance_items` (
	`id` text PRIMARY KEY NOT NULL,
	`document_id` text NOT NULL,
	`item_type` text DEFAULT '作業' NOT NULL,
	`description` text NOT NULL,
	`quantity` real DEFAULT 1 NOT NULL,
	`unit` text DEFAULT '式' NOT NULL,
	`unit_price` integer DEFAULT 0 NOT NULL,
	`amount` integer DEFAULT 0 NOT NULL,
	`sort_order` integer DEFAULT 0 NOT NULL,
	FOREIGN KEY (`document_id`) REFERENCES `maintenance_documents`(`id`) ON UPDATE no action ON DELETE cascade
);
--> statement-breakpoint
CREATE INDEX `maintenance_items_document_id_idx` ON `maintenance_items` (`document_id`);--> statement-breakpoint
CREATE TABLE `payment_records` (
	`id` text PRIMARY KEY NOT NULL,
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
CREATE UNIQUE INDEX `payment_records_document_uq` ON `payment_records` (`document_type`,`document_id`);--> statement-breakpoint
CREATE INDEX `payment_records_payment_date_idx` ON `payment_records` (`payment_date`);--> statement-breakpoint
CREATE TABLE `sales_document_items` (
	`id` text PRIMARY KEY NOT NULL,
	`document_id` text NOT NULL,
	`description` text NOT NULL,
	`quantity` real DEFAULT 1 NOT NULL,
	`unit` text DEFAULT '式' NOT NULL,
	`unit_price` integer DEFAULT 0 NOT NULL,
	`amount` integer DEFAULT 0 NOT NULL,
	`sort_order` integer DEFAULT 0 NOT NULL,
	FOREIGN KEY (`document_id`) REFERENCES `sales_documents`(`id`) ON UPDATE no action ON DELETE cascade
);
--> statement-breakpoint
CREATE INDEX `sales_document_items_document_id_idx` ON `sales_document_items` (`document_id`);--> statement-breakpoint
CREATE TABLE `sales_documents` (
	`id` text PRIMARY KEY NOT NULL,
	`number` text NOT NULL,
	`type` text NOT NULL,
	`status` text DEFAULT '下書き' NOT NULL,
	`customer_id` text NOT NULL,
	`vehicle_id` text,
	`issued_at` text NOT NULL,
	`due_date` text,
	`tax_rate` integer DEFAULT 10 NOT NULL,
	`subtotal` integer DEFAULT 0 NOT NULL,
	`tax` integer DEFAULT 0 NOT NULL,
	`total` integer DEFAULT 0 NOT NULL,
	`note` text,
	`created_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	`updated_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	FOREIGN KEY (`customer_id`) REFERENCES `customers`(`id`) ON UPDATE no action ON DELETE no action,
	FOREIGN KEY (`vehicle_id`) REFERENCES `vehicles`(`id`) ON UPDATE no action ON DELETE no action
);
--> statement-breakpoint
CREATE UNIQUE INDEX `sales_documents_number_uq` ON `sales_documents` (`number`);--> statement-breakpoint
CREATE INDEX `sales_documents_customer_id_idx` ON `sales_documents` (`customer_id`);--> statement-breakpoint
CREATE INDEX `sales_documents_vehicle_id_idx` ON `sales_documents` (`vehicle_id`);--> statement-breakpoint
CREATE INDEX `sales_documents_status_idx` ON `sales_documents` (`status`);--> statement-breakpoint
CREATE TABLE `staff_profiles` (
	`uid` text PRIMARY KEY NOT NULL,
	`display_name` text NOT NULL,
	`email` text,
	`role` text DEFAULT 'staff' NOT NULL,
	`created_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	`updated_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL
);
--> statement-breakpoint
CREATE TABLE `vehicle_files` (
	`id` text PRIMARY KEY NOT NULL,
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
CREATE INDEX `vehicle_files_vehicle_id_idx` ON `vehicle_files` (`vehicle_id`);--> statement-breakpoint
CREATE TABLE `vehicles` (
	`id` text PRIMARY KEY NOT NULL,
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
CREATE INDEX `vehicles_customer_id_idx` ON `vehicles` (`customer_id`);--> statement-breakpoint
CREATE INDEX `vehicles_registration_number_idx` ON `vehicles` (`registration_number`);--> statement-breakpoint
CREATE INDEX `vehicles_inspection_date_idx` ON `vehicles` (`inspection_date`);