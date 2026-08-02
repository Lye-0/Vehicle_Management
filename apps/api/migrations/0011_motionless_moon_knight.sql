ALTER TABLE `backup_records` ADD `trigger` text DEFAULT 'manual' NOT NULL;--> statement-breakpoint
ALTER TABLE `backup_records` ADD `protected_until` text;--> statement-breakpoint
ALTER TABLE `backup_records` ADD `keep_forever` integer DEFAULT false NOT NULL;--> statement-breakpoint
ALTER TABLE `maintenance_documents` ADD `archived_previous_status` text;--> statement-breakpoint
ALTER TABLE `maintenance_documents` ADD `archived_by` text;--> statement-breakpoint
ALTER TABLE `maintenance_documents` ADD `purge_at` text;--> statement-breakpoint
ALTER TABLE `maintenance_documents` ADD `keep_forever` integer DEFAULT false NOT NULL;--> statement-breakpoint
ALTER TABLE `sales_documents` ADD `archived_previous_status` text;--> statement-breakpoint
ALTER TABLE `sales_documents` ADD `archived_by` text;--> statement-breakpoint
ALTER TABLE `sales_documents` ADD `purge_at` text;--> statement-breakpoint
ALTER TABLE `sales_documents` ADD `keep_forever` integer DEFAULT false NOT NULL;