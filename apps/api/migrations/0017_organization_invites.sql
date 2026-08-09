CREATE TABLE `organization_invites` (
	`id` text PRIMARY KEY NOT NULL,
	`organization_id` text NOT NULL,
	`email` text NOT NULL,
	`token_hash` text NOT NULL,
	`role` text DEFAULT 'employee' NOT NULL,
	`status` text DEFAULT 'pending' NOT NULL,
	`expires_at` text NOT NULL,
	`created_by_uid` text NOT NULL,
	`accepted_uid` text,
	`created_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	`updated_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL
);
--> statement-breakpoint
CREATE UNIQUE INDEX `organization_invites_token_hash_uq` ON `organization_invites` (`token_hash`);
--> statement-breakpoint
CREATE INDEX `organization_invites_organization_email_idx` ON `organization_invites` (`organization_id`,`email`);
--> statement-breakpoint
CREATE INDEX `organization_invites_status_expires_idx` ON `organization_invites` (`status`,`expires_at`);
