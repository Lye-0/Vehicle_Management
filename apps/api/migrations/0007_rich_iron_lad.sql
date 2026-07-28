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
CREATE INDEX `document_number_sequences_organization_id_idx` ON `document_number_sequences` (`organization_id`);