CREATE TABLE `mileage_histories` (
	`id` text PRIMARY KEY NOT NULL,
	`organization_id` text NOT NULL,
	`vehicle_id` text NOT NULL,
	`maintenance_document_id` text NOT NULL,
	`mileage` integer NOT NULL,
	`created_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	`updated_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	FOREIGN KEY (`vehicle_id`) REFERENCES `vehicles`(`id`) ON UPDATE no action ON DELETE cascade,
	FOREIGN KEY (`maintenance_document_id`) REFERENCES `maintenance_documents`(`id`) ON UPDATE no action ON DELETE cascade
);
--> statement-breakpoint
CREATE INDEX `mileage_histories_organization_id_idx` ON `mileage_histories` (`organization_id`);--> statement-breakpoint
CREATE INDEX `mileage_histories_vehicle_id_idx` ON `mileage_histories` (`vehicle_id`);--> statement-breakpoint
CREATE UNIQUE INDEX `mileage_histories_organization_document_uq` ON `mileage_histories` (`organization_id`,`maintenance_document_id`);