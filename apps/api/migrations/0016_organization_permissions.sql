CREATE TABLE `organization_permissions` (
	`organization_id` text PRIMARY KEY NOT NULL,
	`employee_can_export_csv` integer DEFAULT true NOT NULL,
	`employee_can_edit_shop` integer DEFAULT true NOT NULL,
	`employee_can_edit_tax` integer DEFAULT true NOT NULL,
	`employee_can_create_restore_backup` integer DEFAULT true NOT NULL,
	`employee_can_manage_backup_retention` integer DEFAULT false NOT NULL,
	`created_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL,
	`updated_at` text DEFAULT CURRENT_TIMESTAMP NOT NULL
);
