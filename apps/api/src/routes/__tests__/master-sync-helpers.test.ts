import { describe, expect, it } from "vitest";
import {
  CUSTOMER_FIELD_TO_DB_COLUMN,
  CUSTOMER_SYNC_ALLOWLIST,
  VEHICLE_FIELD_TO_DB_COLUMN,
  VEHICLE_SYNC_ALLOWLIST,
  extractCustomerFieldsFromOverride,
  extractVehicleFieldsFromOverride,
  findDuplicateCustomers,
  findDuplicateVehicles,
  isBlankValue,
  normalizeChassisNumberForDuplicate,
  normalizeEmailForDuplicate,
  normalizePhoneForDuplicate,
  normalizeRegistrationNumberForDuplicate,
  validateCombination,
  validateSyncFields,
} from "../../lib/master-sync-helpers";

describe("master-sync-helpers", () => {
  describe("isBlankValue", () => {
    it("nullを空欄と判定", () => {
      expect(isBlankValue(null)).toBe(true);
    });

    it("undefinedを空欄と判定", () => {
      expect(isBlankValue(undefined)).toBe(true);
    });

    it("空文字を空欄と判定", () => {
      expect(isBlankValue("")).toBe(true);
    });

    it("空白文字のみを空欄と判定", () => {
      expect(isBlankValue("   ")).toBe(true);
    });

    it("有効な文字列を空欄でないと判定", () => {
      expect(isBlankValue("test")).toBe(false);
    });

    it("数値を空欄でないと判定", () => {
      expect(isBlankValue(0)).toBe(false);
    });
  });

  describe("正規化関数", () => {
    it("電話番号から非数字を除去", () => {
      expect(normalizePhoneForDuplicate("090-1234-5678")).toBe("09012345678");
    });

    it("電話番号の全角数字を半角化", () => {
      expect(normalizePhoneForDuplicate("０９０１２３４５６７８")).toBe("09012345678");
    });

    it("登録番号から空白とハイフンを除去し大文字化", () => {
      expect(normalizeRegistrationNumberForDuplicate("品川 300 い 5678")).toBe("品川300い5678");
    });

    it("車台番号から空白とハイフンを除去し大文字化", () => {
      expect(normalizeChassisNumberForDuplicate("ZVW50-00001")).toBe("ZVW5000001");
    });

    it("メールアドレスをtrim後に小文字化", () => {
      expect(normalizeEmailForDuplicate("  Test@Example.COM  ")).toBe("test@example.com");
    });

    it("null/undefinedを空文字に正規化", () => {
      expect(normalizePhoneForDuplicate(null)).toBe("");
      expect(normalizePhoneForDuplicate(undefined)).toBe("");
    });

    it("正規化後が空欄の値を返さない", () => {
      expect(normalizePhoneForDuplicate("---")).toBe("");
    });
  });

  describe("allowlist", () => {
    it("CUSTOMER_SYNC_ALLOWLISTに期待されるフィールドが含まれる", () => {
      expect(CUSTOMER_SYNC_ALLOWLIST.has("name")).toBe(true);
      expect(CUSTOMER_SYNC_ALLOWLIST.has("nameKana")).toBe(true);
      expect(CUSTOMER_SYNC_ALLOWLIST.has("phone")).toBe(true);
      expect(CUSTOMER_SYNC_ALLOWLIST.has("postalCode")).toBe(true);
      expect(CUSTOMER_SYNC_ALLOWLIST.has("address")).toBe(true);
      expect(CUSTOMER_SYNC_ALLOWLIST.has("email")).toBe(false);
      expect(CUSTOMER_SYNC_ALLOWLIST.has("memo")).toBe(false);
    });

    it("VEHICLE_SYNC_ALLOWLISTにmileageが含まれない", () => {
      expect(VEHICLE_SYNC_ALLOWLIST.has("mileage")).toBe(false);
    });

    it("VEHICLE_SYNC_ALLOWLISTに期待されるフィールドが含まれる", () => {
      expect(VEHICLE_SYNC_ALLOWLIST.has("maker")).toBe(true);
      expect(VEHICLE_SYNC_ALLOWLIST.has("name")).toBe(true);
      expect(VEHICLE_SYNC_ALLOWLIST.has("model")).toBe(true);
      expect(VEHICLE_SYNC_ALLOWLIST.has("registrationNumber")).toBe(true);
      expect(VEHICLE_SYNC_ALLOWLIST.has("chassisNumber")).toBe(true);
      expect(VEHICLE_SYNC_ALLOWLIST.has("modelYear")).toBe(true);
      expect(VEHICLE_SYNC_ALLOWLIST.has("inspectionDate")).toBe(true);
      expect(VEHICLE_SYNC_ALLOWLIST.has("bodyColor")).toBe(true);
      expect(VEHICLE_SYNC_ALLOWLIST.has("displacement")).toBe(true);
      expect(VEHICLE_SYNC_ALLOWLIST.has("transmission")).toBe(true);
    });
  });

  describe("生SQLカラムマップ", () => {
    it("CUSTOMER_FIELD_TO_DB_COLUMNが実DBカラム名と一致", () => {
      expect(CUSTOMER_FIELD_TO_DB_COLUMN.name).toBe("name");
      expect(CUSTOMER_FIELD_TO_DB_COLUMN.nameKana).toBe("name_kana");
      expect(CUSTOMER_FIELD_TO_DB_COLUMN.phone).toBe("phone");
      expect(CUSTOMER_FIELD_TO_DB_COLUMN.postalCode).toBe("postal_code");
      expect(CUSTOMER_FIELD_TO_DB_COLUMN.address).toBe("address");
    });

    it("VEHICLE_FIELD_TO_DB_COLUMNが実DBカラム名と一致", () => {
      expect(VEHICLE_FIELD_TO_DB_COLUMN.maker).toBe("maker");
      expect(VEHICLE_FIELD_TO_DB_COLUMN.name).toBe("name");
      expect(VEHICLE_FIELD_TO_DB_COLUMN.model).toBe("model");
      expect(VEHICLE_FIELD_TO_DB_COLUMN.registrationNumber).toBe("registration_number");
      expect(VEHICLE_FIELD_TO_DB_COLUMN.chassisNumber).toBe("chassis_number");
      expect(VEHICLE_FIELD_TO_DB_COLUMN.modelYear).toBe("model_year");
      expect(VEHICLE_FIELD_TO_DB_COLUMN.inspectionDate).toBe("inspection_date");
      expect(VEHICLE_FIELD_TO_DB_COLUMN.bodyColor).toBe("body_color");
      expect(VEHICLE_FIELD_TO_DB_COLUMN.displacement).toBe("displacement");
      expect(VEHICLE_FIELD_TO_DB_COLUMN.transmission).toBe("transmission");
    });
  });

  describe("validateSyncFields", () => {
    it("undefinedの場合はnullを返す", () => {
      expect(validateSyncFields(undefined, CUSTOMER_SYNC_ALLOWLIST)).toBeNull();
    });

    it("allowlist内のフィールドのみならnullを返す", () => {
      expect(validateSyncFields(["name", "phone"], CUSTOMER_SYNC_ALLOWLIST)).toBeNull();
    });

    it("allowlist外のフィールドがあればそのリストを返す", () => {
      const invalid = validateSyncFields(["name", "email"], CUSTOMER_SYNC_ALLOWLIST);
      expect(invalid).toEqual(["email"]);
    });
  });

  describe("extractCustomerFieldsFromOverride", () => {
    it("overrideからallowlist対応フィールドを抽出", () => {
      const result = extractCustomerFieldsFromOverride({
        name: " 山田太郎 ",
        kana: "やまだたろう",
        phone: "090-1234-5678",
        postalCode: "100-0001",
        address: "東京都",
      });
      expect(result.name).toBe("山田太郎");
      expect(result.nameKana).toBe("やまだたろう");
      expect(result.phone).toBe("090-1234-5678");
      expect(result.postalCode).toBe("100-0001");
      expect(result.address).toBe("東京都");
    });

    it("空欄のフィールドは除外", () => {
      const result = extractCustomerFieldsFromOverride({
        name: "山田太郎",
        phone: "  ",
        address: "",
      });
      expect(result.name).toBe("山田太郎");
      expect(result.phone).toBeUndefined();
      expect(result.address).toBeUndefined();
    });

    it("null/undefinedの場合は空オブジェクト", () => {
      expect(extractCustomerFieldsFromOverride(null)).toEqual({});
      expect(extractCustomerFieldsFromOverride(undefined)).toEqual({});
    });
  });

  describe("extractVehicleFieldsFromOverride", () => {
    it("overrideキーをsyncフィールド名に変換", () => {
      const result = extractVehicleFieldsFromOverride({
        maker: "トヨタ",
        name: "プリウス",
        modelType: "ZVW50",
        plate: "品川300い5678",
        vin: "ZVW5000001",
        year: "2020年",
        color: "パールホワイト",
      });
      expect(result.maker).toBe("トヨタ");
      expect(result.name).toBe("プリウス");
      expect(result.model).toBe("ZVW50");
      expect(result.registrationNumber).toBe("品川300い5678");
      expect(result.chassisNumber).toBe("ZVW5000001");
      expect(result.modelYear).toBe("2020年");
      expect(result.bodyColor).toBe("パールホワイト");
    });
  });

  describe("findDuplicateCustomers", () => {
    const mockCustomers = [
      { id: "c1", name: "山田太郎", phone: "090-1234-5678", email: "yamada@example.com" },
      { id: "c2", name: "佐藤花子", phone: "080-2345-6789", email: "sato@example.com" },
    ];

    it("電話番号の正規化後一致で重複検出", () => {
      const result = findDuplicateCustomers(mockCustomers, { name: "新顧客", phone: "09012345678" });
      expect(result).toHaveLength(1);
      expect(result[0].id).toBe("c1");
      expect(result[0].matchReason).toBe("phone");
    });

    it("メールアドレスの小文字化後一致で重複検出", () => {
      const result = findDuplicateCustomers(mockCustomers, { name: "新顧客", email: "YAMADA@EXAMPLE.COM" });
      expect(result).toHaveLength(1);
      expect(result[0].matchReason).toBe("email");
    });

    it("excludeCustomerIdを除外", () => {
      const result = findDuplicateCustomers(mockCustomers, { name: "山田太郎", phone: "09012345678" }, "c1");
      expect(result).toHaveLength(0);
    });

    it("正規化後が空欄の値は比較対象外", () => {
      const result = findDuplicateCustomers(mockCustomers, { name: "新顧客", phone: "---" });
      expect(result).toHaveLength(0);
    });
  });

  describe("findDuplicateVehicles", () => {
    const mockVehicles = [
      { id: "v1", maker: "トヨタ", name: "プリウス", registrationNumber: "品川300い5678", chassisNumber: "ZVW5000001" },
    ];

    it("車台番号の正規化後一致で重複検出", () => {
      const result = findDuplicateVehicles(mockVehicles, { maker: "トヨタ", name: "プリウス", chassisNumber: "ZVW50-00001" });
      expect(result).toHaveLength(1);
      expect(result[0].matchReason).toBe("chassis_number");
    });

    it("登録番号の正規化後一致で重複検出", () => {
      const result = findDuplicateVehicles(mockVehicles, { maker: "トヨタ", name: "プリウス", registrationNumber: "品川 300 い 5678" });
      expect(result).toHaveLength(1);
      expect(result[0].matchReason).toBe("registration_number");
    });

    it("excludeVehicleIdを除外", () => {
      const result = findDuplicateVehicles(mockVehicles, { maker: "トヨタ", name: "プリウス", chassisNumber: "ZVW5000001" }, "v1");
      expect(result).toHaveLength(0);
    });
  });

  describe("validateCombination", () => {
    it("customerIdとnewCustomerの同時指定を拒否", () => {
      const result = validateCombination({
        customerId: "c1",
        newCustomer: { name: "新顧客" },
        documentType: "sales",
      });
      expect(result).not.toBeNull();
      expect(result!.status).toBe(400);
    });

    it("vehicleIdとnewVehicleの同時指定を拒否", () => {
      const result = validateCombination({
        customerId: "c1",
        vehicleId: "v1",
        newVehicle: { maker: "トヨタ", name: "プリウス" },
        documentType: "sales",
      });
      expect(result).not.toBeNull();
      expect(result!.status).toBe(400);
    });

    it("顧客未指定を拒否", () => {
      const result = validateCombination({
        documentType: "sales",
      });
      expect(result).not.toBeNull();
      expect(result!.status).toBe(400);
    });

    it("整備書類で車両未指定を拒否", () => {
      const result = validateCombination({
        customerId: "c1",
        documentType: "maintenance",
      });
      expect(result).not.toBeNull();
      expect(result!.status).toBe(400);
    });

    it("新規顧客＋既存車両を拒否", () => {
      const result = validateCombination({
        newCustomer: { name: "新顧客" },
        vehicleId: "v1",
        documentType: "sales",
      });
      expect(result).not.toBeNull();
      expect(result!.status).toBe(400);
    });

    it("既存顧客＋既存車両は許可", () => {
      const result = validateCombination({
        customerId: "c1",
        vehicleId: "v1",
        documentType: "sales",
      });
      expect(result).toBeNull();
    });

    it("新規顧客＋新規車両は許可", () => {
      const result = validateCombination({
        newCustomer: { name: "新顧客" },
        newVehicle: { maker: "トヨタ", name: "プリウス" },
        documentType: "sales",
      });
      expect(result).toBeNull();
    });

    it("販売書類で車両なしは許可", () => {
      const result = validateCombination({
        customerId: "c1",
        documentType: "sales",
      });
      expect(result).toBeNull();
    });
  });
});
