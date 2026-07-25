DROP INDEX `payment_records_document_uq`;--> statement-breakpoint
CREATE UNIQUE INDEX `payment_records_organization_document_uq` ON `payment_records` (`organization_id`,`document_type`,`document_id`);