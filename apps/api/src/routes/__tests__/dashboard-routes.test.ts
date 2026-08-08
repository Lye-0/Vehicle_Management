import { describe, expect, it } from "vitest";
import { buildCalendarEvents } from "../dashboard-routes";

describe("dashboard calendar maintenance events", () => {
  it("車検・一般整備・板金を整備予定として表示する", () => {
    const categories = ["車検", "一般整備", "板金"] as const;
    const maintenanceRows = categories.map((category, index) => ({
      id: `maintenance-calendar-${index}`,
      category,
      type: "整備請求書",
      number: `M-CALENDAR-${index}`,
      status: "下書き",
      customerId: "customer-calendar",
      vehicleId: "vehicle-calendar",
      issuedAt: "2026-08-08",
      intakeDate: `2026-08-${String(8 + index).padStart(2, "0")}`,
      plannedReleaseDate: `2026-08-${String(10 + index).padStart(2, "0")}`,
      completionDate: null,
      dueDate: null,
      total: 0,
    })) as unknown as Parameters<typeof buildCalendarEvents>[3];

    const events = buildCalendarEvents([], [], [], maintenanceRows, [], [], [], new Map(), new Map());
    const intakeEvents = events.filter((event) => event.id.startsWith("inspection-document-"));

    expect(intakeEvents).toHaveLength(3);
    expect(intakeEvents.map((event) => event.categoryLabel)).toEqual([
      "整備：車検",
      "整備：一般整備",
      "整備：板金",
    ]);
    expect(intakeEvents.map((event) => event.date)).toEqual(["2026-08-08", "2026-08-09", "2026-08-10"]);
    expect(intakeEvents.map((event) => event.endDate)).toEqual(["2026-08-10", "2026-08-11", "2026-08-12"]);
  });
});
