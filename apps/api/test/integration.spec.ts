import { env, SELF } from "cloudflare:test";
import { describe, expect, it } from "vitest";

const marker = `CLI-${crypto.randomUUID().slice(0, 8)}`;
const organizationId = `org-cli-${marker.toLowerCase()}`;
const otherOrganizationId = `org-cli-other-${marker.toLowerCase()}`;
const ownerUid = `cli-owner-${marker.toLowerCase()}`;
const employeeUid = `cli-employee-${marker.toLowerCase()}`;
const importedCustomerId = `customer-import-${marker.toLowerCase()}`;

type JsonObject = Record<string, unknown>;
type TestEnv = Env & {
	B2_ENDPOINT?: string;
	B2_REGION?: string;
	B2_BUCKET?: string;
	B2_KEY_ID?: string;
	B2_APPLICATION_KEY?: string;
};

describe("CLI authenticated workflow", () => {
	it("rejects missing and malformed authentication before loading organization data", async () => {
		const missingToken = await requestJsonWithoutAuth<JsonObject>("/api/customers");
		expect(missingToken.response.status).toBe(401);

		const malformedToken = await requestJsonWithToken<JsonObject>("/api/customers", "not-a-jwt");
		expect(malformedToken.response.status).toBe(401);
	});

	it("completes the core organization workflow without browser interaction", async () => {
		let customerId: string | undefined;
		let vehicleId: string | undefined;
		let salesDocumentId: string | undefined;
		let maintenanceDocumentId: string | undefined;
		let inspectionScheduleId: string | undefined;
		let attachmentId: string | undefined;
		let backupId: string | undefined;

		await prepareTestOrganizations();

		try {
			const session = await requestJson<JsonObject>("/api/auth/me");
			expect(session.response.status).toBe(200);
			expect(session.body.profile).toEqual(expect.objectContaining({ role: "owner" }));
			expect(session.body.organizations).toEqual(expect.arrayContaining([
				expect.objectContaining({ organizationId, role: "owner", status: "active" }),
		]));

			const updatedProfile = await requestJson<JsonObject>("/api/auth/profile", "PATCH", {
				displayName: `${marker} 表示名変更`,
				email: `${marker.toLowerCase()}-profile@example.com`,
			});
			expect(updatedProfile.response.status).toBe(200);
			expect(updatedProfile.body.profile).toEqual({
				displayName: `${marker} 表示名変更`,
				email: `${marker.toLowerCase()}-profile@example.com`,
			});

			const refreshedSession = await requestJson<JsonObject>("/api/auth/me");
			expect(refreshedSession.body.profile).toEqual(expect.objectContaining({
				displayName: `${marker} 表示名変更`,
				email: `${marker.toLowerCase()}-profile@example.com`,
			}));

			const members = await requestJson<JsonObject>("/api/organization/members");
			expect(members.response.status).toBe(200);
			expect(members.body.currentRole).toBe("owner");
			expect(members.body.members).toEqual(expect.arrayContaining([
				expect.objectContaining({ uid: employeeUid, role: "employee", status: "active" }),
				expect.objectContaining({ uid: ownerUid, displayName: `${marker} 表示名変更`, email: `${marker.toLowerCase()}-profile@example.com` }),
		]));

			const createdCustomer = await requestJson<JsonObject>("/api/customers", "POST", {
				name: `${marker} 顧客`,
				nameKana: "CLIテストコキャク",
				phone: "090-0000-0000",
				email: "cli-test@example.com",
				postalCode: "100-0001",
				address: "東京都千代田区",
				memo: marker,
			});
			expect(createdCustomer.response.status).toBe(201);
			customerId = stringValue(objectValue(createdCustomer.body.customer).id);
			expect(customerId).toBeTruthy();

			const updatedCustomer = await requestJson<JsonObject>(`/api/customers/${customerId}`, "PATCH", {
				name: `${marker} 顧客 更新`,
				nameKana: "CLIテストコキャクコウシン",
				phone: "090-1111-1111",
				email: "cli-test-updated@example.com",
				postalCode: "100-0002",
				address: "東京都港区",
				memo: `${marker} updated`,
			});
			expect(updatedCustomer.response.status).toBe(200);
			expect(objectValue(updatedCustomer.body.customer).name).toBe(`${marker} 顧客 更新`);

			const createdVehicle = await requestJson<JsonObject>(`/api/customers/${customerId}/vehicles`, "POST", {
				maker: "トヨタ",
				model: `${marker} プリウス`,
				modelType: "6AA-ZVW60",
				registrationNumber: `品川 500 あ ${marker.slice(-4)}`,
				chassisNumber: `CLI-CHASSIS-${marker}`,
				modelYear: 2024,
				inspectionDate: "2027-07-26",
				mileage: 12345,
				bodyColor: "パールホワイト",
				displacement: 1800,
				transmission: "CVT",
				memo: `${marker} 車両メモ`,
				freeItem1: "4WD",
				freeItem2: "禁煙",
				freeItem3: "ワンオーナー",
			});
			expect(createdVehicle.response.status).toBe(201);
			vehicleId = stringValue(createdVehicle.body.vehicleId);
			expect(vehicleId).toBeTruthy();

			const updatedVehicle = await requestJson<JsonObject>(`/api/vehicles/${vehicleId}`, "PATCH", {
				maker: "トヨタ",
				model: `${marker} プリウス 更新`,
				registrationNumber: `品川 500 あ ${marker.slice(-4)}`,
				chassisNumber: `CLI-CHASSIS-${marker}`,
				modelYear: 2024,
				inspectionDate: "2027-08-01",
				mileage: 13000,
				bodyColor: "ブラック",
				modelType: "6AA-ZVW60-UPDATED",
				displacement: 1800,
				transmission: "CVT",
				memo: `${marker} 車両メモ 更新`,
				freeItem1: "2WD",
				freeItem2: "禁煙",
				freeItem3: "整備履歴あり",
			});
			expect(updatedVehicle.response.status).toBe(200);

			const customerSearch = await requestJson<JsonObject>(`/api/customers?q=${encodeURIComponent(marker)}`);
			expect(customerSearch.response.status).toBe(200);
			const searchedCustomer = arrayValue(customerSearch.body.customers).find((customer) => objectValue(customer).id === customerId);
			expect(searchedCustomer).toEqual(expect.objectContaining({ name: `${marker} 顧客 更新` }));
			expect(arrayValue(objectValue(searchedCustomer).vehicles)).toEqual(expect.arrayContaining([
				expect.objectContaining({ id: vehicleId, mileage: 13000, bodyColor: "ブラック", modelType: "6AA-ZVW60-UPDATED", displacement: 1800, transmission: "CVT", freeItem1: "2WD" }),
			]));

			const createdSchedule = await requestJson<JsonObject>('/api/inspection-schedules', 'POST', {
				customerId,
				vehicleId,
				inspectionType: '12か月点検',
				dueDate: '2026-08-20',
				note: `${marker} 点検予定`,
			});
			expect(createdSchedule.response.status).toBe(201);
			inspectionScheduleId = stringValue(objectValue(createdSchedule.body.schedule).id);
			expect(objectValue(createdSchedule.body.schedule)).toEqual(expect.objectContaining({ inspectionType: '12か月点検', status: '予定', vehicleId }));

			const listedSchedules = await requestJson<JsonObject>(`/api/inspection-schedules?vehicleId=${vehicleId}`);
			expect(listedSchedules.response.status).toBe(200);
			expect(arrayValue(listedSchedules.body.schedules)).toEqual(expect.arrayContaining([expect.objectContaining({ id: inspectionScheduleId, dueDate: '2026-08-20' })]));

			const updatedSchedule = await requestJson<JsonObject>(`/api/inspection-schedules/${inspectionScheduleId}`, 'PATCH', { status: '完了', note: `${marker} 点検完了` });
			expect(updatedSchedule.response.status).toBe(200);
			expect(objectValue(updatedSchedule.body.schedule)).toEqual(expect.objectContaining({ status: '完了', note: `${marker} 点検完了` }));
			const tamperedSchedule = await requestJson<JsonObject>(`/api/inspection-schedules/${inspectionScheduleId}`, 'PATCH', { status: '不正状態' });
			expect(tamperedSchedule.response.status).toBe(400);

			const sales = await requestJson<JsonObject>("/api/sales-documents", "POST", {
				type: "請求書",
				status: "下書き",
				number: `${marker}-SALE-001`,
				customerId,
				vehicleId,
				issuedAt: "2026-07-26",
				dueDate: "2026-08-09",
				taxRate: 10,
				rounding: "切り捨て",
				note: marker,
				items: [{ itemType: "車両本体価格", description: "車両本体価格", quantity: 1, unit: "式", unitPrice: 100001 }],
			});
			expect(sales.response.status).toBe(201);
			const salesDocument = objectValue(sales.body.document);
			salesDocumentId = stringValue(salesDocument.id);
			const salesNumber = stringValue(salesDocument.number);
			expect(salesNumber).toMatch(/^S-\d{4}-\d{5}$/);
			expect(salesDocument).toEqual(expect.objectContaining({ subtotal: 100001, tax: 10000, total: 110001, status: "下書き" }));

			const duplicateSales = await requestJson<JsonObject>("/api/sales-documents", "POST", {
				type: "請求書",
				status: "下書き",
				number: `${marker}-SALE-001`,
				customerId,
				vehicleId,
				issuedAt: "2026-07-26",
				items: [{ itemType: "その他", description: "重複確認", quantity: 1, unit: "式", unitPrice: 1 }],
			});
			expect(duplicateSales.response.status).toBe(201);
			expect(stringValue(objectValue(duplicateSales.body.document).number)).toMatch(/^S-\d{4}-\d{5}$/);
			expect(stringValue(objectValue(duplicateSales.body.document).number)).not.toBe(salesNumber);

			const updatedSales = await requestJson<JsonObject>(`/api/sales-documents/${salesDocumentId}`, "PATCH", {
				status: "入金待ち",
				number: `${marker}-SALE-EDITED`,
				note: `${marker} 販売ヘッダー更新`,
				details: {
					downPayment: 12345,
					customerOverride: { name: `${marker} プレビュー顧客` },
				},
				items: [
					{ itemType: "車両本体価格", description: "車両本体価格", quantity: 1, unit: "式", unitPrice: 100001 },
					{ itemType: "登録費用", description: "登録代行費用", quantity: 1, unit: "式", unitPrice: 999 },
				],
			});
			expect(updatedSales.response.status).toBe(200);
			const updatedSalesDocument = objectValue(updatedSales.body.document);
			expect(updatedSalesDocument).toEqual(expect.objectContaining({ number: salesNumber, status: "入金待ち", note: `${marker} 販売ヘッダー更新`, subtotal: 101000, tax: 10100, total: 111100 }));
			expect(objectValue(updatedSalesDocument.details).downPayment).toBe(12345);
			expect(objectValue(objectValue(updatedSalesDocument.details).customerOverride).name).toBe(`${marker} プレビュー顧客`);
			const tamperedSalesStatus = await requestJson<JsonObject>(`/api/sales-documents/${salesDocumentId}`, "PATCH", { status: "不正状態" });
			expect(tamperedSalesStatus.response.status).toBe(400);

			const maintenance = await requestJson<JsonObject>("/api/maintenance-documents", "POST", {
				type: "整備請求書",
				status: "入金待ち",
				number: `${marker}-MAINT-001`,
				category: "板金",
				customerId,
				vehicleId,
				intakeDate: "2026-07-26",
				plannedReleaseDate: "2026-07-27",
				issuedAt: "2026-07-26",
				dueDate: "2026-08-10",
				taxRate: 10,
				rounding: "切り捨て",
				fees: { 自賠責: 10000, 重量税: 0, 印紙代: 1000, リサイクル料金: 500 },
				adjustment: -100,
				note: marker,
				details: { bankName: `${marker}銀行`, bankAccount: "7654321" },
				items: [{ kind: "作業", description: "エンジンオイル交換", quantity: 2, unit: "式", unitPrice: 1500 }],
			});
			expect(maintenance.response.status).toBe(201);
			const maintenanceDocument = objectValue(maintenance.body.document);
			maintenanceDocumentId = stringValue(maintenanceDocument.id);
			const maintenanceNumber = stringValue(maintenanceDocument.number);
			expect(maintenanceNumber).toMatch(/^M-\d{4}-\d{5}$/);
			expect(maintenanceDocument).toEqual(expect.objectContaining({ subtotal: 3000, tax: 300, total: 14700, status: "入金待ち" }));
			expect(objectValue(maintenanceDocument.details).customerOverride).toBeNull();
			expect(objectValue(maintenanceDocument.details).vehicleOverride).toBeNull();
			expect(objectValue(maintenanceDocument.details)).toEqual(expect.objectContaining({ bankName: `${marker}銀行`, bankAccount: "7654321" }));

			const duplicateMaintenance = await requestJson<JsonObject>("/api/maintenance-documents", "POST", {
				type: "整備請求書",
				status: "下書き",
				number: `${marker}-MAINT-001`,
				category: "一般整備",
				customerId,
				vehicleId,
				issuedAt: "2026-07-26",
				items: [{ kind: "作業", description: "重複確認", quantity: 1, unit: "式", unitPrice: 1 }],
			});
			expect(duplicateMaintenance.response.status).toBe(201);
			expect(stringValue(objectValue(duplicateMaintenance.body.document).number)).toMatch(/^M-\d{4}-\d{5}$/);
			expect(stringValue(objectValue(duplicateMaintenance.body.document).number)).not.toBe(maintenanceNumber);

			const updatedMaintenance = await requestJson<JsonObject>(`/api/maintenance-documents/${maintenanceDocumentId}`, "PATCH", {
				status: "完了",
				completionDate: "2026-07-27",
			});
			expect(updatedMaintenance.response.status).toBe(200);
			expect(objectValue(updatedMaintenance.body.document)).toEqual(expect.objectContaining({ status: "完了", plannedReleaseDate: "2026-07-27", completionDate: "2026-07-27" }));
			const tamperedMaintenanceStatus = await requestJson<JsonObject>(`/api/maintenance-documents/${maintenanceDocumentId}`, "PATCH", { status: "不正状態" });
			expect(tamperedMaintenanceStatus.response.status).toBe(400);
			const removedMaintenanceIntakeStatus = await requestJson<JsonObject>(`/api/maintenance-documents/${maintenanceDocumentId}`, "PATCH", { status: "受付中" });
			expect(removedMaintenanceIntakeStatus.response.status).toBe(400);
			const removedMaintenanceWorkStatus = await requestJson<JsonObject>(`/api/maintenance-documents/${maintenanceDocumentId}`, "PATCH", { status: "作業中" });
			expect(removedMaintenanceWorkStatus.response.status).toBe(400);

			const payment = await requestJson<JsonObject>(`/api/payments/${encodeURIComponent("販売請求書")}/${salesDocumentId}`, "PATCH", {
				paidAmount: 50000,
				paymentDate: "2026-07-26",
				method: "銀行振込",
				note: marker,
			});
			expect(payment.response.status).toBe(200);
			expect(objectValue(payment.body.record)).toEqual(expect.objectContaining({ paidAmount: 50000, method: "銀行振込" }));

			const payments = await requestJson<JsonObject>("/api/payments");
			expect(payments.response.status).toBe(200);
			expect(arrayValue(payments.body.records)).toEqual(expect.arrayContaining([
				expect.objectContaining({ documentId: salesDocumentId, paidAmount: 50000 }),
			]));
			const manySalesItems = Array.from({ length: 20 }, (_, index) => ({ itemType: "その他", description: `${marker} 明細${index + 1}`, quantity: 1, unit: "式", unitPrice: index + 1 }));
			const manyItemsSales = await requestJson<JsonObject>(`/api/sales-documents/${salesDocumentId}`, "PATCH", { items: manySalesItems });
			expect(manyItemsSales.response.status).toBe(200);
			expect(arrayValue(objectValue(manyItemsSales.body.document).items)).toHaveLength(20);

			const archivedSales = await requestJson<JsonObject>(`/api/sales-documents/${salesDocumentId}`, "DELETE");
			expect(archivedSales.response.status).toBe(200);
			const hiddenArchivedSales = await requestJson<JsonObject>("/api/sales-documents");
			expect(arrayValue(hiddenArchivedSales.body.documents).some((document) => objectValue(document).id === salesDocumentId)).toBe(false);
			const visibleArchivedSales = await requestJson<JsonObject>("/api/sales-documents?includeArchived=true");
			expect(arrayValue(visibleArchivedSales.body.documents)).toEqual(expect.arrayContaining([expect.objectContaining({ id: salesDocumentId, status: "アーカイブ済み" })]));
			const restoredSales = await requestJson<JsonObject>(`/api/sales-documents/${salesDocumentId}/restore`, "POST");
			expect(restoredSales.response.status).toBe(200);

			const archivedMaintenance = await requestJson<JsonObject>(`/api/maintenance-documents/${maintenanceDocumentId}`, "DELETE");
			expect(archivedMaintenance.response.status).toBe(200);
			const restoredMaintenance = await requestJson<JsonObject>(`/api/maintenance-documents/${maintenanceDocumentId}/restore`, "POST");
			expect(restoredMaintenance.response.status).toBe(200);

			const vehicleHistory = await requestJson<JsonObject>(`/api/vehicles/${vehicleId}/history`);
			expect(vehicleHistory.response.status).toBe(200);
			expect(arrayValue(vehicleHistory.body.sales)).toEqual(expect.arrayContaining([expect.objectContaining({ id: salesDocumentId, number: expect.any(String) })]));
			expect(arrayValue(vehicleHistory.body.maintenance)).toEqual(expect.arrayContaining([expect.objectContaining({ id: maintenanceDocumentId, category: '板金' })]));
			expect(arrayValue(vehicleHistory.body.inspections)).toEqual(expect.arrayContaining([expect.objectContaining({ id: inspectionScheduleId, status: '完了' })]));
			expect(arrayValue(vehicleHistory.body.payments)).toEqual(expect.arrayContaining([expect.objectContaining({ documentId: salesDocumentId, paidAmount: 50000 })]));

			const settings = await requestJson<JsonObject>("/api/settings", "PATCH", {
				settings: {
					shop: { name: `${marker} 店舗`, logoDataUrl: "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=" },
					document: { defaultDueDays: 30 },
					tax: { consumptionTaxRate: 10, display: "税込", rounding: "四捨五入" },
					salesItemPresetGroups: {
						vehiclePrice: ["車両本体価格", `${marker}-vehicle`],
						fees: ["登録代行費用", `${marker}-fees`],
						accessories: ["フロアマット", `${marker}-accessory`],
					},
					maintenanceItemPresets: ["点検", marker],
				},
			});
			expect(settings.response.status).toBe(200);
			const savedSettings = objectValue(settings.body.settings);
			expect(objectValue(savedSettings.shop).name).toBe(`${marker} 店舗`);
			expect(objectValue(savedSettings.shop).logoDataUrl).toMatch(/^data:image\/png;base64,/);
			expect(objectValue(savedSettings.document).defaultDueDays).toBe(30);
			const salesPresetGroups = objectValue(savedSettings.salesItemPresetGroups);
			expect(stringArrayValue(salesPresetGroups.vehiclePrice)).toContain(`${marker}-vehicle`);
			expect(stringArrayValue(salesPresetGroups.fees)).toContain(`${marker}-fees`);
			expect(stringArrayValue(salesPresetGroups.accessories)).toContain(`${marker}-accessory`);
			expect(stringArrayValue(savedSettings.salesItemPresets)).toEqual(expect.arrayContaining([
				`${marker}-vehicle`,
				`${marker}-fees`,
				`${marker}-accessory`,
			]));

			const dashboard = await requestJson<JsonObject>("/api/dashboard");
			expect(dashboard.response.status).toBe(200);
			const dashboardSummary = objectValue(objectValue(dashboard.body.dashboard).summary);
			const dashboardVehicleCount = Number(dashboardSummary.registeredVehicles);
			if (dashboardVehicleCount !== 1) throw new Error(`ダッシュボードの車両件数が不正です: ${dashboardVehicleCount}`);
			const calendarEvents = arrayValue(objectValue(dashboard.body.dashboard).calendarEvents);
			expect(calendarEvents).toEqual(expect.arrayContaining([
				expect.objectContaining({ date: "2026-07-26", category: "payment" }),
				expect.objectContaining({ date: "2027-08-01", category: "vehicle-inspection" }),
				expect.objectContaining({ date: "2026-08-09", category: "payment-due" }),
				expect.objectContaining({ date: "2026-08-20", category: "inspection" }),
			]));

			const invalidAttachment = new FormData();
			invalidAttachment.append("file", new File(["not an allowed file"], "invalid.txt", { type: "text/plain" }));
			const rejectedUpload = await requestForm<JsonObject>(`/api/vehicles/${vehicleId}/files`, invalidAttachment);
			if (Number(rejectedUpload.response.status) !== 415) throw new Error(`不正添付の応答が不正です: ${rejectedUpload.response.status}`);

			const emptyCsv = new FormData();
			emptyCsv.append("file", new File([""], "empty.csv", { type: "text/csv" }));
			const emptyCsvResponse = await requestForm<JsonObject>("/api/import/customers/preview", emptyCsv);
			expect(emptyCsvResponse.response.status).toBe(400);

			const malformedCsv = new FormData();
			malformedCsv.append("file", new File(["顧客ID,顧客名\r\n\"閉じていない"], "malformed.csv", { type: "text/csv" }));
			const malformedCsvResponse = await requestForm<JsonObject>("/api/import/customers/preview", malformedCsv);
			expect(malformedCsvResponse.response.status).toBe(400);

			const b2Configured = isB2Configured(env as TestEnv);
			if (b2Configured) {
				const attachment = new FormData();
				attachment.append("file", new File([new Uint8Array([37, 80, 68, 70])], `${marker}.pdf`, { type: "application/pdf" }));
				const uploaded = await requestForm<JsonObject>(`/api/vehicles/${vehicleId}/files`, attachment);
				expect(uploaded.response.status).toBe(201);
				attachmentId = stringValue(objectValue(uploaded.body.file).id);
				expect(objectValue(uploaded.body.file)).toEqual(expect.objectContaining({ type: "pdf", contentType: "application/pdf", size: 4 }));

				const downloaded = await requestRaw(`/api/vehicles/${vehicleId}/files/${attachmentId}`);
				const downloadedBytes = new Uint8Array(await downloaded.arrayBuffer());
				expect(downloaded.status).toBe(200);
				expect(downloaded.headers.get("content-type")).toContain("application/pdf");
				expect(Array.from(downloadedBytes)).toEqual([37, 80, 68, 70]);
			} else {
				const missingStorageBackup = await requestJson<JsonObject>("/api/backups", "POST");
				expect(missingStorageBackup.response.status).toBe(503);
			}

			const exportHeaders = { customers: "顧客ID", vehicles: "車両ID", sales: "書類ID", maintenance: "書類ID", payments: "請求書ID" };
			for (const resource of ["customers", "vehicles", "sales", "maintenance", "payments"] as const) {
				const exported = await requestText(`/api/export/${resource}`);
				expect(exported.response.status).toBe(200);
				expect(exported.response.headers.get("content-type")).toContain("text/csv");
				if (!exported.text.startsWith(exportHeaders[resource])) throw new Error(`${resource} CSVの見出しが不正です。`);
				if (!exported.text.includes(marker)) throw new Error(`${resource} CSVにテストデータがありません。`);
			}

			const csv = [
				"顧客ID,顧客番号,顧客名,ふりがな,電話番号,メールアドレス,郵便番号,住所,メモ,車両台数",
				`${importedCustomerId},C-${marker},${marker} CSV顧客,シーエスブイ,090-2222-2222,csv@example.com,100-0003,東京都新宿区,${marker},0`,
			].join("\r\n");
			const importFile = new FormData();
			importFile.append("file", new File([csv], "customers.csv", { type: "text/csv" }));
			const preview = await requestForm<JsonObject>("/api/import/customers/preview", importFile);
			expect(preview.response.status).toBe(200);
			expect(preview.body.totalRows).toBe(1);
			expect(arrayValue(preview.body.errors)).toHaveLength(0);

			const commitImportFile = new FormData();
			commitImportFile.append("file", new File([csv], "customers.csv", { type: "text/csv" }));
			const committed = await requestForm<JsonObject>("/api/import/customers/commit", commitImportFile);
			expect(committed.response.status).toBe(200);
			expect(committed.body).toEqual(expect.objectContaining({ imported: 1, updated: 0, skipped: 0 }));

			const imported = await requestJson<JsonObject>(`/api/customers?q=${encodeURIComponent(importedCustomerId)}`);
			expect(imported.response.status).toBe(200);
			expect(arrayValue(imported.body.customers)).toEqual(expect.arrayContaining([
				expect.objectContaining({ id: importedCustomerId, name: `${marker} CSV顧客` }),
			]));

			const invalidRowCsv = [
				"顧客ID,顧客番号,顧客名,ふりがな,電話番号,メールアドレス,郵便番号,住所,メモ,車両台数",
				`${importedCustomerId},C-${marker}-INVALID,,シーエスブイ,090-3333-3333,invalid@example.com,100-0004,東京都台東区,${marker} invalid,0`,
			].join("\r\n");
			const invalidRowPreviewFile = new FormData();
			invalidRowPreviewFile.append("file", new File([invalidRowCsv], "invalid-row.csv", { type: "text/csv" }));
			const invalidRowPreview = await requestForm<JsonObject>("/api/import/customers/preview", invalidRowPreviewFile);
			expect(invalidRowPreview.response.status).toBe(200);
			expect(arrayValue(invalidRowPreview.body.errors)).toEqual(expect.arrayContaining([expect.objectContaining({ row: 2 })]));
			const invalidRowCommitFile = new FormData();
			invalidRowCommitFile.append("file", new File([invalidRowCsv], "invalid-row.csv", { type: "text/csv" }));
			const invalidRowCommit = await requestForm<JsonObject>("/api/import/customers/commit", invalidRowCommitFile);
			expect(invalidRowCommit.response.status).toBe(200);
			expect(invalidRowCommit.body.imported).toBe(0);
			expect(invalidRowCommit.body.skipped).toBe(1);
			expect(arrayValue(invalidRowCommit.body.errors)).toHaveLength(1);

			const employeeCannotManageMembers = await requestJson<JsonObject>(`/api/organization/members/${ownerUid}`, "PATCH", { status: "suspended" }, employeeUid);
			expect(employeeCannotManageMembers.response.status).toBe(403);
			const employeeImportAttemptFile = new FormData();
			employeeImportAttemptFile.append("file", new File([csv], "customers.csv", { type: "text/csv" }));
			const employeeCannotImport = await requestForm<JsonObject>("/api/import/customers/preview", employeeImportAttemptFile, employeeUid);
			expect(employeeCannotImport.response.status).toBe(403);
			const employeeCannotBackup = await requestJson<JsonObject>("/api/backups", "POST", undefined, employeeUid);
			expect(employeeCannotBackup.response.status).toBe(403);

			const suspended = await requestJson<JsonObject>(`/api/organization/members/${employeeUid}`, "PATCH", { status: "suspended" });
			expect(suspended.response.status).toBe(200);
			const blockedEmployee = await requestJson<JsonObject>("/api/customers", "GET", undefined, employeeUid);
			expect(blockedEmployee.response.status).toBe(403);
			const reactivated = await requestJson<JsonObject>(`/api/organization/members/${employeeUid}`, "PATCH", { status: "active" });
			expect(reactivated.response.status).toBe(200);
			const activeEmployee = await requestJson<JsonObject>("/api/customers", "GET", undefined, employeeUid);
			expect(activeEmployee.response.status).toBe(200);

			const removedEmployee = await requestJson<JsonObject>(`/api/organization/members/${employeeUid}`, "DELETE");
			expect(removedEmployee.response.status).toBe(200);
			expect(arrayValue(removedEmployee.body.members)).not.toEqual(
				expect.arrayContaining([expect.objectContaining({ uid: employeeUid })]),
			);

			const removedEmployeeAccess = await requestJson<JsonObject>("/api/customers", "GET", undefined, employeeUid);
			expect(removedEmployeeAccess.response.status).toBe(400);
            const readdedEmployee = await requestJson<JsonObject>("/api/organization/members", "POST", { displayName: marker + " 再追加従業員", email: employeeUid + "@example.com" });
            expect(readdedEmployee.response.status).toBe(201);
            expect(readdedEmployee.body.temporaryPassword).toEqual(expect.any(String));
            expect(stringValue(readdedEmployee.body.temporaryPassword).length).toBeGreaterThanOrEqual(16);
            expect(objectValue(readdedEmployee.body.member)).toEqual(expect.objectContaining({ uid: employeeUid, displayName: marker + " 再追加従業員", role: "employee", status: "active", mustChangePassword: true }));
			const readdedEmployeeAccess = await requestJson<JsonObject>("/api/customers", "GET", undefined, employeeUid);
			expect(readdedEmployeeAccess.response.status).toBe(200);

			const otherOrganizationAccess = await requestJson<JsonObject>("/api/customers", "GET", undefined, ownerUid, otherOrganizationId);
			expect(otherOrganizationAccess.response.status).toBe(400);
			const crossTenantCustomerMutation = await requestJson<JsonObject>(`/api/customers/other-customer-${marker.toLowerCase()}`, "PATCH", { name: `${marker} 越境更新` });
			expect(crossTenantCustomerMutation.response.status).toBe(404);
			const crossTenantVehicleHistory = await requestJson<JsonObject>(`/api/vehicles/vehicle-from-other-organization-${marker.toLowerCase()}/history`);
			expect(crossTenantVehicleHistory.response.status).toBe(404);

			if (b2Configured) {
				const createdBackup = await requestJson<JsonObject>("/api/backups", "POST");
				expect(createdBackup.response.status).toBe(201);
				backupId = stringValue(objectValue(createdBackup.body.backup).id);
				expect(objectValue(createdBackup.body.backup).rowCount).toEqual(expect.any(Number));
				const mismatchedRestoreConfirmation = await requestJson<JsonObject>(`/api/backups/${backupId}/restore`, "POST", { confirmId: "different-backup-id" });
				expect(mismatchedRestoreConfirmation.response.status).toBe(400);

				const changedAfterBackup = await requestJson<JsonObject>(`/api/customers/${customerId}`, "PATCH", {
					name: `${marker} 変更後`,
					memo: `${marker} changed after backup`,
				});
				expect(changedAfterBackup.response.status).toBe(200);

				const restored = await requestJson<JsonObject>(`/api/backups/${backupId}/restore`, "POST", { confirmId: backupId });
				expect(restored.response.status).toBe(200);
				expect(restored.body.restored).toBe(true);

				const afterRestore = await requestJson<JsonObject>(`/api/customers?q=${encodeURIComponent(marker)}`);
				expect(afterRestore.response.status).toBe(200);
				expect(arrayValue(afterRestore.body.customers)).toEqual(expect.arrayContaining([
					expect.objectContaining({ id: customerId, name: `${marker} 顧客 更新` }),
				]));
				const missingBackupRestore = await requestJson<JsonObject>(`/api/backups/missing-${marker.toLowerCase()}/restore`, "POST", { confirmId: `missing-${marker.toLowerCase()}` });
				expect(missingBackupRestore.response.status).toBe(404);

				const deletedBackup = await requestJson<JsonObject>(`/api/backups/${backupId}`, "DELETE");
				expect(deletedBackup.response.status).toBe(200);
				backupId = undefined;

				const deletedAttachment = await requestJson<JsonObject>(`/api/vehicles/${vehicleId}/files/${attachmentId}`, "DELETE");
				expect(deletedAttachment.response.status).toBe(200);
				attachmentId = undefined;
			}
		} finally {
			await cleanupTestData({ attachmentId, backupId, vehicleId });
		}
	}, 60000);
});

async function prepareTestOrganizations() {
	await env.DB.prepare("INSERT OR IGNORE INTO organizations (id, name, owner_uid, setup_completed) VALUES (?, ?, ?, 1)")
		.bind(organizationId, `${marker} 組織`, ownerUid)
		.run();
	await env.DB.prepare("INSERT OR IGNORE INTO organizations (id, name, owner_uid, setup_completed) VALUES (?, ?, ?, 1)")
		.bind(otherOrganizationId, `${marker} 別組織`, `other-${marker}`)
		.run();
	await env.DB.prepare("INSERT OR IGNORE INTO organization_memberships (id, organization_id, uid, role, status) VALUES (?, ?, ?, ?, ?)")
		.bind(`membership-owner-${marker}`, organizationId, ownerUid, "owner", "active")
		.run();
	await env.DB.prepare("INSERT OR IGNORE INTO organization_memberships (id, organization_id, uid, role, status) VALUES (?, ?, ?, ?, ?)")
		.bind(`membership-employee-${marker}`, organizationId, employeeUid, "employee", "active")
		.run();
	await env.DB.prepare("INSERT OR IGNORE INTO staff_profiles (uid, display_name, email, role) VALUES (?, ?, ?, ?)")
		.bind(ownerUid, `${marker} オーナー`, `${ownerUid}@example.com`, "owner")
		.run();
	await env.DB.prepare("INSERT OR IGNORE INTO staff_profiles (uid, display_name, email, role) VALUES (?, ?, ?, ?)")
		.bind(employeeUid, marker + " 従業員", employeeUid + "@example.com", "employee")
		.run();
	await env.DB.prepare("INSERT OR IGNORE INTO auth_accounts (uid, must_change_password) VALUES (?, 0)")
		.bind(ownerUid)
		.run();
	await env.DB.prepare("INSERT OR IGNORE INTO auth_accounts (uid, must_change_password) VALUES (?, 0)")
		.bind(employeeUid)
		.run();
	await env.DB.prepare("INSERT OR IGNORE INTO customers (id, organization_id, customer_number, name) VALUES (?, ?, ?, ?)")
		.bind(`other-customer-${marker.toLowerCase()}`, otherOrganizationId, `C-OTHER-${marker}`, `${marker} 別組織顧客`)
		.run();
}

async function cleanupTestData(state: { attachmentId?: string; backupId?: string; vehicleId?: string }) {
	if (state.attachmentId && state.vehicleId) {
		await requestJson<JsonObject>(`/api/vehicles/${state.vehicleId}/files/${state.attachmentId}`, "DELETE").catch(() => undefined);
	}
	if (state.backupId) {
		await requestJson<JsonObject>(`/api/backups/${state.backupId}`, "DELETE").catch(() => undefined);
	}

	for (const table of [
		"payment_records",
		"sales_document_items",
		"maintenance_items",
		"sales_documents",
		"maintenance_documents",
		"vehicle_files",
		"vehicles",
		"customers",
		"inspection_schedules",
		"app_settings",
		"backup_records",
	]) {
		await env.DB.prepare(`DELETE FROM ${table} WHERE organization_id = ?`).bind(organizationId).run();
	}
	await env.DB.prepare("DELETE FROM organization_memberships WHERE organization_id IN (?, ?) OR uid IN (?, ?)")
		.bind(organizationId, otherOrganizationId, ownerUid, employeeUid)
		.run();
	await env.DB.prepare("DELETE FROM staff_profiles WHERE uid IN (?, ?)").bind(ownerUid, employeeUid).run();
	await env.DB.prepare("DELETE FROM auth_accounts WHERE uid IN (?, ?)").bind(ownerUid, employeeUid).run();
	await env.DB.prepare("DELETE FROM organizations WHERE id IN (?, ?)").bind(organizationId, otherOrganizationId).run();
}

async function requestJson<T extends JsonObject>(
	path: string,
	method = "GET",
	payload?: unknown,
	uid = ownerUid,
	selectedOrganizationId = organizationId,
) {
	return requestJsonWithHeaders<T>(path, authHeaders(uid, selectedOrganizationId), method, payload);
}

async function requestJsonWithoutAuth<T extends JsonObject>(path: string, method = "GET", payload?: unknown) {
	return requestJsonWithHeaders<T>(path, new Headers(), method, payload);
}

async function requestJsonWithToken<T extends JsonObject>(path: string, token: string, method = "GET", payload?: unknown, selectedOrganizationId = organizationId) {
	return requestJsonWithHeaders<T>(path, new Headers({ Authorization: `Bearer ${token}`, "X-Organization-Id": selectedOrganizationId }), method, payload);
}

async function requestJsonWithHeaders<T extends JsonObject>(path: string, headers: Headers, method = "GET", payload?: unknown) {
	const requestInit: RequestInit = { method, headers };
	if (payload !== undefined) {
		headers.set("Content-Type", "application/json");
		requestInit.body = JSON.stringify(payload);
	}
	const response = await SELF.fetch(new Request(`https://example.com${path}`, requestInit));
	const body = await response.json() as T;
	return { response, body };
}

async function requestForm<T extends JsonObject>(path: string, formData: FormData, uid = ownerUid, selectedOrganizationId = organizationId) {
	const response = await SELF.fetch(new Request(`https://example.com${path}`, {
		method: "POST",
		headers: authHeaders(uid, selectedOrganizationId),
		body: formData,
	}));
	const body = await response.json() as T;
	return { response, body };
}

async function requestRaw(path: string, uid = ownerUid, selectedOrganizationId = organizationId) {
	return SELF.fetch(new Request(`https://example.com${path}`, {
		method: "GET",
		headers: authHeaders(uid, selectedOrganizationId),
	}));
}

async function requestText(path: string, uid = ownerUid, selectedOrganizationId = organizationId) {
	const response = await requestRaw(path, uid, selectedOrganizationId);
	return { response, text: await response.text() };
}

function authHeaders(uid: string, selectedOrganizationId: string) {
	return new Headers({
		Authorization: `Bearer ${emulatorToken(uid)}`,
		"X-Organization-Id": selectedOrganizationId,
	});
}

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

function base64Url(value: string) {
	const bytes = new TextEncoder().encode(value);
	let binary = "";
	for (const byte of bytes) binary += String.fromCharCode(byte);
	return btoa(binary).replaceAll("+", "-").replaceAll("/", "_").replace(/=+$/u, "");
}

function isB2Configured(value: TestEnv) {
	return Boolean(value.B2_ENDPOINT && value.B2_REGION && value.B2_BUCKET && value.B2_KEY_ID && value.B2_APPLICATION_KEY);
}

function objectValue(value: unknown): JsonObject {
	return value && typeof value === "object" && !Array.isArray(value) ? value as JsonObject : {};
}

function arrayValue(value: unknown): JsonObject[] {
	return Array.isArray(value) ? value.filter((item): item is JsonObject => Boolean(item) && typeof item === "object" && !Array.isArray(item)) : [];
}

function stringArrayValue(value: unknown): string[] {
	return Array.isArray(value) ? value.filter((item): item is string => typeof item === "string") : [];
}

function stringValue(value: unknown) {
	return typeof value === "string" ? value : "";
}
