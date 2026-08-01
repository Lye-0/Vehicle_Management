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
INSERT OR IGNORE INTO `payment_entries` (`id`, `organization_id`, `document_type`, `document_id`, `amount`, `payment_date`, `method`, `note`, `created_at`, `updated_at`)
SELECT 'legacy-' || `id`, `organization_id`, `document_type`, `document_id`, `paid_amount`, `payment_date`, `method`, COALESCE(`note`, ''), `created_at`, `updated_at`
FROM `payment_records`
WHERE `paid_amount` > 0 OR `payment_date` IS NOT NULL OR `method` IS NOT NULL OR `note` IS NOT NULL;
