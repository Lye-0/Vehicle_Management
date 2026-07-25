import { env, SELF } from "cloudflare:test";
import { describe, it, expect } from "vitest";
import worker from "../src/index";
import { getEnvironmentIssues } from "../src/environment";

describe("Vehicle Management API", () => {
	it("accepts the local development environment", () => {
		expect(getEnvironmentIssues({
			APP_ENV: "development",
			DATA_ENV: "development",
			FIREBASE_AUTH_EMULATOR: "true",
		})).toEqual([]);
	});

	it("rejects production settings that point to development services", () => {
		const issues = getEnvironmentIssues({
			APP_ENV: "production",
			DATA_ENV: "production",
			FIREBASE_PROJECT_ID: "vehicle-management-64",
			FIREBASE_AUTH_EMULATOR: "true",
			CORS_ORIGIN: "http://localhost:5173",
			B2_BUCKET: "vehicle-management-64-dev",
		});
		expect(issues).toEqual(expect.arrayContaining([
			"本番環境ではFIREBASE_AUTH_EMULATOR=falseが必要です。",
			"本番環境では開発用とは別のFirebaseプロジェクトを指定してください。",
			"本番環境のINITIAL_SETUP_KEYが未設定です。",
			"本番環境では開発用とは別のB2バケットを指定してください。",
		]));
	});

	it("accepts a separately configured production environment", () => {
		expect(getEnvironmentIssues({
			APP_ENV: "production",
			DATA_ENV: "production",
			FIREBASE_PROJECT_ID: "vehicle-management-production",
			FIREBASE_AUTH_EMULATOR: "false",
			FIREBASE_WEB_API_KEY: "production-web-api-key",
			INITIAL_SETUP_KEY: "production-setup-key",
			CORS_ORIGIN: "https://app.example.com",
			B2_BUCKET: "vehicle-management-64-production",
		})).toEqual([]);
	});

	it("returns service status from the health endpoint", async () => {
		const response = await worker.fetch(new Request("http://example.com/health"), env, {} as ExecutionContext);
		const body = await response.json() as { status: string; services: Record<string, string> };
		expect(response.status).toBe(200);
		expect(body.status).toBe("ok");
		expect(body.services.database).toBe("configured");
	});

	it("returns 401 when the current user is not authenticated", async () => {
		const response = await SELF.fetch("https://example.com/api/auth/me");
		expect(response.status).toBe(401);
	});

	it("protects customer data routes with authentication", async () => {
		const response = await SELF.fetch("https://example.com/api/customers");
		expect(response.status).toBe(401);
	});

	it("protects settings routes with authentication", async () => {
		const response = await SELF.fetch("https://example.com/api/settings");
		expect(response.status).toBe(401);
	});

	it("protects dashboard routes with authentication", async () => {
		const response = await SELF.fetch("https://example.com/api/dashboard");
		expect(response.status).toBe(401);
	});

	it("returns 404 for unknown routes", async () => {
		const response = await SELF.fetch("https://example.com/unknown");
		expect(response.status).toBe(404);
	});
});
