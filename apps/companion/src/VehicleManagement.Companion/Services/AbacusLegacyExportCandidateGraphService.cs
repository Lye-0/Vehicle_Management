using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;

namespace VehicleManagement.Companion.Services;

public sealed record AbacusLegacyExportCandidateGraphDocument(
    string Kind,
    string SourceFileName,
    int SourceRowNumber,
    string DocumentNumber,
    string CustomerName,
    string VehicleName,
    string RegistrationNumber,
    string DocumentDate,
    string TotalAmount,
    string MatchStatus,
    string Warning,
    string? LinkedVehicleId,
    IReadOnlyList<string> CandidateVehicleIds,
    IReadOnlyList<string> CandidateCustomerIds)
{
    public bool IsLinked => !string.IsNullOrWhiteSpace(LinkedVehicleId);

    public string SourceLocation =>
        string.IsNullOrWhiteSpace(SourceFileName)
            ? "出典不明"
            : $"{SourceFileName} #{SourceRowNumber}";

    public string CandidateSummary => CandidateVehicleIds.Count switch
    {
        0 => "なし",
        1 => "1台",
        _ => $"{CandidateVehicleIds.Count:N0}台",
    };
}

public sealed record AbacusLegacyExportCandidateGraphVehicle(
    string VehicleId,
    string CustomerId,
    string CustomerName,
    string Maker,
    string VehicleName,
    string ModelYear,
    string InspectionDate,
    string Mileage,
    string RegistrationNumber,
    string ChassisNumber,
    IReadOnlyList<AbacusLegacyExportCandidateGraphDocument> Documents)
{
    public string DisplayName =>
        string.IsNullOrWhiteSpace(VehicleName) ? "車名未設定" : VehicleName;

    public string IdentifierSummary =>
        string.Join(" / ", new[] { RegistrationNumber, ChassisNumber }.Where(value => !string.IsNullOrWhiteSpace(value)));
}

public sealed record AbacusLegacyExportCandidateGraphCustomer(
    string CustomerId,
    string CustomerNumber,
    string CustomerName,
    string NameKana,
    string PhoneNumber,
    string EmailAddress,
    string PostalCode,
    string Address,
    string Memo,
    IReadOnlyList<AbacusLegacyExportCandidateGraphVehicle> Vehicles,
    IReadOnlyList<AbacusLegacyExportCandidateGraphDocument> UnresolvedDocuments)
{
    public string DisplayName =>
        string.IsNullOrWhiteSpace(CustomerName) ? "顧客名未設定" : CustomerName;

    public string VehicleSummary => $"車両 {Vehicles.Count:N0}台 / 書類 {Documents:N0}件";

    public int Documents => Vehicles.Sum(vehicle => vehicle.Documents.Count) + UnresolvedDocuments.Count;
}

public sealed record AbacusLegacyExportCandidateGraphResult(
    string PackagePath,
    IReadOnlyList<AbacusLegacyExportCandidateGraphCustomer> Customers,
    IReadOnlyList<AbacusLegacyExportCandidateGraphDocument> AllDocuments,
    IReadOnlyList<AbacusLegacyExportCandidateGraphDocument> UnresolvedDocuments,
    IReadOnlyList<AbacusLegacyExportPreviewRow> UnresolvedVehicleRows,
    int SolidLinkCount,
    int ReviewLinkCount,
    int UnmatchedDocumentCount,
    IReadOnlyList<string> Warnings);

/// <summary>
/// 再検証済みの登録前候補パッケージを、顧客・車両・書類の確認用グラフへ変換します。
/// 顧客名だけでは統合せず、出力CSVのIDとマニフェストの判定を優先します。
/// </summary>
public sealed class AbacusLegacyExportCandidateGraphService
{
    private const string CustomersFileName = "customers.csv";
    private const string VehiclesFileName = "vehicles.csv";
    private const string SalesFileName = "sales.csv";
    private const string MaintenanceFileName = "maintenance.csv";
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly Regex SourceMemoPattern = new(
        @"ABACUS=(?<file>[^;#]+)#(?<row>\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly string[] CustomerHeaders =
        ["顧客ID", "顧客番号", "顧客名", "ふりがな", "電話番号", "メールアドレス", "郵便番号", "住所", "メモ", "車両台数"];
    private static readonly string[] VehicleHeaders =
        ["車両ID", "顧客ID", "顧客名", "メーカー", "車名", "型式", "登録番号", "車台番号", "年式", "車検満了日", "走行距離", "車体色", "排気量", "ミッション", "記録簿", "備考"];
    private static readonly string[] SalesHeaders =
        ["書類ID", "書類番号", "書類種別", "ステータス", "顧客名", "車名", "登録番号", "発行日", "支払期限", "税率", "小計", "消費税", "合計", "明細", "備考", "明細詳細"];
    private static readonly string[] MaintenanceHeaders =
        ["書類ID", "書類番号", "書類種別", "入庫区分", "ステータス", "顧客名", "車名", "登録番号", "入庫日", "出庫予定日", "支払期限", "税率", "小計", "消費税", "合計", "明細", "備考", "明細詳細"];

    public Task<AbacusLegacyExportCandidateGraphResult> BuildAsync(
        AbacusLegacyExportPreviewPackageResult package,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Build(package, cancellationToken), cancellationToken);

    private static AbacusLegacyExportCandidateGraphResult Build(
        AbacusLegacyExportPreviewPackageResult package,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        var packageRoot = Path.GetFullPath(package.PackagePath);
        var customerRows = ReadRows(packageRoot, CustomersFileName, CustomerHeaders, cancellationToken);
        var vehicleRows = ReadRows(packageRoot, VehiclesFileName, VehicleHeaders, cancellationToken);
        var salesRows = ReadRows(packageRoot, SalesFileName, SalesHeaders, cancellationToken);
        var maintenanceRows = ReadRows(packageRoot, MaintenanceFileName, MaintenanceHeaders, cancellationToken);

        var customers = new Dictionary<string, CustomerBuilder>(StringComparer.Ordinal);
        foreach (var row in customerRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var customerId = Required(row.Fields, 0, "顧客ID");
            if (customers.ContainsKey(customerId))
            {
                throw new InvalidDataException($"顧客CSVに重複した顧客IDがあります: {customerId}");
            }

            customers.Add(customerId, new CustomerBuilder(
                customerId,
                Value(row.Fields, 1),
                Value(row.Fields, 2),
                Value(row.Fields, 3),
                Value(row.Fields, 4),
                Value(row.Fields, 5),
                Value(row.Fields, 6),
                Value(row.Fields, 7),
                Value(row.Fields, 8),
                new List<VehicleBuilder>(),
                new List<AbacusLegacyExportCandidateGraphDocument>()));
        }

        var vehicles = new Dictionary<string, VehicleBuilder>(StringComparer.Ordinal);
        foreach (var row in vehicleRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var vehicleId = Required(row.Fields, 0, "車両ID");
            var customerId = Required(row.Fields, 1, "車両の顧客ID");
            if (vehicles.ContainsKey(vehicleId))
            {
                throw new InvalidDataException($"車両CSVに重複した車両IDがあります: {vehicleId}");
            }

            if (!customers.TryGetValue(customerId, out var customer))
            {
                throw new InvalidDataException($"車両CSVの顧客IDが顧客CSVにありません: {customerId}");
            }

            var vehicle = new VehicleBuilder(
                vehicleId,
                customerId,
                customer.DisplayName,
                Value(row.Fields, 3),
                Value(row.Fields, 4),
                Value(row.Fields, 8),
                Value(row.Fields, 9),
                Value(row.Fields, 10),
                Value(row.Fields, 6),
                Value(row.Fields, 7),
                new List<AbacusLegacyExportCandidateGraphDocument>());
            vehicles.Add(vehicleId, vehicle);
            customer.Vehicles.Add(vehicle);
        }

        var manifestRows = package.Rows
            .Where(row => row.Kind is "販売書類" or "整備書類")
            .ToDictionary(
                row => BuildSourceKey(row.Kind, row.SourceFileName, row.SourceRowNumber),
                row => row,
                StringComparer.OrdinalIgnoreCase);
        var documents = new List<AbacusLegacyExportCandidateGraphDocument>();
        var representedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in salesRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            documents.Add(BuildDocument(
                "販売書類",
                row,
                4,
                5,
                6,
                7,
                12,
                Value(row.Fields, 14),
                vehicles,
                customers,
                manifestRows,
                representedSources));
        }

        foreach (var row in maintenanceRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            documents.Add(BuildDocument(
                "整備書類",
                row,
                5,
                6,
                7,
                8,
                14,
                Value(row.Fields, 16),
                vehicles,
                customers,
                manifestRows,
                representedSources));
        }

        // 出力CSVに含まれなかった「除外」行も、顧客名が分かるものは未確定欄で確認できるようにします。
        foreach (var manifestRow in package.Rows.Where(row =>
                     (row.Kind is "販売書類" or "整備書類") &&
                     !string.IsNullOrWhiteSpace(row.CustomerName)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceKey = BuildSourceKey(manifestRow.Kind, manifestRow.SourceFileName, manifestRow.SourceRowNumber);
            if (representedSources.Contains(sourceKey))
            {
                continue;
            }

            var candidateCustomerIds = FindCustomers(customers, manifestRow.CustomerName);
            var document = new AbacusLegacyExportCandidateGraphDocument(
                manifestRow.Kind,
                manifestRow.SourceFileName,
                manifestRow.SourceRowNumber,
                manifestRow.DocumentNumber,
                manifestRow.CustomerName,
                manifestRow.VehicleName,
                "",
                "",
                "",
                manifestRow.MatchStatus,
                manifestRow.Warning,
                null,
                [],
                candidateCustomerIds);
            documents.Add(document);
            if (candidateCustomerIds.Count == 1 && customers.TryGetValue(candidateCustomerIds[0], out var customer))
            {
                customer.UnresolvedDocuments.Add(document);
            }
        }

        var unresolved = documents.Where(document => !document.IsLinked).ToArray();
        var solidLinkCount = documents.Count(document => document.IsLinked && document.MatchStatus == "一意一致");
        var reviewLinkCount = documents.Count(document => !document.IsLinked && document.MatchStatus is "要確認" or "候補");
        var unmatchedCount = documents.Count(document => !document.IsLinked && document.MatchStatus is "未一致" or "除外");
        var customerResults = customers.Values
            .OrderBy(customer => customer.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(customer => new AbacusLegacyExportCandidateGraphCustomer(
                customer.CustomerId,
                customer.CustomerNumber,
                customer.DisplayName,
                customer.NameKana,
                customer.PhoneNumber,
                customer.EmailAddress,
                customer.PostalCode,
                customer.Address,
                customer.Memo,
                customer.Vehicles
                    .OrderBy(vehicle => vehicle.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .Select(vehicle => new AbacusLegacyExportCandidateGraphVehicle(
                        vehicle.VehicleId,
                        vehicle.CustomerId,
                        vehicle.CustomerName,
                        vehicle.Maker,
                        vehicle.VehicleName,
                        vehicle.ModelYear,
                        vehicle.InspectionDate,
                        vehicle.Mileage,
                        vehicle.RegistrationNumber,
                        vehicle.ChassisNumber,
                        vehicle.Documents.ToArray()))
                    .ToArray(),
                customer.UnresolvedDocuments.ToArray()))
            .ToArray();

        var warnings = new List<string>
        {
            "このグラフは再検証済み候補CSVの読み取り専用プレビューです。顧客・車両・書類の登録やAPI送信は行いません。",
            "実線はマニフェストが一意一致と判定した書類、点線は要確認・未一致・除外の書類です。",
        };
        if (package.SkippedBlankCustomerRows > 0)
        {
            warnings.Add($"顧客名空欄で{package.SkippedBlankCustomerRows:N0}行を無視しています。これはエラーではありません。");
        }

        return new AbacusLegacyExportCandidateGraphResult(
            packageRoot,
            customerResults,
            documents,
            unresolved,
            package.Rows
                .Where(row => row.Kind == "車両一覧" && row.MatchStatus != "候補")
                .ToArray(),
            solidLinkCount,
            reviewLinkCount,
            unmatchedCount,
            warnings);
    }

    private static AbacusLegacyExportCandidateGraphDocument BuildDocument(
        string kind,
        CsvRow row,
        int customerNameIndex,
        int vehicleNameIndex,
        int registrationIndex,
        int documentDateIndex,
        int totalAmountIndex,
        string memo,
        IReadOnlyDictionary<string, VehicleBuilder> vehicles,
        IReadOnlyDictionary<string, CustomerBuilder> customers,
        IReadOnlyDictionary<string, AbacusLegacyExportPreviewRow> manifestRows,
        ISet<string> representedSources)
    {
        var customerName = Value(row.Fields, customerNameIndex);
        var vehicleName = Value(row.Fields, vehicleNameIndex);
        var registrationNumber = Value(row.Fields, registrationIndex);
        var source = SourceMemoPattern.Match(memo);
        var sourceFileName = source.Success ? source.Groups["file"].Value : "";
        var sourceRowNumber = source.Success && int.TryParse(source.Groups["row"].Value, out var parsedRow) ? parsedRow : row.RowNumber;
        var sourceKey = BuildSourceKey(kind, sourceFileName, sourceRowNumber);
        if (source.Success)
        {
            representedSources.Add(sourceKey);
        }

        manifestRows.TryGetValue(sourceKey, out var manifestRow);
        var candidateCustomerIds = FindCustomers(customers, customerName);
        var candidateVehicles = FindVehicles(vehicles.Values, candidateCustomerIds, vehicleName, registrationNumber);
        var matchStatus = manifestRow?.MatchStatus ?? (candidateVehicles.Count == 1 ? "要確認" : "未一致");
        var warning = manifestRow?.Warning ?? "候補マニフェストに対応する行がないため、手動確認が必要です。";
        string? linkedVehicleId = null;
        if (matchStatus == "一意一致" && candidateVehicles.Count == 1)
        {
            linkedVehicleId = candidateVehicles[0].VehicleId;
            candidateCustomerIds = [candidateVehicles[0].CustomerId];
        }
        else if (matchStatus == "一意一致")
        {
            matchStatus = candidateVehicles.Count == 0 ? "未一致" : "要確認";
            warning = "マニフェストは一意一致ですが、再検証した車両CSVから1台に再現できません。手動確認が必要です。";
        }

        var document = new AbacusLegacyExportCandidateGraphDocument(
            kind,
            sourceFileName,
            sourceRowNumber,
            Value(row.Fields, 1),
            customerName,
            vehicleName,
            registrationNumber,
            Value(row.Fields, documentDateIndex),
            Value(row.Fields, totalAmountIndex),
            matchStatus,
            warning,
            linkedVehicleId,
            candidateVehicles.Select(vehicle => vehicle.VehicleId).ToArray(),
            candidateCustomerIds);
        if (linkedVehicleId is not null && vehicles.TryGetValue(linkedVehicleId, out var linkedVehicle))
        {
            linkedVehicle.Documents.Add(document);
        }
        else if (candidateCustomerIds.Count == 1 && customers.TryGetValue(candidateCustomerIds[0], out var customer))
        {
            customer.UnresolvedDocuments.Add(document);
        }

        return document;
    }

    private static IReadOnlyList<VehicleBuilder> FindVehicles(
        IEnumerable<VehicleBuilder> source,
        IReadOnlyList<string> candidateCustomerIds,
        string vehicleName,
        string registrationNumber)
    {
        var customerSet = candidateCustomerIds.ToHashSet(StringComparer.Ordinal);
        var candidates = source
            .Where(vehicle => customerSet.Count == 0 || customerSet.Contains(vehicle.CustomerId))
            .ToArray();
        var normalizedRegistration = Normalize(registrationNumber);
        if (!string.IsNullOrWhiteSpace(normalizedRegistration))
        {
            candidates = candidates
                .Where(vehicle => Normalize(vehicle.RegistrationNumber) == normalizedRegistration)
                .ToArray();
        }

        var normalizedVehicleName = Normalize(vehicleName);
        if (!string.IsNullOrWhiteSpace(normalizedVehicleName))
        {
            var named = candidates
                .Where(vehicle => Normalize(vehicle.VehicleName) == normalizedVehicleName)
                .ToArray();
            if (named.Length > 0)
            {
                candidates = named;
            }
        }

        return candidates;
    }

    private static IReadOnlyList<string> FindCustomers(
        IReadOnlyDictionary<string, CustomerBuilder> customers,
        string customerName)
    {
        var normalizedName = Normalize(customerName);
        return string.IsNullOrWhiteSpace(normalizedName)
            ? []
            : customers.Values
                .Where(customer => Normalize(customer.DisplayName) == normalizedName)
                .Select(customer => customer.CustomerId)
                .ToArray();
    }

    private static IReadOnlyList<CsvRow> ReadRows(
        string packageRoot,
        string fileName,
        IReadOnlyList<string> expectedHeaders,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(packageRoot, fileName);
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"グラフ表示用のCSVがありません: {fileName}");
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(File.ReadAllBytes(path));
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException($"{fileName}をUTF-8として読み取れません。", exception);
        }

        using var parser = new TextFieldParser(new StringReader(text))
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false,
        };
        parser.SetDelimiters(",");
        if (parser.EndOfData)
        {
            throw new InvalidDataException($"{fileName}が空です。");
        }

        var header = parser.ReadFields() ?? [];
        if (header.Length > 0)
        {
            header[0] = header[0].TrimStart('\uFEFF');
        }
        if (!header.SequenceEqual(expectedHeaders, StringComparer.Ordinal))
        {
            throw new InvalidDataException($"{fileName}の見出しが既定形式と一致しません。");
        }

        var rows = new List<CsvRow>();
        var rowNumber = 0;
        while (!parser.EndOfData)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rowNumber++;
            string[] fields;
            try
            {
                fields = parser.ReadFields() ?? [];
            }
            catch (MalformedLineException exception)
            {
                throw new InvalidDataException($"{fileName}の{rowNumber}行目を読み取れません。", exception);
            }

            if (fields.Length != expectedHeaders.Count)
            {
                throw new InvalidDataException($"{fileName}の{rowNumber}行目の列数が不正です。");
            }

            rows.Add(new CsvRow(rowNumber, fields));
        }

        return rows;
    }

    private static string BuildSourceKey(string kind, string fileName, int rowNumber) =>
        $"{kind}|{fileName}|{rowNumber}";

    private static string Required(IReadOnlyList<string> fields, int index, string label)
    {
        var value = Value(fields, index);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"グラフ表示に必要な{label}が空欄です。")
            : value;
    }

    private static string Value(IReadOnlyList<string> fields, int index) =>
        index >= 0 && index < fields.Count ? fields[index].Trim() : "";

    private static string Normalize(string value) =>
        value.Normalize(NormalizationForm.FormKC)
            .Replace(" ", "", StringComparison.Ordinal)
            .Replace("　", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .Replace("ー", "", StringComparison.Ordinal)
            .ToUpperInvariant();

    private sealed record CsvRow(int RowNumber, string[] Fields);

    private sealed class CustomerBuilder(
        string customerId,
        string customerNumber,
        string displayName,
        string nameKana,
        string phoneNumber,
        string emailAddress,
        string postalCode,
        string address,
        string memo,
        List<VehicleBuilder> vehicles,
        List<AbacusLegacyExportCandidateGraphDocument> unresolvedDocuments)
    {
        public string CustomerId { get; } = customerId;
        public string CustomerNumber { get; } = customerNumber;
        public string DisplayName { get; } = displayName;
        public string NameKana { get; } = nameKana;
        public string PhoneNumber { get; } = phoneNumber;
        public string EmailAddress { get; } = emailAddress;
        public string PostalCode { get; } = postalCode;
        public string Address { get; } = address;
        public string Memo { get; } = memo;
        public List<VehicleBuilder> Vehicles { get; } = vehicles;
        public List<AbacusLegacyExportCandidateGraphDocument> UnresolvedDocuments { get; } = unresolvedDocuments;
    }

    private sealed class VehicleBuilder(
        string vehicleId,
        string customerId,
        string customerName,
        string maker,
        string vehicleName,
        string modelYear,
        string inspectionDate,
        string mileage,
        string registrationNumber,
        string chassisNumber,
        List<AbacusLegacyExportCandidateGraphDocument> documents)
    {
        public string VehicleId { get; } = vehicleId;
        public string CustomerId { get; } = customerId;
        public string CustomerName { get; } = customerName;
        public string Maker { get; } = maker;
        public string VehicleName { get; } = vehicleName;
        public string DisplayName => string.IsNullOrWhiteSpace(VehicleName) ? "車名未設定" : VehicleName;
        public string ModelYear { get; } = modelYear;
        public string InspectionDate { get; } = inspectionDate;
        public string Mileage { get; } = mileage;
        public string RegistrationNumber { get; } = registrationNumber;
        public string ChassisNumber { get; } = chassisNumber;
        public List<AbacusLegacyExportCandidateGraphDocument> Documents { get; } = documents;
    }
}
