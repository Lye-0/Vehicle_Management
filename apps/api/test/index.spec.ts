import { env, SELF } from "cloudflare:test";
import { describe, it, expect } from "vitest";
import worker from "../src/index";

describe("Vehicle Management API", () => {
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
