ALTER TABLE `customers` ADD `deleted_at` text;--> statement-breakpoint
ALTER TABLE `customers` ADD `deleted_by` text;--> statement-breakpoint
ALTER TABLE `customers` ADD `deletion_batch_id` text;--> statement-breakpoint
CREATE INDEX `customers_organization_deleted_at_idx` ON `customers` (`organization_id`,`deleted_at`);--> statement-breakpoint
ALTER TABLE `inspection_schedules` ADD `deletion_batch_id` text;--> statement-breakpoint
ALTER TABLE `maintenance_documents` ADD `archive_reason` text;--> statement-breakpoint
ALTER TABLE `maintenance_documents` ADD `deletion_batch_id` text;--> statement-breakpoint
ALTER TABLE `sales_documents` ADD `archive_reason` text;--> statement-breakpoint
ALTER TABLE `sales_documents` ADD `deletion_batch_id` text;--> statement-breakpoint
ALTER TABLE `vehicles` ADD `deleted_at` text;--> statement-breakpoint
ALTER TABLE `vehicles` ADD `deleted_by` text;--> statement-breakpoint
ALTER TABLE `vehicles` ADD `deletion_batch_id` text;--> statement-breakpoint
CREATE INDEX `vehicles_organization_deleted_at_idx` ON `vehicles` (`organization_id`,`deleted_at`);