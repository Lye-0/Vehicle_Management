CREATE TABLE `shared_schedules` (
	`id` text PRIMARY KEY NOT NULL,
	`organization_id` text DEFAULT 'org-default' NOT NULL,
	`title` text NOT NULL,
	`start_date` text NOT NULL,
	`end_date` text NOT NULL,
	`detail` text DEFAULT '' NOT NULL,
	`created_by_uid` text NOT NULL,
	`created_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	`updated_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL
);
--> statement-breakpoint
CREATE INDEX `shared_schedules_organization_date_idx` ON `shared_schedules` (`organization_id`,`start_date`,`end_date`);--> statement-breakpoint
CREATE INDEX `shared_schedules_created_by_uid_idx` ON `shared_schedules` (`created_by_uid`);