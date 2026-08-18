import path from "node:path";
import { defineWorkersConfig, readD1Migrations } from "@cloudflare/vitest-pool-workers/config";

const migrationsPath = path.join(process.cwd(), "migrations");

export default defineWorkersConfig(async () => ({
	test: {
		poolOptions: {
			workers: {
				wrangler: { configPath: "./wrangler.jsonc", environment: "development" },
				miniflare: {
					bindings: {
						TEST_MIGRATIONS: await readD1Migrations(migrationsPath),
						FIREBASE_AUTH_EMULATOR_RESET_MODE: "skip",
						FIREBASE_PROJECT_ID: "demo-vehicle-management",
					},
				},
			},
		},
		setupFiles: ["./test/apply-migrations.ts"],
	},
}));
