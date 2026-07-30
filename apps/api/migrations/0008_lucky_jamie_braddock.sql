ALTER TABLE `maintenance_documents` ADD `details_json` text DEFAULT '{}' NOT NULL;--> statement-breakpoint
ALTER TABLE `maintenance_items` ADD `technical_fee` integer DEFAULT 0 NOT NULL;--> statement-breakpoint
ALTER TABLE `maintenance_items` ADD `summary` text DEFAULT '' NOT NULL;