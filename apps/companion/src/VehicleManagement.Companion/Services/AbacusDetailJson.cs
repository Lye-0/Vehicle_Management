using System.Text.Json;
using System.Text;
using VehicleManagement.AbacusImport;

namespace VehicleManagement.Companion.Services;

public sealed record AbacusDetailJsonDocument(
    int Version,
    string Kind,
    string SourceFile,
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
    long DetailAmount,
    int ExcludedDetailCount,
    int AmountOnlyRowCount,
    string MatchStatus,
    string Warning,
    IReadOnlyList<AbacusDetailFinancialLine>? FinancialLines = null,
    long? AbacusTax = null,
    long? AbacusTaxRate = null);

public sealed record AbacusDetailMatch(
    AbacusUcsDetailDocument? Document,
    string Status,
    string Warning)
{
    public bool IsMapped => Document is not null && string.Equals(Status, "matched", StringComparison.Ordinal);
}

public sealed class AbacusDetailMapper
{
    private readonly IReadOnlyList<AbacusUcsDetailDocument> documents;

    public AbacusDetailMapper(IEnumerable<AbacusUcsDetailDocument> documents) => this.documents = documents.ToArray();

    public AbacusDetailMatch Match(string kind, string documentNumber, string customerName, string vehicleName, string registrationNumber, string chassisNumber)
    {
        var candidates = documents
            .Where(document => string.Equals(document.Kind, kind, StringComparison.Ordinal))
            .Where(document => Normalize(document.DocumentNumber) == Normalize(documentNumber))
            .Where(document => string.IsNullOrWhiteSpace(customerName) || Normalize(document.CustomerName) == Normalize(customerName))
            .Where(document => VehicleCompatible(document, vehicleName, registrationNumber, chassisNumber))
            .ToArray();
        if (candidates.Length == 1) return new AbacusDetailMatch(candidates[0], "matched", "書類番号・顧客・車両識別子がUCSの1レコードに一致しました。");
        if (candidates.Length > 1) return new AbacusDetailMatch(null, "review", $"同一条件に{candidates.Length:N0}件のUCSレコードが一致しました。明細を自動登録せず要確認にしています。");
        return new AbacusDetailMatch(null, "unmatched", "書類番号・顧客・車両識別子に一致するUCS明細レコードがありません。");
    }

    public static string Serialize(AbacusDetailMatch match)
    {
        var document = match.Document;
        var payload = new AbacusDetailJsonDocument(
            1,
            "abacus-detail-lines",
            document?.SourceFileName ?? "",
            document?.RecordIdHex ?? "",
            document?.DocumentNumber ?? "",
            document?.CustomerName ?? "",
            document?.VehicleName ?? "",
            document?.RegistrationNumber ?? "",
            document?.ChassisNumber ?? "",
            document?.Lines ?? [],
            document?.PartsSubtotal,
            document?.TechnicalSubtotal,
            document?.AbacusSubtotal,
            document?.AbacusTotal,
            document?.DetailAmount ?? 0,
            document?.ExcludedDetailCount ?? 0,
            document?.Lines.Count(line => line.IsAmountOnly) ?? 0,
            match.Status,
            match.Warning,
            document?.FinancialLines ?? [],
            document?.AbacusTax,
            document?.AbacusTaxRate);
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false });
    }

    private static bool IdentifierCompatible(string csv, string ucs) =>
        string.IsNullOrWhiteSpace(csv) || string.IsNullOrWhiteSpace(ucs) || NormalizeIdentifier(csv) == NormalizeIdentifier(ucs);

    private static bool VehicleCompatible(
        AbacusUcsDetailDocument document,
        string vehicleName,
        string registrationNumber,
        string chassisNumber)
    {
        var normalizedRegistration = NormalizeIdentifier(registrationNumber);
        var normalizedChassis = NormalizeIdentifier(chassisNumber);
        var hasCsvIdentifier = normalizedRegistration.Length > 0 || normalizedChassis.Length > 0;
        if (hasCsvIdentifier)
        {
            var registrationMatches = normalizedRegistration.Length > 0 &&
                NormalizeIdentifier(document.RegistrationNumber) == normalizedRegistration;
            var chassisMatches = normalizedChassis.Length > 0 &&
                NormalizeIdentifier(document.ChassisNumber) == normalizedChassis;
            return (registrationMatches || chassisMatches) &&
                IdentifierCompatible(registrationNumber, document.RegistrationNumber) &&
                IdentifierCompatible(chassisNumber, document.ChassisNumber);
        }

        return string.IsNullOrWhiteSpace(vehicleName) || Normalize(document.VehicleName) == Normalize(vehicleName);
    }

    private static string Normalize(string value) =>
        value.Normalize(NormalizationForm.FormKC).Trim().Replace(" ", "", StringComparison.Ordinal).Replace("　", "", StringComparison.Ordinal).ToUpperInvariant();

    private static string NormalizeIdentifier(string value) =>
        Normalize(value).Replace("-", "", StringComparison.Ordinal);
}
