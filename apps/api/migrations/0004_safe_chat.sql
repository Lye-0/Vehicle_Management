CREATE TABLE `backup_records` (
	`id` text PRIMARY KEY NOT NULL,
	`organization_id` text NOT NULL,
	`manifest_key` text NOT NULL,
	`file_count` integer DEFAULT 0 NOT NULL,
	`row_count` integer DEFAULT 0 NOT NULL,
	`status` text DEFAULT 'completed' NOT NULL,
	`created_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	`updated_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL
);
--> statement-breakpoint
CREATE UNIQUE INDEX `backup_records_manifest_key_uq` ON `backup_records` (`manifest_key`);--> statement-breakpoint
CREATE INDEX `backup_records_organization_id_idx` ON `backup_records` (`organization_id`);