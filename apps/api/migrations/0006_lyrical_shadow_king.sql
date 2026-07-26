ALTER TABLE `sales_document_items` ADD `tax_category` text DEFAULT '課税' NOT NULL;--> statement-breakpoint
ALTER TABLE `sales_document_items` ADD `other_amount` integer DEFAULT 0 NOT NULL;--> statement-breakpoint
ALTER TABLE `sales_document_items` ADD `summary` text DEFAULT '' NOT NULL;--> statement-breakpoint
ALTER TABLE `sales_documents` ADD `details_json` text DEFAULT '{}' NOT NULL;