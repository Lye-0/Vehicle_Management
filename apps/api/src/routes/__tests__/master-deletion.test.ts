import { env, SELF } from "cloudflare:test";
import { afterAll, beforeAll, describe, expect, it } from "vitest";

const TEST_ORG = "org-master-deletion-test";
const TEST_UID = "master-deletion-test-user";

beforeAll(async () => {
  await env.DB.prepare("INSERT OR IGNORE INTO organizations (id, name) VALUES (?, ?)")
    .bind(TEST_ORG, "master-deletion-test").run();
  await env.DB.prepare("INSERT OR IGNORE INTO staff_profiles (uid, display_name, role) VALUES (?, ?, ?)")
    .bind(TEST_UID, "Master Deletion Test", "owner").run();
  await env.DB.prepare("INSERT OR IGNORE INTO organization_memberships (id, organization_id, uid, role, status) VALUES (?, ?, ?, ?, ?)")
    .bind(`mem-${TEST_UID}`, TEST_ORG, TEST_UID, "owner", "active").run();
});

afterAll(async () => {
  await env.DB.prepare("DELETE FROM mileage_histories WHERE organization_id = ?").bind(TEST_ORG).run();
  await env.DB.prepare("DELETE FROM inspection_schedules WHERE organization_id = ?").bind(TEST_ORG).run();
  await env.DB.prepare("DELETE FROM vehicle_files WHERE organization_id = ?").bind(TEST_ORG).run();
  await env.DB.prepare("DELETE FROM payment_entries WHERE organization_id = ?").bind(TEST_ORG).run();
  await env.DB.prepare("DELETE FROM payment_records WHERE organization_id = ?").bind(TEST_ORG).run();
  await env.DB.prepare("DELETE FROM maintenance_items WHERE organization_id = ?").bind(TEST_ORG).run();
  await env.DB.prepare("DELETE FROM sales_document_items WHERE organization_id = ?").bind(TEST_ORG).run();
  await env.DB.prepare("DELETE FROM maintenance_documents WHERE organization_id = ?").bind(TEST_ORG).run();
  await env.DB.prepare("DELETE FROM sales_documents WHERE organization_id = ?").bind(TEST_ORG).run();
  await env.DB.prepare("DELETE FROM vehicles WHERE organization_id = ?").bind(TEST_ORG).run();
  await env.DB.prepare("DELETE FROM customers WHERE organization_id = ?").bind(TEST_ORG).run();
  await env.DB.prepare("DELETE FROM organization_memberships WHERE organization_id = ?").bind(TEST_ORG).run();
  await env.DB.prepare("DELETE FROM staff_profiles WHERE uid = ?").bind(TEST_UID).run();
  await env.DB.prepare("DELETE FROM organizations WHERE id = ?").bind(TEST_ORG).run();
});

function emulatorToken(uid: string) {
  const projectId = env.FIREBASE_PROJECT_ID ?? "demo-vehicle-management";
  const payload = { sub: uid, aud: projectId, iss: `https://securetoken.google.com/${projectId}`, exp: Math.floor(Date.now() / 1000) + 3600, email: `${uid}@example.com`, email_verified: true, name: uid };
  return `${base64Url(JSON.stringify({ alg: "none", typ: "JWT" }))}.${base64Url(JSON.stringify(payload))}.deletion-test`;
}

function base64Url(data: string) { return Buffer.from(data).toString("base64url"); }

function request(url: string, method = "GET", body?: unknown) {
  return new Request(`https://example.com${url}`, {
    method,
    headers: new Headers({ "Content-Type": "application/json", "Authorization": `Bearer ${emulatorToken(TEST_UID)}`, "X-Organization-Id": TEST_ORG }),
    body: body === undefined ? undefined : JSON.stringify(body),
  });
}

async function seedCustomer(id: string, name: string) {
  await env.DB.prepare("INSERT INTO customers (id, organization_id, customer_number, name) VALUES (?, ?, ?, ?)")
    .bind(id, TEST_ORG, `C-${id}`, name).run();
}

async function seedVehicle(id: string, customerId: string, name: string) {
  await env.DB.prepare("INSERT INTO vehicles (id, organization_id, customer_id, maker, name, inspection_date, mileage) VALUES (?, ?, ?, ?, ?, ?, ?)")
    .bind(id, TEST_ORG, customerId, "テストメーカー", name, "2027-01-01", 12000).run();
}

async function seedSales(id: string, number: string, customerId: string, vehicleId: string) {
  await env.DB.prepare("INSERT INTO sales_documents (id, organization_id, number, type, status, customer_id, vehicle_id, issued_at, total) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)")
    .bind(id, TEST_ORG, number, "請求書", "完了", customerId, vehicleId, "2026-08-01", 10000).run();
}

async function seedMaintenance(id: string, number: string, customerId: string, vehicleId: string) {
  await env.DB.prepare("INSERT INTO maintenance_documents (id, organization_id, number, type, category, status, customer_id, vehicle_id, issued_at, total) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)")
    .bind(id, TEST_ORG, number, "整備請求書", "一般整備", "完了", customerId, vehicleId, "2026-08-02", 20000).run();
}

describe("customer and vehicle deletion", () => {
  it("顧客削除で書類を個別アーカイブし、1書類の復元だけで親と履歴を戻す", async () => {
    await seedCustomer("delete-customer-1", "削除顧客");
    await seedVehicle("delete-vehicle-1", "delete-customer-1", "削除車両");
    await seedSales("delete-sales-1", "S-DELETE-1", "delete-customer-1", "delete-vehicle-1");
    await seedMaintenance("delete-maintenance-1", "M-DELETE-1", "delete-customer-1", "delete-vehicle-1");
    await seedMaintenance("delete-maintenance-2", "M-DELETE-2", "delete-customer-1", "delete-vehicle-1");
    await env.DB.prepare("INSERT INTO inspection_schedules (id, organization_id, customer_id, vehicle_id, inspection_type, due_date) VALUES (?, ?, ?, ?, ?, ?)")
      .bind("delete-schedule-1", TEST_ORG, "delete-customer-1", "delete-vehicle-1", "車検", "2027-01-01").run();
    await env.DB.prepare("INSERT INTO mileage_histories (id, organization_id, vehicle_id, maintenance_document_id, mileage) VALUES (?, ?, ?, ?, ?)")
      .bind("delete-mileage-1", TEST_ORG, "delete-vehicle-1", "delete-maintenance-1", 12000).run();
    await env.DB.prepare("INSERT INTO mileage_histories (id, organization_id, vehicle_id, maintenance_document_id, mileage) VALUES (?, ?, ?, ?, ?)")
      .bind("delete-mileage-2", TEST_ORG, "delete-vehicle-1", "delete-maintenance-2", 13000).run();

    const current = await env.DB.prepare("SELECT updated_at FROM customers WHERE id = ?").bind("delete-customer-1").first<{ updated_at: string }>();
    const impact = await SELF.fetch(request("/api/customers/delete-customer-1/deletion-impact"));
    expect(impact.status).toBe(200);
    expect((await impact.json()).impact).toEqual(expect.objectContaining({ vehicleCount: 1, documentCount: 3, inspectionCount: 1, attachmentCount: 0 }));

    const deleted = await SELF.fetch(request("/api/customers/delete-customer-1", "DELETE", { confirmation: true, expectedUpdatedAt: current?.updated_at }));
    expect(deleted.status).toBe(200);
    expect((await env.DB.prepare("SELECT deleted_at FROM customers WHERE id = ?").bind("delete-customer-1").first<{ deleted_at: string | null }>())?.deleted_at).not.toBeNull();
    expect((await env.DB.prepare("SELECT deleted_at FROM vehicles WHERE id = ?").bind("delete-vehicle-1").first<{ deleted_at: string | null }>())?.deleted_at).not.toBeNull();
    expect((await env.DB.prepare("SELECT status, deletion_batch_id FROM maintenance_documents WHERE id = ?").bind("delete-maintenance-1").first<{ status: string; deletion_batch_id: string | null }>())).toEqual(expect.objectContaining({ status: "アーカイブ済み", deletion_batch_id: expect.any(String) }));

    const restored = await SELF.fetch(request("/api/archives/maintenance/delete-maintenance-1/restore", "POST", {}));
    expect(restored.status).toBe(200);
    expect((await env.DB.prepare("SELECT archived_at FROM maintenance_documents WHERE id = ?").bind("delete-maintenance-1").first<{ archived_at: string | null }>())?.archived_at).toBeNull();
    expect((await env.DB.prepare("SELECT archived_at FROM sales_documents WHERE id = ?").bind("delete-sales-1").first<{ archived_at: string | null }>())?.archived_at).not.toBeNull();
    expect((await env.DB.prepare("SELECT archived_at FROM maintenance_documents WHERE id = ?").bind("delete-maintenance-2").first<{ archived_at: string | null }>())?.archived_at).not.toBeNull();
    expect((await env.DB.prepare("SELECT deleted_at FROM customers WHERE id = ?").bind("delete-customer-1").first<{ deleted_at: string | null }>())?.deleted_at).toBeNull();
    expect((await env.DB.prepare("SELECT deleted_at FROM vehicles WHERE id = ?").bind("delete-vehicle-1").first<{ deleted_at: string | null }>())?.deleted_at).toBeNull();

    const history = await SELF.fetch(request("/api/vehicles/delete-vehicle-1/history"));
    expect(history.status).toBe(200);
    const historyBody = await history.json() as { sales: Array<{ id: string }>; maintenance: Array<{ id: string }>; inspections: Array<{ id: string }> };
    expect(historyBody.sales).toHaveLength(0);
    expect(historyBody.maintenance.map((row) => row.id)).toEqual(["delete-maintenance-1"]);
    expect(historyBody.inspections).toHaveLength(0);
  });

  it("車両削除では顧客を残し、復元時に対象車両だけを戻す", async () => {
    await seedCustomer("delete-customer-2", "車両削除顧客");
    await seedVehicle("delete-vehicle-2", "delete-customer-2", "車両削除対象");
    await seedSales("delete-sales-2", "S-DELETE-2", "delete-customer-2", "delete-vehicle-2");

    const current = await env.DB.prepare("SELECT updated_at FROM vehicles WHERE id = ?").bind("delete-vehicle-2").first<{ updated_at: string }>();
    const deleted = await SELF.fetch(request("/api/vehicles/delete-vehicle-2", "DELETE", { confirmation: true, expectedUpdatedAt: current?.updated_at }));
    expect(deleted.status).toBe(200);
    expect((await env.DB.prepare("SELECT deleted_at FROM customers WHERE id = ?").bind("delete-customer-2").first<{ deleted_at: string | null }>())?.deleted_at).toBeNull();
    expect((await env.DB.prepare("SELECT deleted_at FROM vehicles WHERE id = ?").bind("delete-vehicle-2").first<{ deleted_at: string | null }>())?.deleted_at).not.toBeNull();

    const restored = await SELF.fetch(request("/api/archives/sales/delete-sales-2/restore", "POST", {}));
    expect(restored.status).toBe(200);
    expect((await env.DB.prepare("SELECT deleted_at FROM vehicles WHERE id = ?").bind("delete-vehicle-2").first<{ deleted_at: string | null }>())?.deleted_at).toBeNull();
  });

  it("削除済み車両の添付ファイルは取得・削除できない", async () => {
    await seedCustomer("delete-customer-file", "添付削除顧客");
    await seedVehicle("delete-vehicle-file", "delete-customer-file", "添付削除車両");
    await env.DB.prepare("INSERT INTO vehicle_files (id, organization_id, vehicle_id, object_key, file_name, content_type, size_bytes, file_kind) VALUES (?, ?, ?, ?, ?, ?, ?, ?)")
      .bind("delete-file-1", TEST_ORG, "delete-vehicle-file", "vehicles/delete-vehicle-file/delete-file-1.pdf", "添付.pdf", "application/pdf", 4, "pdf").run();

    const current = await env.DB.prepare("SELECT updated_at FROM vehicles WHERE id = ?").bind("delete-vehicle-file").first<{ updated_at: string }>();
    const deleted = await SELF.fetch(request("/api/vehicles/delete-vehicle-file", "DELETE", { confirmation: true, expectedUpdatedAt: current?.updated_at }));
    expect(deleted.status).toBe(200);

    const downloaded = await SELF.fetch(request("/api/vehicles/delete-vehicle-file/files/delete-file-1"));
    expect(downloaded.status).toBe(404);
    const removed = await SELF.fetch(request("/api/vehicles/delete-vehicle-file/files/delete-file-1", "DELETE"));
    expect(removed.status).toBe(404);
    expect(await env.DB.prepare("SELECT id FROM vehicle_files WHERE id = ?").bind("delete-file-1").first()).not.toBeNull();
  });

  it("削除済み点検予定は更新・物理削除できない", async () => {
    await seedCustomer("delete-customer-schedule", "予定削除顧客");
    await seedVehicle("delete-vehicle-schedule", "delete-customer-schedule", "予定削除車両");
    await env.DB.prepare("INSERT INTO inspection_schedules (id, organization_id, customer_id, vehicle_id, inspection_type, due_date, note) VALUES (?, ?, ?, ?, ?, ?, ?)")
      .bind("delete-schedule-active", TEST_ORG, "delete-customer-schedule", "delete-vehicle-schedule", "車検", "2027-03-01", "更新前").run();

    const updated = await SELF.fetch(request("/api/inspection-schedules/delete-schedule-active", "PATCH", { note: "更新後" }));
    expect(updated.status).toBe(200);
    expect((await env.DB.prepare("SELECT note FROM inspection_schedules WHERE id = ?").bind("delete-schedule-active").first<{ note: string }>())?.note).toBe("更新後");

    await env.DB.prepare("INSERT INTO inspection_schedules (id, organization_id, customer_id, vehicle_id, inspection_type, due_date, note) VALUES (?, ?, ?, ?, ?, ?, ?)")
      .bind("delete-schedule-archived", TEST_ORG, "delete-customer-schedule", "delete-vehicle-schedule", "12か月点検", "2027-04-01", "保持").run();
    const current = await env.DB.prepare("SELECT updated_at FROM vehicles WHERE id = ?").bind("delete-vehicle-schedule").first<{ updated_at: string }>();
    const deleted = await SELF.fetch(request("/api/vehicles/delete-vehicle-schedule", "DELETE", { confirmation: true, expectedUpdatedAt: current?.updated_at }));
    expect(deleted.status).toBe(200);

    const archivedPatch = await SELF.fetch(request("/api/inspection-schedules/delete-schedule-archived", "PATCH", { note: "変更不可" }));
    expect(archivedPatch.status).toBe(404);
    const archivedDelete = await SELF.fetch(request("/api/inspection-schedules/delete-schedule-archived", "DELETE"));
    expect(archivedDelete.status).toBe(404);
    expect(await env.DB.prepare("SELECT deletion_batch_id, note FROM inspection_schedules WHERE id = ?").bind("delete-schedule-archived").first<{ deletion_batch_id: string | null; note: string }>()).toEqual(expect.objectContaining({ deletion_batch_id: expect.any(String), note: "保持" }));
  });
});
