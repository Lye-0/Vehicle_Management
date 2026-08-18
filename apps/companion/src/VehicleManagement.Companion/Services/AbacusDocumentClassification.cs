using System.Globalization;
using System.Text;

namespace VehicleManagement.Companion.Services;

public sealed record AbacusDocumentTypeClassification(
    string Value,
    string RawValue,
    string? Warning);

public sealed record AbacusMaintenanceCategoryClassification(
    string Value,
    string RawValue,
    string? Warning);

public sealed record AbacusImportedStatusClassification(
    string Value,
    string? Warning);

/// <summary>
/// ABACUSの自由入力に近い書類種別・入庫区分をWeb側の列挙値へ変換します。
/// 変換できない原文は捨てず、呼び出し側が詳細JSONやレポートへ保存できるように警告を返します。
/// </summary>
public static class AbacusDocumentClassification
{
    public static AbacusDocumentTypeClassification NormalizeDocumentType(string? value, string fallback)
    {
        var raw = Clean(value);
        var normalized = raw.Normalize(NormalizationForm.FormKC);
        if (normalized.Contains("見積", StringComparison.Ordinal))
        {
            return new("見積書", raw, null);
        }

        if (normalized.Contains("請求", StringComparison.Ordinal))
        {
            return new("請求書", raw, null);
        }

        var safeFallback = fallback.Contains("整備", StringComparison.Ordinal) ? "請求書" : fallback;
        return new(
            safeFallback,
            raw,
            string.IsNullOrWhiteSpace(raw)
                ? $"ABACUSの書類種別が空欄のため「{safeFallback}」として扱いました。"
                : $"ABACUSの書類種別「{raw}」を判定できないため「{safeFallback}」として扱いました。");
    }

    public static AbacusMaintenanceCategoryClassification NormalizeMaintenanceCategory(string? value)
    {
        var raw = Clean(value);
        var normalized = raw.Normalize(NormalizationForm.FormKC);
        if (normalized.Contains("車検", StringComparison.Ordinal))
        {
            return new("車検", raw, null);
        }

        if (normalized.Contains("板金", StringComparison.Ordinal) ||
            normalized.Contains("鈑金", StringComparison.Ordinal) ||
            normalized.Contains("事故", StringComparison.Ordinal) ||
            normalized.Contains("保険修理", StringComparison.Ordinal))
        {
            return new("板金", raw, null);
        }

        if (normalized is "一般整備" or "点検" or "1年点検" or "１年点検" or "12か月点検" or "１２か月点検" or "24か月点検" or "２４か月点検")
        {
            return new("一般整備", raw, null);
        }

        return new(
            "一般整備",
            raw,
            string.IsNullOrWhiteSpace(raw)
                ? "ABACUSの入庫区分が空欄のため「一般整備」として扱いました。"
                : $"ABACUSの入庫区分「{raw}」を「一般整備」として扱いました。");
    }

    public static AbacusImportedStatusClassification CalculateStatus(string? documentDate, DateTime importBaseDate)
    {
        var raw = Clean(documentDate);
        if (!DateTime.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return new("下書き", string.IsNullOrWhiteSpace(raw)
                ? "書類日付が空欄のため「下書き」として扱いました。"
                : $"書類日付「{raw}」が不正なため「下書き」として扱いました。");
        }

        return new(
            date.Date <= importBaseDate.Date.AddMonths(-1) ? "完了" : "下書き",
            null);
    }

    private static string Clean(string? value) =>
        (value ?? string.Empty).Normalize(NormalizationForm.FormKC).Trim();
}
