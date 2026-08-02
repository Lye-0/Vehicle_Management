ALTER TABLE `maintenance_documents` ADD `tax_rounding` text DEFAULT '切り捨て' NOT NULL;--> statement-breakpoint
ALTER TABLE `sales_documents` ADD `tax_rounding` text DEFAULT '切り捨て' NOT NULL;