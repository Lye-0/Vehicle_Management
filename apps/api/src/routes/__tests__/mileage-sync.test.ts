import { env } from "cloudflare:test";
import { describe, expect, it, afterAll } from "vitest";

describe("D1 batch rollback", () => {
	const testOrganizationId = "org-d1-batch-rollback-test";

	afterAll(async () => {
		await env.DB.prepare("DELETE FROM organizations WHERE id = ?").bind(testOrganizationId).run();
	});

	it("rolls back all statements when a later statement violates a unique constraint", async () => {
		// Setup: create a test organization with known name
		await env.DB.prepare("INSERT OR IGNORE INTO organizations (id, name) VALUES (?, ?)")
			.bind(testOrganizationId, "initial-name")
			.run();

		// Verify initial state
		const initialOrg = await env.DB.prepare("SELECT name FROM organizations WHERE id = ?")
			.bind(testOrganizationId)
			.first<{ name: string }>();
		expect(initialOrg?.name).toBe("initial-name");

		// Build batch: successful UPDATE followed by UNIQUE constraint violation
		const statements = [
			// 1. This UPDATE would succeed in isolation
			env.DB.prepare("UPDATE organizations SET name = ? WHERE id = ?")
				.bind("updated-name", testOrganizationId),
			// 2. This INSERT violates PRIMARY KEY (unique constraint) on organizations.id
			env.DB.prepare("INSERT INTO organizations (id, name) VALUES (?, ?)")
				.bind(testOrganizationId, "duplicate"),
		];

		// batch() should reject because of the constraint violation
		await expect(env.DB.batch(statements)).rejects.toThrow();

		// Verify the UPDATE was rolled back — name should still be the initial value
		const afterBatchOrg = await env.DB.prepare("SELECT name FROM organizations WHERE id = ?")
			.bind(testOrganizationId)
			.first<{ name: string }>();
		expect(afterBatchOrg?.name).toBe("initial-name");
	});

	it("rolls back all statements when a later statement violates a NOT NULL constraint", async () => {
		// Setup: create a fresh test organization
		const notNullOrgId = "org-d1-batch-notnull-test";
		await env.DB.prepare("INSERT OR IGNORE INTO organizations (id, name) VALUES (?, ?)")
			.bind(notNullOrgId, "before-notnull")
			.run();

		try {
			const statements = [
				env.DB.prepare("UPDATE organizations SET name = ? WHERE id = ?")
					.bind("updated-should-rollback", notNullOrgId),
				// This INSERT violates NOT NULL on organizations.name
				env.DB.prepare("INSERT INTO organizations (id, name) VALUES (?, NULL)")
					.bind("org-null-name-test"),
			];

			await expect(env.DB.batch(statements)).rejects.toThrow();

			const afterBatchOrg = await env.DB.prepare("SELECT name FROM organizations WHERE id = ?")
				.bind(notNullOrgId)
				.first<{ name: string }>();
			expect(afterBatchOrg?.name).toBe("before-notnull");
		} finally {
			await env.DB.prepare("DELETE FROM organizations WHERE id = ?").bind(notNullOrgId).run();
		}
	});
});
