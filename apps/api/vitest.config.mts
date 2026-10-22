import path from "node:path";
import { cloudflareTest, readD1Migrations } from "@cloudflare/vitest-plugin";
import { defineConfig } from "vitest/config";

const migrationsPath = path.join(process.cwd(), "migrations");

export default defineConfig(async () => ({
	plugins: [
		cloudflareTest({
			wrangler: { configPath: "./wrangler.jsonc", environment: "development" },
			miniflare: {
				bindings: {
					TEST_MIGRATIONS: await readD1Migrations(migrationsPath),
					FIREBASE_AUTH_EMULATOR_RESET_MODE: "skip",
					FIREBASE_PROJECT_ID: "demo-vehicle-management",
				},
			},
		}),
	],
	test: {
		setupFiles: ["./test/apply-migrations.ts"],
	},
}));
