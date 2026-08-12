PRAGMA foreign_keys=OFF;
CREATE TABLE `__new_maintenance_documents` (
	`id` text PRIMARY KEY NOT NULL,
	`organization_id` text DEFAULT 'org-default' NOT NULL,
	`number` text NOT NULL,
	`type` text DEFAULT '整備請求書' NOT NULL,
	`category` text NOT NULL,
	`status` text DEFAULT '下書き' NOT NULL,
	`customer_id` text NOT NULL,
	`vehicle_id` text,
	`intake_date` text,
	`planned_release_date` text,
	`completion_date` text,
	`issued_at` text NOT NULL,
	`due_date` text,
	`tax_rate` integer DEFAULT 10 NOT NULL,
	`tax_rounding` text DEFAULT '切り捨て' NOT NULL,
	`subtotal` integer DEFAULT 0 NOT NULL,
	`tax` integer DEFAULT 0 NOT NULL,
	`total` integer DEFAULT 0 NOT NULL,
	`note` text,
	`details_json` text DEFAULT '{}' NOT NULL,
	`archived_at` text,
	`archived_previous_status` text,
	`archived_by` text,
	`purge_at` text,
	`keep_forever` integer DEFAULT false NOT NULL,
	`created_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	`updated_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	FOREIGN KEY (`customer_id`) REFERENCES `customers`(`id`) ON UPDATE no action ON DELETE no action,
	FOREIGN KEY (`vehicle_id`) REFERENCES `vehicles`(`id`) ON UPDATE no action ON DELETE no action
);
--> statement-breakpoint
INSERT INTO `__new_maintenance_documents`("id", "organization_id", "number", "type", "category", "status", "customer_id", "vehicle_id", "intake_date", "planned_release_date", "completion_date", "issued_at", "due_date", "tax_rate", "tax_rounding", "subtotal", "tax", "total", "note", "details_json", "archived_at", "archived_previous_status", "archived_by", "purge_at", "keep_forever", "created_at", "updated_at") SELECT "id", "organization_id", "number", "type", "category", "status", "customer_id", "vehicle_id", "intake_date", "planned_release_date", "completion_date", "issued_at", "due_date", "tax_rate", "tax_rounding", "subtotal", "tax", "total", "note", "details_json", "archived_at", "archived_previous_status", "archived_by", "purge_at", "keep_forever", "created_at", "updated_at" FROM `maintenance_documents`;
--> statement-breakpoint
DROP TABLE `maintenance_documents`;
--> statement-breakpoint
ALTER TABLE `__new_maintenance_documents` RENAME TO `maintenance_documents`;
--> statement-breakpoint
PRAGMA foreign_keys=ON;
--> statement-breakpoint
CREATE UNIQUE INDEX `maintenance_documents_organization_number_uq` ON `maintenance_documents` (`organization_id`,`number`);
--> statement-breakpoint
CREATE INDEX `maintenance_documents_organization_id_idx` ON `maintenance_documents` (`organization_id`);
--> statement-breakpoint
CREATE INDEX `maintenance_documents_customer_id_idx` ON `maintenance_documents` (`customer_id`);
--> statement-breakpoint
CREATE INDEX `maintenance_documents_vehicle_id_idx` ON `maintenance_documents` (`vehicle_id`);
--> statement-breakpoint
CREATE INDEX `maintenance_documents_status_idx` ON `maintenance_documents` (`status`);
