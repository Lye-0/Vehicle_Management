import { env, SELF } from "cloudflare:test";
import { describe, expect, it, afterAll, beforeAll } from "vitest";

const TEST_ORG = "org-sales-sync-test";
const TEST_UID = "sms-test-user";

beforeAll(async () => {
  await env.DB.prepare("INSERT OR IGNORE INTO organizations (id, name) VALUES (?, ?)")
    .bind(TEST_ORG, "sales-sync-test").run();
  await env.DB.prepare("INSERT OR IGNORE INTO staff_profiles (uid, display_name, role) VALUES (?, ?, ?)")
    .bind(TEST_UID, "SMS Test User", "owner").run();
  await env.DB.prepare("INSERT OR IGNORE INTO organization_memberships (id, organization_id, uid, role, status) VALUES (?, ?, ?, ?, ?)")
    .bind(`mem-sms-${TEST_UID}`, TEST_ORG, TEST_UID, "owner", "active").run();
});

afterAll(async () => {
  await env.DB.prepare("DELETE FROM sales_document_items WHERE organization_id = ?").bind(TEST_ORG).run();
  await env.DB.prepare("DELETE FROM sales_documents WHERE organization_id = ?").bind(TEST_ORG).run();
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

async function getCustomerUpdatedAt(id: string): Promise<string> {
  const row = await env.DB.prepare("SELECT updated_at FROM customers WHERE id = ?").bind(id).first<{ updated_at: string }>();
  return row!.updated_at;
}

async function getVehicleUpdatedAt(id: string): Promise<string> {
  const row = await env.DB.prepare("SELECT updated_at FROM vehicles WHERE id = ?").bind(id).first<{ updated_at: string }>();
  return row!.updated_at;
}

describe("POST sales documents masterSync", () => {
  it("新規顧客＋新規車両＋販売書類を一体作成", async () => {
    const res = await SELF.fetch(postReq("https://example.com/api/sales-documents", {
      type: "見積書",
      newCustomer: { name: "新規売客A", phone: "090-1111-0001" },
      newVehicle: { maker: "トヨタ", name: "プリウス", bodyColor: "ブラック" },
      details: { customerOverride: null, vehicleOverride: null },
      items: [{ itemType: "車両本体価格", description: "車両本体", quantity: 1, unit: "式", unitPrice: 2000000 }],
    }));
    expect(res.status).toBe(201);
    const body = await res.json() as { document: { customerId: string; vehicleId: string | null } };
    expect(body.document.customerId).toBeTruthy();
    expect(body.document.vehicleId).toBeTruthy();
  });

  it("既存顧客＋既存車両＋販売書類を一体作成", async () => {
    const cid = "sms-cust-combo-001";
    const vid = "sms-veh-combo-001";
    await seedCustomer(cid, "既存組み合わせ顧客A");
    await seedVehicle(vid, cid, "トヨタ", "プリウス");

    const res = await SELF.fetch(postReq("https://example.com/api/sales-documents", {
      type: "見積書",
      customerId: cid,
      vehicleId: vid,
      details: { customerOverride: null, vehicleOverride: null },
      items: [],
    }));
    expect(res.status).toBe(201);
    const body = await res.json() as { document: { customerId: string; vehicleId: string } };
    expect(body.document.customerId).toBe(cid);
    expect(body.document.vehicleId).toBe(vid);
  });

  it("既存顧客＋新規車両＋販売書類を一体作成", async () => {
    const cid = "sms-cust-combo-002";
    await seedCustomer(cid, "既存組み合わせ顧客B");

    const res = await SELF.fetch(postReq("https://example.com/api/sales-documents", {
      type: "請求書",
      customerId: cid,
      newVehicle: { maker: "ホンダ", name: "N-BOX" },
      details: { customerOverride: null, vehicleOverride: null },
      items: [],
    }));
    expect(res.status).toBe(201);
    const body = await res.json() as { document: { customerId: string; vehicleId: string } };
    expect(body.document.customerId).toBe(cid);
    expect(body.document.vehicleId).toBeTruthy();
  });

  it("既存顧客＋車両なしを400拒否", async () => {
    const cid = "sms-cust-001";
    await seedCustomer(cid, "既存顧客A", "090-1111-2222");

    const res = await SELF.fetch(postReq("https://example.com/api/sales-documents", {
      type: "見積書",
      customerId: cid,
      vehicleId: null,
      details: { customerOverride: null, vehicleOverride: null },
      items: [],
    }));
    expect(res.status).toBe(400);
  });

  it("customerIdとnewCustomerの同時指定を400拒否", async () => {
    const cid = "sms-cust-002";
    await seedCustomer(cid, "既存顧客B");

    const res = await SELF.fetch(postReq("https://example.com/api/sales-documents", {
      type: "見積書",
      customerId: cid,
      newCustomer: { name: "新規売客C" },
      details: { customerOverride: null, vehicleOverride: null },
      items: [],
    }));
    expect(res.status).toBe(400);
  });

  it("vehicleIdとnewVehicleの同時指定を400拒否", async () => {
    const cid = "sms-cust-003";
    const vid = "sms-veh-003";
    await seedCustomer(cid, "既存顧客C");
    await seedVehicle(vid, cid, "トヨタ", "クラウン");

    const res = await SELF.fetch(postReq("https://example.com/api/sales-documents", {
      type: "見積書",
      customerId: cid,
      vehicleId: vid,
      newVehicle: { maker: "トヨタ", name: "カローラ" },
      details: { customerOverride: null, vehicleOverride: null },
      items: [],
    }));
    expect(res.status).toBe(400);
  });

  it("新規顧客にcustomerFieldsを指定して400", async () => {
    const res = await SELF.fetch(postReq("https://example.com/api/sales-documents", {
      type: "見積書",
      newCustomer: { name: "新規売客D", phone: "090-0000-0001" },
      newVehicle: { maker: "トヨタ", name: "アルファード" },
      details: { customerOverride: null, vehicleOverride: null },
      masterSync: { confirmed: true, customerFields: ["phone"] },
      items: [],
    }));
    expect(res.status).toBe(400);
  });

  it("新規車両にvehicleFieldsを指定して400", async () => {
    const res = await SELF.fetch(postReq("https://example.com/api/sales-documents", {
      type: "見積書",
      newCustomer: { name: "新規売客E" },
      newVehicle: { maker: "トヨタ", name: "ヴェルファイア" },
      details: { customerOverride: null, vehicleOverride: null },
      masterSync: { confirmed: true, vehicleFields: ["bodyColor"] },
      items: [],
    }));
    expect(res.status).toBe(400);
  });

  it("車台番号完全一致で新規車両作成を拒否", async () => {
    const cid = "sms-cust-010";
    const vid = "sms-veh-010";
    await seedCustomer(cid, "重複テスト顧客");
    await seedVehicle(vid, cid, "トヨタ", "プリウス", undefined, "ZVW5000001");

    const res = await SELF.fetch(postReq("https://example.com/api/sales-documents", {
      type: "見積書",
      newCustomer: { name: "新規売客F" },
      newVehicle: { maker: "トヨタ", name: "プリウス", chassisNumber: "ZVW50-00001" },
      duplicateConfirmation: { registrationNumberConfirmed: true, confirmedVehicleId: vid },
      details: { customerOverride: null, vehicleOverride: null },
      items: [],
    }));
    expect(res.status).toBe(409);
  });

  it("登録番号一致で確認なしを拒否", async () => {
    const cid = "sms-cust-011";
    const vid = "sms-veh-011";
    await seedCustomer(cid, "重複テスト顧客2");
    await seedVehicle(vid, cid, "ホンダ", "フィット", undefined, undefined, "横浜300い5678");

    const res = await SELF.fetch(postReq("https://example.com/api/sales-documents", {
      type: "見積書",
      newCustomer: { name: "新規売客G" },
      newVehicle: { maker: "ホンダ", name: "フィット", registrationNumber: "横浜 300 い 5678" },
      details: { customerOverride: null, vehicleOverride: null },
      items: [],
    }));
    expect(res.status).toBe(409);
  });

  it("登録番号一致で正しい候補確認ありなら許可", async () => {
    const cid = "sms-cust-012";
    const vid = "sms-veh-012";
    await seedCustomer(cid, "重複テスト顧客3");
    await seedVehicle(vid, cid, "ホンダ", "フィット", undefined, undefined, "品川500あ1234");

    const res = await SELF.fetch(postReq("https://example.com/api/sales-documents", {
      type: "見積書",
      newCustomer: { name: "新規売客H" },
      newVehicle: { maker: "ホンダ", name: "フィット", registrationNumber: "品川 500 あ 1234" },
      duplicateConfirmation: { registrationNumberConfirmed: true, confirmedVehicleId: vid },
      details: { customerOverride: null, vehicleOverride: null },
      items: [],
    }));
    expect(res.status).toBe(201);
  });

  it("登録番号一致候補が複数でも1件目を確認すれば許可", async () => {
    const cid = "sms-cust-013";
    const firstVid = "sms-veh-013-a";
    const secondVid = "sms-veh-013-b";
    await seedCustomer(cid, "重複候補複数顧客1");
    await seedVehicle(firstVid, cid, "トヨタ", "プリウス", undefined, undefined, "名古屋300あ1234");
    await seedVehicle(secondVid, cid, "ホンダ", "フィット", undefined, undefined, "名古屋 300 あ 1234");

    const res = await SELF.fetch(postReq("https://example.com/api/sales-documents", {
      type: "見積書",
      newCustomer: { name: "新規売客I" },
      newVehicle: { maker: "日産", name: "ノート", registrationNumber: "名古屋300あ1234" },
      duplicateConfirmation: { registrationNumberConfirmed: true, confirmedVehicleId: firstVid },
      details: { customerOverride: null, vehicleOverride: null },
      items: [],
    }));
    expect(res.status).toBe(201);
  });

  it("登録番号一致候補が複数でも2件目を確認すれば許可", async () => {
    const cid = "sms-cust-014";
    const firstVid = "sms-veh-014-a";
    const secondVid = "sms-veh-014-b";
    await seedCustomer(cid, "重複候補複数顧客2");
    await seedVehicle(firstVid, cid, "トヨタ", "プリウス", undefined, undefined, "京都500い5678");
    await seedVehicle(secondVid, cid, "ホンダ", "フィット", undefined, undefined, "京都 500 い 5678");

    const res = await SELF.fetch(postReq("https://example.com/api/sales-documents", {
      type: "見積書",
      newCustomer: { name: "新規売客J" },
      newVehicle: { maker: "日産", name: "ノート", registrationNumber: "京都500い5678" },
      duplicateConfirmation: { registrationNumberConfirmed: true, confirmedVehicleId: secondVid },
      details: { customerOverride: null, vehicleOverride: null },
      items: [],
    }));
    expect(res.status).toBe(201);
  });

  it("登録番号一致候補に存在しない車両IDは拒否", async () => {
    const cid = "sms-cust-015";
    const vid = "sms-veh-015";
    await seedCustomer(cid, "重複候補外ID顧客");
    await seedVehicle(vid, cid, "スバル", "インプレッサ", undefined, undefined, "神戸300う1111");

    const res = await SELF.fetch(postReq("https://example.com/api/sales-documents", {
      type: "見積書",
      newCustomer: { name: "新規売客K" },
      newVehicle: { maker: "スバル", name: "インプレッサ", registrationNumber: "神戸300う1111" },
      duplicateConfirmation: { registrationNumberConfirmed: true, confirmedVehicleId: "sms-veh-015-not-candidate" },
      details: { customerOverride: null, vehicleOverride: null },
      items: [],
    }));
    expect(res.status).toBe(400);
  });

  it("登録番号一致で確認がfalseなら拒否", async () => {
    const cid = "sms-cust-016";
    const vid = "sms-veh-016";
    await seedCustomer(cid, "重複確認false顧客");
    await seedVehicle(vid, cid, "マツダ", "デミオ", undefined, undefined, "福岡500え2222");

    const res = await SELF.fetch(postReq("https://example.com/api/sales-documents", {
      type: "見積書",
      newCustomer: { name: "新規売客L" },
      newVehicle: { maker: "マツダ", name: "デミオ", registrationNumber: "福岡500え2222" },
      duplicateConfirmation: { registrationNumberConfirmed: false, confirmedVehicleId: vid },
      details: { customerOverride: null, vehicleOverride: null },
      items: [],
    }));
    expect(res.status).toBe(409);
  });
});

describe("PATCH sales documents masterSync", () => {
  it("顧客の複数差分から1項目だけ更新", async () => {
    const cid = "sms-cust-020";
    const vid = "sms-veh-020";
    await seedCustomer(cid, "山田太郎", "090-1111-1111");
    await seedVehicle(vid, cid, "トヨタ", "プリウス");

    const postRes = await SELF.fetch(postReq("https://example.com/api/sales-documents", {
      type: "見積書",
      customerId: cid,
      vehicleId: vid,
      details: { customerOverride: null, vehicleOverride: null },
      items: [],
    }));
    const doc = (await postRes.json() as { document: { id: string } }).document;

    const patchRes = await SELF.fetch(patchReq(`https://example.com/api/sales-documents/${doc.id}`, {
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
    expect(patchRes.status).toBe(200);

    const phone = await env.DB.prepare("SELECT phone FROM customers WHERE id = ?").bind(cid).first<{ phone: string | null }>();
    expect(phone?.phone).toBe("080-2222-2222");
  });

  it("選択していない項目が変更されない", async () => {
    const cid = "sms-cust-021";
    const vid = "sms-veh-021";
    await seedCustomer(cid, "佐藤花子", "090-3333-3333", "sato@test.com", "東京都");
    await seedVehicle(vid, cid, "トヨタ", "プリウス");

    const postRes = await SELF.fetch(postReq("https://example.com/api/sales-documents", {
      type: "見積書",
      customerId: cid,
      vehicleId: vid,
      details: { customerOverride: null, vehicleOverride: null },
      items: [],
    }));
    const doc = (await postRes.json() as { document: { id: string } }).document;

    const patchRes = await SELF.fetch(patchReq(`https://example.com/api/sales-documents/${doc.id}`, {
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
    expect(patchRes.status).toBe(200);

    const cust = await env.DB.prepare("SELECT address FROM customers WHERE id = ?").bind(cid).first<{ address: string | null }>();
    expect(cust?.address).toBe("東京都");
  });

  it("空欄フィールド指定を400拒否", async () => {
    const cid = "sms-cust-022";
    const vid = "sms-veh-022";
    await seedCustomer(cid, "鈴木一郎", "090-5555-5555");
    await seedVehicle(vid, cid, "トヨタ", "プリウス");

    const postRes = await SELF.fetch(postReq("https://example.com/api/sales-documents", {
      type: "見積書",
      customerId: cid,
      vehicleId: vid,
      details: { customerOverride: null, vehicleOverride: null },
      items: [],
    }));
    const doc = (await postRes.json() as { document: { id: string } }).document;

    const patchRes = await SELF.fetch(patchReq(`https://example.com/api/sales-documents/${doc.id}`, {
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
    expect(patchRes.status).toBe(400);
  });

  it("allowlist外を400拒否", async () => {
    const cid = "sms-cust-023";
    const vid = "sms-veh-023";
    await seedCustomer(cid, "高橋二郎");
    await seedVehicle(vid, cid, "トヨタ", "プリウス");

    const postRes = await SELF.fetch(postReq("https://example.com/api/sales-documents", {
      type: "見積書",
      customerId: cid,
      vehicleId: vid,
      details: { customerOverride: null, vehicleOverride: null },
      items: [],
    }));
    const doc = (await postRes.json() as { document: { id: string } }).document;

    const patchRes = await SELF.fetch(patchReq(`https://example.com/api/sales-documents/${doc.id}`, {
      customerId: cid,
      vehicleId: vid,
      details: { customerOverride: null, vehicleOverride: null },
      masterSync: { confirmed: true, customerFields: ["email"] },
    }));
    expect(patchRes.status).toBe(400);
  });

  it("confirmedがtrueでない場合を400拒否", async () => {
    const cid = "sms-cust-024";
    const vid = "sms-veh-024";
    await seedCustomer(cid, "渡辺四郎");
    await seedVehicle(vid, cid, "トヨタ", "プリウス");

    const postRes = await SELF.fetch(postReq("https://example.com/api/sales-documents", {
      type: "見積書",
      customerId: cid,
      vehicleId: vid,
      details: { customerOverride: null, vehicleOverride: null },
      items: [],
    }));
    const doc = (await postRes.json() as { document: { id: string } }).document;

    const patchRes = await SELF.fetch(patchReq(`https://example.com/api/sales-documents/${doc.id}`, {
      customerId: cid,
      vehicleId: vid,
      details: { customerOverride: null, vehicleOverride: null },
      masterSync: { confirmed: false, customerFields: ["phone"] },
    }));
    expect(patchRes.status).toBe(400);
  });

  it("updatedAt不一致で409", async () => {
    const cid = "sms-cust-025";
    const vid = "sms-veh-025";
    await seedCustomer(cid, "加藤六郎", "090-9999-9999");
    await seedVehicle(vid, cid, "トヨタ", "プリウス");

    const postRes = await SELF.fetch(postReq("https://example.com/api/sales-documents", {
      type: "見積書",
      customerId: cid,
      vehicleId: vid,
      details: { customerOverride: null, vehicleOverride: null },
      items: [],
    }));
    const doc = (await postRes.json() as { document: { id: string } }).document;

    const patchRes = await SELF.fetch(patchReq(`https://example.com/api/sales-documents/${doc.id}`, {
      customerId: cid,
      vehicleId: vid,
      details: { customerOverride: { name: "加藤六郎", phone: "070-0000-0000" } },
      masterSync: { confirmed: true, customerFields: ["phone"], expectedCustomerUpdatedAt: "1970-01-01T00:00:00.000Z" },
    }));
    expect(patchRes.status).toBe(409);

    const phone = await env.DB.prepare("SELECT phone FROM customers WHERE id = ?").bind(cid).first<{ phone: string | null }>();
    expect(phone?.phone).toBe("090-9999-9999");
  });

  it("車両マスタを更新", async () => {
    const cid = "sms-cust-030";
    const vid = "sms-veh-030";
    await seedCustomer(cid, "車両更新顧客");
    await seedVehicle(vid, cid, "トヨタ", "プリウス", "ブラック");

    const postRes = await SELF.fetch(postReq("https://example.com/api/sales-documents", {
      type: "見積書",
      customerId: cid,
      vehicleId: vid,
      details: { customerOverride: null, vehicleOverride: null },
      items: [],
    }));
    const doc = (await postRes.json() as { document: { id: string } }).document;

    const patchRes = await SELF.fetch(patchReq(`https://example.com/api/sales-documents/${doc.id}`, {
      customerId: cid,
      vehicleId: vid,
      details: {
        customerOverride: null,
        vehicleOverride: { maker: "トヨタ", name: "プリウス", modelType: "", plate: "", vin: "", year: "", inspectionDate: "", mileage: "", color: "レッド", displacement: "", transmission: "" },
      },
      masterSync: {
        confirmed: true,
        vehicleFields: ["bodyColor"],
        expectedVehicleUpdatedAt: await getVehicleUpdatedAt(vid),
      },
    }));
    expect(patchRes.status).toBe(200);

    const color = await env.DB.prepare("SELECT body_color FROM vehicles WHERE id = ?").bind(vid).first<{ body_color: string | null }>();
    expect(color?.body_color).toBe("レッド");
  });
});

describe("販売書類の車両必須", () => {
  it("既存顧客＋車両なしを400拒否", async () => {
    const cid = "sms-cust-040";
    await seedCustomer(cid, "車両なし顧客");

    const res = await SELF.fetch(postReq("https://example.com/api/sales-documents", {
      type: "請求書",
      customerId: cid,
      vehicleId: null,
      details: { customerOverride: null, vehicleOverride: null },
      items: [],
    }));
    expect(res.status).toBe(400);
  });

  it("新規顧客＋車両なしを400拒否", async () => {
    const res = await SELF.fetch(postReq("https://example.com/api/sales-documents", {
      type: "見積書",
      newCustomer: { name: "車両なし新規" },
      details: { customerOverride: null, vehicleOverride: null },
      items: [],
    }));
    expect(res.status).toBe(400);
  });
});
