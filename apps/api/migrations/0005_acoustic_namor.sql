ALTER TABLE `maintenance_documents` ADD `planned_release_date` text;--> statement-breakpoint
UPDATE `maintenance_documents` SET `planned_release_date` = `completion_date` WHERE `planned_release_date` IS NULL AND `completion_date` IS NOT NULL;--> statement-breakpoint
ALTER TABLE `maintenance_documents` ADD `archived_at` text;--> statement-breakpoint
ALTER TABLE `sales_document_items` ADD `item_type` text DEFAULT 'その他' NOT NULL;--> statement-breakpoint
ALTER TABLE `sales_documents` ADD `archived_at` text;
