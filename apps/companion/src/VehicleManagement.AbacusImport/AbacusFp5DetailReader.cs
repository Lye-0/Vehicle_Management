using System.Globalization;
using System.Text;

namespace VehicleManagement.AbacusImport;

public sealed record AbacusDetailLine(
    string? Description,
    decimal? Quantity,
    string? Unit,
    long? UnitPrice,
    long? PartAmount,
    long? TechnicalFees,
    string? Summary,
    int SourceRowIndex)
{
    public bool HasValue => Description is not null || Quantity is not null || Unit is not null || UnitPrice is not null || PartAmount is not null || TechnicalFees is not null || Summary is not null;
    public bool IsAmountOnly => Description is null && (PartAmount is not null || UnitPrice is not null || TechnicalFees is not null);
}

/// <summary>
/// ABACUSの明細表とは別の金額欄です。
/// 元の行明細と区別して保持し、登録時にWebの対応する内訳へ変換します。
/// </summary>
public sealed record AbacusDetailFinancialLine(
    string Description,
    string ItemType,
    string TaxCategory,
    long Amount,
    int SourceRowIndex);

public sealed record AbacusUcsDetailDocument(
    string Kind,
    string SourceFileName,
    string RecordIdHex,
    string DocumentNumber,
    string CustomerName,
    string VehicleName,
    string RegistrationNumber,
    string ChassisNumber,
    IReadOnlyList<AbacusDetailLine> Lines,
    long? PartsSubtotal,
    long? TechnicalSubtotal,
    long? AbacusSubtotal,
    long? AbacusTotal,
    int ExcludedDetailCount,
    IReadOnlyList<AbacusDetailFinancialLine>? FinancialLines = null,
    long? AbacusTax = null,
    long? AbacusTaxRate = null)
{
    public long DetailAmount =>
        Lines.Sum(line => line.PartAmount ?? 0) +
        Lines.Sum(line => line.TechnicalFees ?? 0) +
        (string.Equals(Kind, "販売書類", StringComparison.Ordinal)
            ? FinancialLines?.Sum(line => line.Amount) ?? 0
            : 0);
}

public sealed record AbacusUcsDetailReadResult(
    string SourceFolderPath,
    IReadOnlyList<AbacusUcsDetailDocument> Documents,
    int UnsupportedFileCount,
    IReadOnlyList<string> Warnings);

/// <summary>
/// abx-cs-hb.ucs（販売）とabx-cs-sb.ucs（整備）のFP5レコードから、
/// ABACUS画面の18行明細を復元します。行順ではなく、書類番号・顧客・車両識別子で
/// 後段のCSV書類へ照合するための読み取り専用データです。
/// </summary>
public sealed class AbacusFp5DetailReader
{
    private static readonly Encoding AbacusEncoding = CreateEncoding();
    private static readonly int[] DescriptionReferences = [.. Enumerable.Range(0, 10).Select(index => 0x801F + index), 0x8033, 0x8034, .. Enumerable.Range(0, 6).Select(index => 0x805B + index)];
    private static readonly int[] QuantityReferences = [.. Enumerable.Range(0, 10).Select(index => 0x8029 + index), 0x8035, 0x8036, .. Enumerable.Range(0, 6).Select(index => 0x8061 + index)];
    private static readonly int[] UnitReferences = [.. Enumerable.Range(0, 18).Select(index => 0x8068 + index)];
    private static readonly int[] UnitPriceReferences = [.. Enumerable.Range(0, 18).Select(index => 0x807A + index)];
    private static readonly int[] PartAmountReferences = [.. Enumerable.Range(0, 18).Select(index => 0x808C + index)];
    private static readonly int[] TechnicalFeeReferences = [.. Enumerable.Range(0, 18).Select(index => 0x809E + index)];
    private static readonly int[] SummaryReferences = [.. Enumerable.Range(0, 18).Select(index => 0x80B0 + index)];

    public async Task<AbacusUcsDetailReadResult> ReadFolderAsync(string sourceFolder, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(sourceFolder);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"ABACUSフォルダーが見つかりません: {root}");
        var documents = new List<AbacusUcsDetailDocument>();
        var warnings = new List<string>();
        var unsupported = 0;
        foreach (var (fileName, kind) in new[] { ("abx-cs-hb.ucs", "販売書類"), ("abx-cs-sb.ucs", "整備書類") })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(root, fileName);
            if (!File.Exists(path))
            {
                unsupported++;
                warnings.Add($"{fileName}がないため、{kind}のABACUS明細は抽出していません。");
                continue;
            }

            var stream = await new AbacusFp5RawTokenReader().ReadAsync(path, cancellationToken);
            documents.AddRange(ParseDocuments(stream, kind, fileName));
        }

        return new AbacusUcsDetailReadResult(root, documents, unsupported, warnings);
    }

    private static IReadOnlyList<AbacusUcsDetailDocument> ParseDocuments(AbacusFp5RawTokenStream stream, string kind, string fileName)
    {
        var result = new List<AbacusUcsDetailDocument>();
        var groups = stream.Tokens
            .Where(token => token.Path.Count == 2 && token.Path[0].Length == 1 && token.Path[0][0] == 0x05)
            .GroupBy(token => Convert.ToHexString(token.Path[1]), StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var fields = new Dictionary<int, string>();
            foreach (var token in group)
            {
                var reference = ReadReference(token.Reference);
                if (reference is null || fields.ContainsKey(reference.Value)) continue;
                fields[reference.Value] = DecodeValue(token.Data);
            }

            var isSales = string.Equals(kind, "販売書類", StringComparison.Ordinal);
            var lineCount = isSales ? 12 : 18;
            var lines = new List<AbacusDetailLine>();
            for (var index = 0; index < lineCount; index++)
            {
                var line = isSales
                    ? new AbacusDetailLine(
                        NullableText(fields, DescriptionReferences, index),
                        null,
                        null,
                        null,
                        NullableLong(fields, QuantityReferences, index),
                        null,
                        null,
                        index + 1)
                    : new AbacusDetailLine(
                        NullableText(fields, DescriptionReferences, index),
                        NullableDecimal(fields, QuantityReferences, index),
                        NullableText(fields, UnitReferences, index),
                        NullableLong(fields, UnitPriceReferences, index),
                        NullableLong(fields, PartAmountReferences, index),
                        NullableLong(fields, TechnicalFeeReferences, index),
                        NullableText(fields, SummaryReferences, index),
                        index + 1);
                // 品名だけで金額・数量・摘要がない行は、ABACUSの見出し／作業グループ行として除外します。
                // 金額だけの行は車両請求額を構成するため、descriptionが空でも保持します。
                if (line.HasValue && (line.Quantity is not null || line.Unit is not null || line.UnitPrice is not null || line.PartAmount is not null || line.TechnicalFees is not null || line.Summary is not null)) lines.Add(line);
            }

            var financialLines = isSales ? CreateSalesFinancialLines(fields, lines) : MapMaintenanceFinancialLines(fields);
            (long? Subtotal, long? Tax, long? Total)? salesAmounts = isSales ? ReadSalesAmounts(fields, financialLines) : null;

            var documentNumber = FirstNonEmpty(fields, 0x1F, 0x8055, 0x81A2) ?? ExtractTrailingNumber(FirstNonEmpty(fields, 0x8162));
            if (string.IsNullOrWhiteSpace(documentNumber)) continue;
            result.Add(new AbacusUcsDetailDocument(
                kind,
                fileName,
                group.Key,
                NormalizeDocumentNumber(documentNumber),
                FirstNonEmpty(fields, 0x01) ?? "",
                FirstNonEmpty(fields, 0x09) ?? "",
                FirstNonEmpty(fields, 0x21) ?? "",
                FirstNonEmpty(fields, 0x3A) ?? "",
                lines,
                isSales ? NullableLong(fields, 0x54) : NullableLong(fields, 0x8106),
                isSales ? null : NullableLong(fields, 0x8107),
                isSales ? salesAmounts?.Subtotal : NullableLong(fields, 0x812B),
                isSales ? salesAmounts?.Total : NullableLong(fields, 0x812C),
                lineCount - lines.Count,
                financialLines,
                isSales ? salesAmounts?.Tax : null,
                isSales ? NullableLong(fields, 0x80EC) ?? NullableLong(fields, 0x80ED) : null));
        }

        return result;
    }

    private static IReadOnlyList<AbacusDetailFinancialLine> CreateSalesFinancialLines(
        IReadOnlyDictionary<int, string> fields,
        IReadOnlyList<AbacusDetailLine> detailLines)
    {
        var result = new List<AbacusDetailFinancialLine>();
        AddFinancialLine(result, fields, 0x19, "車両本体価格", "車両本体価格", "課税", 91);
        AddFinancialLine(result, fields, 0x1A, "値引等", "値引等", "課税", 92);
        AddFinancialLine(result, fields, 0x43, "諸費用（課税）", "手続代行費用", "課税", 93);

        // 取得税などの税金・保険料は、ABACUSでは非課税欄にまとめて表示されます。
        var nonTaxableTotal = NullableLong(fields, 0x8101)
            ?? SumNullable(NullableLong(fields, 0x40), NullableLong(fields, 0x45));
        if (nonTaxableTotal is not null && nonTaxableTotal.Value != 0)
        {
            result.Add(new AbacusDetailFinancialLine("諸費用（非課税）", "法定費用", "非課税", nonTaxableTotal.Value, 94));
        }

        // 付属品の合計欄と行明細が一致しない古い書類では、差分だけを保持します。
        var accessoryTotal = NullableLong(fields, 0x54);
        var accessoryDetailTotal = detailLines.Sum(line => line.PartAmount ?? 0);
        var accessoryRemainder = accessoryTotal - accessoryDetailTotal;
        if (accessoryRemainder is not null && accessoryRemainder.Value != 0)
        {
            result.Add(new AbacusDetailFinancialLine("付属品・特別仕様（合計差分）", "付属品・特別仕様", "課税", accessoryRemainder.Value, 95));
        }

        return result;
    }

    /// <summary>
    /// 整備書類の明細表とは別に保存されている法定費用・調整額を復元します。
    /// 0x8163は新しいデータのリサイクル料金、0x810Cは旧データのその他費用欄です。
    /// 0x80CE/0x80E4のような合計欄は個別費用との二重計上になるため使用しません。
    /// </summary>
    public static IReadOnlyList<AbacusDetailFinancialLine> MapMaintenanceFinancialLines(
        IReadOnlyDictionary<int, string> fields)
    {
        var result = new List<AbacusDetailFinancialLine>();
        AddFinancialLine(result, fields, 0x76, "自賠責", "法定費用", "非課税", 91);
        AddFinancialLine(result, fields, 0x75, "重量税", "法定費用", "非課税", 92);
        AddFinancialLine(result, fields, 0x80CD, "印紙代", "法定費用", "非課税", 93);

        // 旧形式では同じ表示枠が0x810C（諸費用金額④）に保存されています。
        var otherFee = NullableLong(fields, 0x8163) ?? NullableLong(fields, 0x810C);
        AddFinancialLine(result, otherFee, "リサイクル料金", "法定費用", "非課税", 94);
        AddFinancialLine(result, fields, 0x80D0, "端数値引", "調整", "対象外", 95);
        return result;
    }

    private static void AddFinancialLine(
        ICollection<AbacusDetailFinancialLine> result,
        IReadOnlyDictionary<int, string> fields,
        int reference,
        string description,
        string itemType,
        string taxCategory,
        int sourceRowIndex)
    {
        AddFinancialLine(result, NullableLong(fields, reference), description, itemType, taxCategory, sourceRowIndex);
    }

    private static void AddFinancialLine(
        ICollection<AbacusDetailFinancialLine> result,
        long? value,
        string description,
        string itemType,
        string taxCategory,
        int sourceRowIndex)
    {
        if (value is null || value.Value == 0) return;
        result.Add(new AbacusDetailFinancialLine(description, itemType, taxCategory, value.Value, sourceRowIndex));
    }

    private static long? SumNullable(long? first, long? second) =>
        first is null && second is null ? null : (first ?? 0) + (second ?? 0);

    private static (long? Subtotal, long? Tax, long? Total) ReadSalesAmounts(
        IReadOnlyDictionary<int, string> fields,
        IReadOnlyList<AbacusDetailFinancialLine> financialLines)
    {
        var taxIncluded = NullableLong(fields, 0x80FA);
        var taxRate = NullableLong(fields, 0x80EC) ?? NullableLong(fields, 0x80ED);
        var nonTaxable = NullableLong(fields, 0x8101)
            ?? SumNullable(NullableLong(fields, 0x40), NullableLong(fields, 0x45));
        if (taxIncluded is not null && taxRate is > 0)
        {
            var tax = (long)Math.Floor(taxIncluded.Value * (double)taxRate.Value / (100d + taxRate.Value));
            var taxableSubtotal = taxIncluded.Value - tax;
            var subtotal = taxableSubtotal + (nonTaxable ?? 0);
            var total = taxIncluded.Value + (nonTaxable ?? 0);
            return (subtotal, tax, total);
        }

        var detailTotal = financialLines.Sum(line => line.Amount);
        return detailTotal > 0 ? (detailTotal, null, detailTotal) : (null, null, null);
    }

    private static string? NullableText(IReadOnlyDictionary<int, string> fields, IReadOnlyList<int> references, int index) =>
        index < references.Count ? NullableText(fields, references[index]) : null;

    private static string? NullableText(IReadOnlyDictionary<int, string> fields, int reference)
    {
        if (!fields.TryGetValue(reference, out var value)) return null;
        var normalized = value.Replace("\0", "", StringComparison.Ordinal).Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    private static decimal? NullableDecimal(IReadOnlyDictionary<int, string> fields, IReadOnlyList<int> references, int index) =>
        index < references.Count ? ParseDecimal(NullableText(fields, references[index])) : null;

    private static long? NullableLong(IReadOnlyDictionary<int, string> fields, IReadOnlyList<int> references, int index) =>
        index < references.Count ? NullableLong(fields, references[index]) : null;

    private static long? NullableLong(IReadOnlyDictionary<int, string> fields, int reference) =>
        ParseLong(NullableText(fields, reference));

    private static decimal? ParseDecimal(string? value) =>
        decimal.TryParse(value?.Replace(",", "", StringComparison.Ordinal), NumberStyles.Number, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static long? ParseLong(string? value)
    {
        var normalized = value?.Replace(",", "", StringComparison.Ordinal).Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return null;

        if (normalized[0] == '-')
        {
            normalized = normalized.TrimStart('-').Trim();
            if (normalized.Length == 0) return null;
            normalized = $"-{normalized}";
        }

        return long.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;
    }

    private static string? FirstNonEmpty(IReadOnlyDictionary<int, string> fields, params int[] references) =>
        references.Select(reference => NullableText(fields, reference)).FirstOrDefault(value => value is not null);

    private static string? ExtractTrailingNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var digits = new string(value.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        return digits.Length == 0 ? null : digits;
    }

    private static string NormalizeDocumentNumber(string value)
    {
        var trimmed = value.Trim();
        return long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number.ToString(CultureInfo.InvariantCulture)
            : trimmed;
    }

    private static int? ReadReference(byte[] bytes)
    {
        if (bytes.Length is < 1 or > 4) return null;
        var value = 0;
        foreach (var item in bytes) value = checked((value << 8) | item);
        return value;
    }

    private static string DecodeValue(byte[] bytes) => AbacusEncoding.GetString(bytes);

    private static Encoding CreateEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932);
    }
}
