import { env, SELF } from "cloudflare:test";
import { describe, expect, it, afterAll, beforeAll } from "vitest";

const TEST_ORG = "org-master-sync-test";
const TEST_UID = "ms-test-user";

beforeAll(async () => {
  await env.DB.prepare("INSERT OR IGNORE INTO organizations (id, name) VALUES (?, ?)")
    .bind(TEST_ORG, "master-sync-test").run();
  await env.DB.prepare("INSERT OR IGNORE INTO staff_profiles (uid, display_name, role) VALUES (?, ?, ?)")
    .bind(TEST_UID, "MS Test User", "owner").run();
  await env.DB.prepare("INSERT OR IGNORE INTO organization_memberships (id, organization_id, uid, role, status) VALUES (?, ?, ?, ?, ?)")
    .bind(`mem-ms-${TEST_UID}`, TEST_ORG, TEST_UID, "owner", "active").run();
});

afterAll(async () => {
  await env.DB.prepare("DELETE FROM mileage_histories WHERE organization_id = ?").bind(TEST_ORG).run();
  await env.DB.prepare("DELETE FROM maintenance_items WHERE organization_id = ?").bind(TEST_ORG).run();
  await env.DB.prepare("DELETE FROM maintenance_documents WHERE organization_id = ?").bind(TEST_ORG).run();
  await env.DB.prepare("DELETE FROM vehicles WHERE organization_id = ?").bind(TEST_ORG).run();
  await env.DB.prepare("DELETE FROM customers WHERE organization_id = ?").bind(TEST_ORG).run();
  await env.DB.prepare("DELETE FROM organization_memberships WHERE organization_id = ?").bind(TEST_ORG).run();
  await env.DB.prepare("DELETE FROM staff_profiles WHERE uid = ?").bind(TEST_UID).run();
  await env.DB.prepare("DELETE FROM organizations WHERE id = ?").bind(TEST_ORG).run();
});

function emulatorToken(uid: string) {
  const projectId = env.FIREBASE_PROJECT_ID ?? "vehicle-management-64";
  const payload = {
    sub: uid,
    aud: projectId,
    iss: `https://securetoken.google.com/${projectId}`,
    exp: Math.floor(Date.now() / 1000) + 3600,
    email: `${uid}@example.com`,
    email_verified: true,
    name: uid,
  };
  return `${base64Url(JSON.stringify({ alg: "none", typ: "JWT" }))}.${base64Url(JSON.stringify(payload))}.cli-test`;
}

function base64Url(data: string) {
  return Buffer.from(data).toString("base64url");
}

function authHeaders() {
  return new Headers({
    "Content-Type": "application/json",
    "Authorization": `Bearer ${emulatorToken(TEST_UID)}`,
    "X-Organization-Id": TEST_ORG,
  });
}

function postReq(url: string, body: unknown) {
  return new Request(url, { method: "POST", headers: authHeaders(), body: JSON.stringify(body) });
}

function patchReq(url: string, body: unknown) {
  return new Request(url, { method: "PATCH", headers: authHeaders(), body: JSON.stringify(body) });
}

async function seedCustomer(id: string, name: string, phone?: string, email?: string, address?: string) {
  await env.DB.prepare(
    "INSERT INTO customers (id, organization_id, customer_number, name, phone, email, address) VALUES (?, ?, ?, ?, ?, ?, ?)"
  ).bind(id, TEST_ORG, `C-${id.replace(/[^a-zA-Z0-9]/g, '').toUpperCase()}`, name, phone ?? null, email ?? null, address ?? null).run();
}

async function seedVehicle(id: string, customerId: string, maker: string, name: string, bodyColor?: string, chassisNumber?: string, registrationNumber?: string) {
  await env.DB.prepare(
    "INSERT INTO vehicles (id, organization_id, customer_id, maker, name, body_color, chassis_number, registration_number) VALUES (?, ?, ?, ?, ?, ?, ?, ?)"
  ).bind(id, TEST_ORG, customerId, maker, name, bodyColor ?? null, chassisNumber ?? null, registrationNumber ?? null).run();
}

async function seedDoc(id: string, number: string, customerId: string, vehicleId: string, issuedAt: string) {
  await env.DB.prepare(
    "INSERT INTO maintenance_documents (id, organization_id, number, type, category, status, customer_id, vehicle_id, issued_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)"
  ).bind(id, TEST_ORG, number, "整備見積書", "一般整備", "下書き", customerId, vehicleId, issuedAt).run();
}

async function getCustomerUpdatedAt(id: string): Promise<string> {
  const row = await env.DB.prepare("SELECT updated_at FROM customers WHERE id = ?").bind(id).first<{ updated_at: string }>();
  return row!.updated_at;
}

describe("PATCH masterSync", () => {
  it("顧客の複数差分から1項目だけ更新", async () => {
    const cid = "ms-cust-001";
    const vid = "ms-veh-001";
    const did = "ms-doc-001";
    await seedCustomer(cid, "山田太郎", "090-1111-1111");
    await seedVehicle(vid, cid, "トヨタ", "プリウス", "ブラック");
    await seedDoc(did, "M-MS-001", cid, vid, "2026-08-01");

    const res = await SELF.fetch(patchReq(`https://example.com/api/maintenance-documents/${did}`, {
      customerId: cid,
      vehicleId: vid,
      details: {
        customerOverride: { name: "山田太郎", kana: "", phone: "080-2222-2222", postalCode: "", address: "" },
        vehicleOverride: null,
      },
      masterSync: {
        confirmed: true,
        customerFields: ["phone"],
        expectedCustomerUpdatedAt: await getCustomerUpdatedAt(cid),
      },
    }));
    expect(res.status).toBe(200);

    const phone = await env.DB.prepare("SELECT phone FROM customers WHERE id = ?").bind(cid).first<{ phone: string | null }>();
    expect(phone?.phone).toBe("080-2222-2222");
  });

  it("選択していない項目が変更されない", async () => {
    const cid = "ms-cust-002";
    const vid = "ms-veh-002";
    const did = "ms-doc-002";
    await seedCustomer(cid, "佐藤花子", "090-3333-3333", "sato@test.com", "東京都");
    await seedVehicle(vid, cid, "ホンダ", "フィット", "ホワイト");
    await seedDoc(did, "M-MS-002", cid, vid, "2026-08-01");

    const res = await SELF.fetch(patchReq(`https://example.com/api/maintenance-documents/${did}`, {
      customerId: cid,
      vehicleId: vid,
      details: {
        customerOverride: { name: "佐藤花子", kana: "", phone: "080-4444-4444", postalCode: "", address: "神奈川県" },
        vehicleOverride: null,
      },
      masterSync: {
        confirmed: true,
        customerFields: ["phone"],
        expectedCustomerUpdatedAt: await getCustomerUpdatedAt(cid),
      },
    }));
    expect(res.status).toBe(200);

    const cust = await env.DB.prepare("SELECT address FROM customers WHERE id = ?").bind(cid).first<{ address: string | null }>();
    expect(cust?.address).toBe("東京都");
  });

  it("空欄フィールド指定を400拒否", async () => {
    const cid = "ms-cust-003";
    const vid = "ms-veh-003";
    const did = "ms-doc-003";
    await seedCustomer(cid, "鈴木一郎", "090-5555-5555");
    await seedVehicle(vid, cid, "日産", "ノート");
    await seedDoc(did, "M-MS-003", cid, vid, "2026-08-01");

    const res = await SELF.fetch(patchReq(`https://example.com/api/maintenance-documents/${did}`, {
      customerId: cid,
      vehicleId: vid,
      details: {
        customerOverride: { name: "鈴木一郎", kana: "", phone: "", postalCode: "", address: "" },
        vehicleOverride: null,
      },
      masterSync: {
        confirmed: true,
        customerFields: ["phone"],
        expectedCustomerUpdatedAt: await getCustomerUpdatedAt(cid),
      },
    }));
    expect(res.status).toBe(400);
  });

  it("allowlist外を400拒否", async () => {
    const cid = "ms-cust-004";
    const vid = "ms-veh-004";
    const did = "ms-doc-004";
    await seedCustomer(cid, "高橋二郎");
    await seedVehicle(vid, cid, "マツダ", "CX-5");
    await seedDoc(did, "M-MS-004", cid, vid, "2026-08-01");

    const res = await SELF.fetch(patchReq(`https://example.com/api/maintenance-documents/${did}`, {
      customerId: cid,
      vehicleId: vid,
      details: { customerOverride: null, vehicleOverride: null },
      masterSync: { confirmed: true, customerFields: ["email"] },
    }));
    expect(res.status).toBe(400);
  });

  it("mileageをvehicleFieldsに指定して400拒否", async () => {
    const cid = "ms-cust-005";
    const vid = "ms-veh-005";
    const did = "ms-doc-005";
    await seedCustomer(cid, "伊藤三郎");
    await seedVehicle(vid, cid, "スバル", "インプレッサ");
    await seedDoc(did, "M-MS-005", cid, vid, "2026-08-01");

    const res = await SELF.fetch(patchReq(`https://example.com/api/maintenance-documents/${did}`, {
      customerId: cid,
      vehicleId: vid,
      details: { customerOverride: null, vehicleOverride: { color: "レッド" } },
      masterSync: { confirmed: true, vehicleFields: ["mileage"] },
    }));
    expect(res.status).toBe(400);
  });

  it("confirmedがtrueでない場合を400拒否", async () => {
    const cid = "ms-cust-006";
    const vid = "ms-veh-006";
    const did = "ms-doc-006";
    await seedCustomer(cid, "渡辺四郎");
    await seedVehicle(vid, cid, "トヨタ", "カムリ");
    await seedDoc(did, "M-MS-006", cid, vid, "2026-08-01");

    const res = await SELF.fetch(patchReq(`https://example.com/api/maintenance-documents/${did}`, {
      customerId: cid,
      vehicleId: vid,
      details: { customerOverride: null, vehicleOverride: null },
      masterSync: { confirmed: false, customerFields: ["phone"] },
    }));
    expect(res.status).toBe(400);
  });

  it("customerFields指定時にexpectedCustomerUpdatedAtがなく400", async () => {
    const cid = "ms-cust-007";
    const vid = "ms-veh-007";
    const did = "ms-doc-007";
    await seedCustomer(cid, "小林五郎", "090-7777-7777");
    await seedVehicle(vid, cid, "トヨタ", "RAV4");
    await seedDoc(did, "M-MS-007", cid, vid, "2026-08-01");

    const res = await SELF.fetch(patchReq(`https://example.com/api/maintenance-documents/${did}`, {
      customerId: cid,
      vehicleId: vid,
      details: { customerOverride: { name: "小林五郎", phone: "080-8888-8888" } },
      masterSync: { confirmed: true, customerFields: ["phone"] },
    }));
    expect(res.status).toBe(400);
  });

  it("updatedAt不一致で409", async () => {
    const cid = "ms-cust-008";
    const vid = "ms-veh-008";
    const did = "ms-doc-008";
    await seedCustomer(cid, "加藤六郎", "090-9999-9999");
    await seedVehicle(vid, cid, "トヨタ", "ヤリス");
    await seedDoc(did, "M-MS-008", cid, vid, "2026-08-01");

    const res = await SELF.fetch(patchReq(`https://example.com/api/maintenance-documents/${did}`, {
      customerId: cid,
      vehicleId: vid,
      details: { customerOverride: { name: "加藤六郎", phone: "070-0000-0000" } },
      masterSync: { confirmed: true, customerFields: ["phone"], expectedCustomerUpdatedAt: "1970-01-01T00:00:00.000Z" },
    }));
    expect(res.status).toBe(409);

    const phone = await env.DB.prepare("SELECT phone FROM customers WHERE id = ?").bind(cid).first<{ phone: string | null }>();
    expect(phone?.phone).toBe("090-9999-9999");
  });
});

describe("POST masterSync", () => {
  it("新規顧客＋新規車両＋整備書類を一体作成", async () => {
    const res = await SELF.fetch(postReq("https://example.com/api/maintenance-documents", {
      type: "整備見積書",
      category: "一般整備",
      newCustomer: { name: "新規顧客A", phone: "090-1111-0001" },
      newVehicle: { maker: "トヨタ", name: "プリウス", bodyColor: "ブラック" },
      details: { customerOverride: null, vehicleOverride: null },
      items: [{ kind: "作業", description: "オイル交換", quantity: 1, unit: "式", unitPrice: 5000, technicalFee: 0, summary: "" }],
    }));
    expect(res.status).toBe(201);
    const body = await res.json() as { document: { customerId: string; vehicleId: string } };
    expect(body.document.customerId).toBeTruthy();
    expect(body.document.vehicleId).toBeTruthy();
  });

  it("新規顧客＋既存車両を400拒否", async () => {
    const cid = "ms-cust-010";
    const vid = "ms-veh-010";
    await seedCustomer(cid, "既存顧客");
    await seedVehicle(vid, cid, "トヨタ", "クラウン");

    const res = await SELF.fetch(postReq("https://example.com/api/maintenance-documents", {
      type: "整備見積書",
      category: "一般整備",
      newCustomer: { name: "新規顧客B" },
      vehicleId: vid,
      details: { customerOverride: null, vehicleOverride: null },
      items: [],
    }));
    expect(res.status).toBe(400);
  });

  it("customerIdとnewCustomerの同時指定を400拒否", async () => {
    const cid = "ms-cust-011";
    const vid = "ms-veh-011";
    await seedCustomer(cid, "既存顧客2");
    await seedVehicle(vid, cid, "ホンダ", "シビック");

    const res = await SELF.fetch(postReq("https://example.com/api/maintenance-documents", {
      type: "整備見積書",
      category: "一般整備",
      customerId: cid,
      newCustomer: { name: "新規顧客C" },
      vehicleId: vid,
      details: { customerOverride: null, vehicleOverride: null },
      items: [],
    }));
    expect(res.status).toBe(400);
  });

  it("vehicleIdとnewVehicleの同時指定を400拒否", async () => {
    const cid = "ms-cust-012";
    const vid = "ms-veh-012";
    await seedCustomer(cid, "既存顧客3");
    await seedVehicle(vid, cid, "日産", "スカイライン");

    const res = await SELF.fetch(postReq("https://example.com/api/maintenance-documents", {
      type: "整備見積書",
      category: "一般整備",
      customerId: cid,
      vehicleId: vid,
      newVehicle: { maker: "トヨタ", name: "カローラ" },
      details: { customerOverride: null, vehicleOverride: null },
      items: [],
    }));
    expect(res.status).toBe(400);
  });

  it("新規顧客にcustomerFieldsを指定して400", async () => {
    const res = await SELF.fetch(postReq("https://example.com/api/maintenance-documents", {
      type: "整備見積書",
      category: "一般整備",
      newCustomer: { name: "新規顧客D", phone: "090-0000-0001" },
      newVehicle: { maker: "トヨタ", name: "アルファード" },
      details: { customerOverride: null, vehicleOverride: null },
      masterSync: { confirmed: true, customerFields: ["phone"] },
      items: [],
    }));
    expect(res.status).toBe(400);
  });

  it("新規車両にvehicleFieldsを指定して400", async () => {
    const res = await SELF.fetch(postReq("https://example.com/api/maintenance-documents", {
      type: "整備見積書",
      category: "一般整備",
      newCustomer: { name: "新規顧客E" },
      newVehicle: { maker: "トヨタ", name: "ヴェルファイア" },
      details: { customerOverride: null, vehicleOverride: null },
      masterSync: { confirmed: true, vehicleFields: ["bodyColor"] },
      items: [],
    }));
    expect(res.status).toBe(400);
  });

  it("既存車両が別顧客に所属する場合を拒否", async () => {
    const cid1 = "ms-cust-013a";
    const cid2 = "ms-cust-013b";
    const vid = "ms-veh-013";
    await seedCustomer(cid1, "顧客A");
    await seedCustomer(cid2, "顧客B");
    await seedVehicle(vid, cid1, "トヨタ", "ランドクルーザー");

    const res = await SELF.fetch(postReq("https://example.com/api/maintenance-documents", {
      type: "整備見積書",
      category: "一般整備",
      customerId: cid2,
      vehicleId: vid,
      details: { customerOverride: null, vehicleOverride: null },
      items: [],
    }));
    expect(res.status).toBe(400);
  });
});

describe("重複検出", () => {
  it("車台番号完全一致で新規車両作成を拒否", async () => {
    const cid = "ms-cust-020";
    const vid = "ms-veh-020";
    await seedCustomer(cid, "重複テスト顧客");
    await seedVehicle(vid, cid, "トヨタ", "プリウス", undefined, "ZVW5000001");

    const res = await SELF.fetch(postReq("https://example.com/api/maintenance-documents", {
      type: "整備見積書",
      category: "一般整備",
      newCustomer: { name: "新規顧客F" },
      newVehicle: { maker: "トヨタ", name: "プリウス", chassisNumber: "ZVW50-00001" },
      details: { customerOverride: null, vehicleOverride: null },
      items: [],
    }));
    expect(res.status).toBe(409);
  });

  it("登録番号一致で確認なしを拒否", async () => {
    const cid = "ms-cust-021";
    const vid = "ms-veh-021";
    await seedCustomer(cid, "重複テスト顧客2");
    await seedVehicle(vid, cid, "ホンダ", "フィット", undefined, undefined, "横浜300い5678");

    const res = await SELF.fetch(postReq("https://example.com/api/maintenance-documents", {
      type: "整備見積書",
      category: "一般整備",
      newCustomer: { name: "新規顧客G" },
      newVehicle: { maker: "ホンダ", name: "フィット", registrationNumber: "横浜 300 い 5678" },
      details: { customerOverride: null, vehicleOverride: null },
      items: [],
    }));
    expect(res.status).toBe(409);
  });

  it("登録番号一致で正しい候補確認ありなら許可", async () => {
    const cid = "ms-cust-022";
    const vid = "ms-veh-022";
    await seedCustomer(cid, "重複テスト顧客3");
    await seedVehicle(vid, cid, "ホンダ", "フィット", undefined, undefined, "品川500あ1234");

    const res = await SELF.fetch(postReq("https://example.com/api/maintenance-documents", {
      type: "整備見積書",
      category: "一般整備",
      newCustomer: { name: "新規顧客H" },
      newVehicle: { maker: "ホンダ", name: "フィット", registrationNumber: "品川 500 あ 1234" },
      duplicateConfirmation: { registrationNumberConfirmed: true, confirmedVehicleId: vid },
      details: { customerOverride: null, vehicleOverride: null },
      items: [],
    }));
    expect(res.status).toBe(201);
  });

  it("無関係な候補IDを確認値として送った場合を拒否", async () => {
    const cid = "ms-cust-023";
    const vid = "ms-veh-023";
    await seedCustomer(cid, "重複テスト顧客4");
    await seedVehicle(vid, cid, "トヨタ", "カムリ", undefined, undefined, "名古屋200さ5678");

    const res = await SELF.fetch(postReq("https://example.com/api/maintenance-documents", {
      type: "整備見積書",
      category: "一般整備",
      newCustomer: { name: "新規顧客I" },
      newVehicle: { maker: "トヨタ", name: "カムリ", registrationNumber: "名古屋200さ5678" },
      duplicateConfirmation: { registrationNumberConfirmed: true, confirmedVehicleId: "wrong-id" },
      details: { customerOverride: null, vehicleOverride: null },
      items: [],
    }));
    expect(res.status).toBe(400);
  });

  it("電話番号一致でも新規顧客作成は可能", async () => {
    const cid = "ms-cust-024";
    await seedCustomer(cid, "既存電話番号顧客", "090-1234-5678");

    const res = await SELF.fetch(postReq("https://example.com/api/maintenance-documents", {
      type: "整備見積書",
      category: "一般整備",
      newCustomer: { name: "新規顧客J", phone: "09012345678" },
      newVehicle: { maker: "トヨタ", name: "カローラ" },
      details: { customerOverride: null, vehicleOverride: null },
      items: [],
    }));
    expect(res.status).toBe(201);
  });
});

describe("新規車両の走行距離", () => {
  it("走行距離未入力で車両作成、mileageはnull、履歴なし", async () => {
    const res = await SELF.fetch(postReq("https://example.com/api/maintenance-documents", {
      type: "整備見積書",
      category: "一般整備",
      newCustomer: { name: "走行距離テスト顧客A" },
      newVehicle: { maker: "トヨタ", name: "プレミオ" },
      details: { customerOverride: null, vehicleOverride: null },
      items: [],
    }));
    expect(res.status).toBe(201);
    const body = await res.json() as { document: { vehicleId: string } };
    const vehicle = await env.DB.prepare("SELECT mileage FROM vehicles WHERE id = ?").bind(body.document.vehicleId).first<{ mileage: number | null }>();
    expect(vehicle?.mileage).toBeNull();
    const historyCount = await env.DB.prepare("SELECT COUNT(*) as cnt FROM mileage_histories WHERE vehicle_id = ?").bind(body.document.vehicleId).first<{ cnt: number }>();
    expect(historyCount?.cnt).toBe(0);
  });

  it("走行距離0で車両作成、mileageは0、履歴なし", async () => {
    const res = await SELF.fetch(postReq("https://example.com/api/maintenance-documents", {
      type: "整備見積書",
      category: "一般整備",
      newCustomer: { name: "走行距離テスト顧客B" },
      newVehicle: { maker: "ホンダ", name: "フィット", mileage: 0 },
      details: { customerOverride: null, vehicleOverride: null },
      items: [],
    }));
    expect(res.status).toBe(201);
    const body = await res.json() as { document: { vehicleId: string } };
    const vehicle = await env.DB.prepare("SELECT mileage FROM vehicles WHERE id = ?").bind(body.document.vehicleId).first<{ mileage: number | null }>();
    // 0は有効な走行距離値として保存されるべき
    expect(vehicle?.mileage).toBe(0);
    const historyCount = await env.DB.prepare("SELECT COUNT(*) as cnt FROM mileage_histories WHERE vehicle_id = ?").bind(body.document.vehicleId).first<{ cnt: number }>();
    expect(historyCount?.cnt).toBe(0);
  });

  it("走行距離15000で車両作成、mileageが一致、履歴なし", async () => {
    const res = await SELF.fetch(postReq("https://example.com/api/maintenance-documents", {
      type: "整備見積書",
      category: "一般整備",
      newCustomer: { name: "走行距離テスト顧客C" },
      newVehicle: { maker: "日産", name: "ノート", mileage: 15000 },
      details: { customerOverride: null, vehicleOverride: null },
      items: [],
    }));
    expect(res.status).toBe(201);
    const body = await res.json() as { document: { vehicleId: string } };
    const vehicle = await env.DB.prepare("SELECT mileage FROM vehicles WHERE id = ?").bind(body.document.vehicleId).first<{ mileage: number | null }>();
    expect(vehicle?.mileage).toBe(15000);
    const historyCount = await env.DB.prepare("SELECT COUNT(*) as cnt FROM mileage_histories WHERE vehicle_id = ?").bind(body.document.vehicleId).first<{ cnt: number }>();
    expect(historyCount?.cnt).toBe(0);
  });
});

describe("D1 batchロールバック", () => {
  it("updatedAt不一致でbatchが実行されずマスタが変更されない", async () => {
    const cid = "ms-cust-030";
    const vid = "ms-veh-030";
    const did = "ms-doc-030";
    await seedCustomer(cid, "ロールバック顧客", "090-3030-3030");
    await seedVehicle(vid, cid, "トヨタ", "ハイエース");
    await seedDoc(did, "M-MS-030", cid, vid, "2026-08-01");

    const originalPhone = (await env.DB.prepare("SELECT phone FROM customers WHERE id = ?").bind(cid).first<{ phone: string | null }>()).phone;

    const res = await SELF.fetch(patchReq(`https://example.com/api/maintenance-documents/${did}`, {
      customerId: cid,
      vehicleId: vid,
      details: { customerOverride: { name: "ロールバック顧客", phone: "070-9999-9999" } },
      masterSync: { confirmed: true, customerFields: ["phone"], expectedCustomerUpdatedAt: "1970-01-01T00:00:00.000Z" },
    }));
    expect(res.status).toBe(409);

    const afterPhone = (await env.DB.prepare("SELECT phone FROM customers WHERE id = ?").bind(cid).first<{ phone: string | null }>()).phone;
    expect(afterPhone).toBe(originalPhone);
  });
});
