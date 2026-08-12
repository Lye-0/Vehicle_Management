using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VehicleManagement.Companion.Services;

public sealed record AbacusLegacyGraphFinalCustomerGroup(
    string GroupKey,
    string Origin,
    bool Approved,
    IReadOnlyList<string> SourceCustomerIds,
    string CustomerId,
    string CustomerNumber,
    string CustomerName,
    string NameKana,
    string PhoneNumber,
    string EmailAddress,
    string PostalCode,
    string Address,
    string Memo);

public sealed record AbacusLegacyGraphFinalizationSnapshot(
    IReadOnlyList<AbacusLegacyGraphFinalCustomerGroup> CustomerGroups,
    IReadOnlyDictionary<string, string> ManualDocumentVehicleLinks,
    IReadOnlyDictionary<string, string> ManualDocumentCustomerGroupLinks,
    IReadOnlyCollection<string> ExcludedDocumentKeys,
    bool ImportConfirmed);

public sealed record AbacusLegacyGraphFinalCustomerPreview(
    string CustomerId,
    string CustomerName,
    int VehicleCount,
    int DocumentCount,
    string Origin,
    bool Approved);

public sealed record AbacusLegacyGraphFinalDocumentPreview(
    string DocumentId,
    string Kind,
    string DocumentNumber,
    string CustomerId,
    string CustomerName,
    string? VehicleId,
    string? VehicleName,
    bool Vehicleless,
    string SourceLocation,
    bool Excluded);

public sealed record AbacusLegacyGraphFinalPackageResult(
    string PackagePath,
    string ManifestPath,
    string ManifestSha256,
    string CustomersCsvPath,
    string VehiclesCsvPath,
    string SalesCsvPath,
    string MaintenanceCsvPath,
    string DocumentLinksPath,
    int CustomerRowCount,
    int VehicleRowCount,
    int SalesRowCount,
    int MaintenanceRowCount,
    int VehiclelessDocumentCount,
    int ExcludedDocumentCount,
    IReadOnlyList<AbacusLegacyGraphFinalCustomerPreview> Customers,
    IReadOnlyList<AbacusLegacyGraphFinalDocumentPreview> Documents);

/// <summary>
/// グラフ画面での最終確定状態を、Web登録前に人が検証できるパッケージへ保存します。
/// この段階ではWeb API、D1、Object Storage、ABACUS原本への書き込みは行いません。
/// </summary>
public sealed class AbacusLegacyGraphFinalPackageStore
{
    private const string ManifestFileName = "manifest.json";
    private const string CustomersFileName = "customers.csv";
    private const string VehiclesFileName = "vehicles.csv";
    private const string SalesFileName = "sales.csv";
    private const string MaintenanceFileName = "maintenance.csv";
    private const string DocumentLinksFileName = "document-links.json";
    private const long MaximumManifestBytes = 2L * 1024 * 1024;
    private const long MaximumDataFileBytes = 64L * 1024 * 1024;
    private const int MaximumDocumentCount = 20_000;
    private const int MaximumCustomerCount = 10_000;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
    private static readonly Encoding Utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public async Task<AbacusLegacyGraphFinalPackageResult> CreateAsync(
        AbacusLegacyExportCandidateGraphResult graph,
        AbacusLegacyGraphFinalizationSnapshot snapshot,
        string destinationParent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.ImportConfirmed)
        {
            throw new InvalidDataException("グラフのインポート内容が最終確定されていません。");
        }

        var sourceRoot = ValidateFolder(graph.PackagePath, "候補パッケージ");
        var destinationRoot = ValidateFolder(destinationParent, "登録前パッケージ保存先");
        ValidateDestination(sourceRoot, destinationRoot);
        if (graph.AllDocuments.Count > MaximumDocumentCount)
        {
            throw new InvalidDataException($"書類件数が上限{MaximumDocumentCount:N0}件を超えています。");
        }
        if (graph.Customers.Count > MaximumCustomerCount)
        {
            throw new InvalidDataException($"顧客件数が上限{MaximumCustomerCount:N0}件を超えています。");
        }

        var sourceCustomers = graph.Customers.ToDictionary(customer => customer.CustomerId, StringComparer.Ordinal);
        var groups = ValidateGroups(snapshot.CustomerGroups, sourceCustomers);
        var groupByKey = groups.ToDictionary(group => group.GroupKey, StringComparer.Ordinal);
        var groupBySourceCustomerId = groups
            .SelectMany(group => group.SourceCustomerIds.Select(customerId => (customerId, group)))
            .ToDictionary(item => item.customerId, item => item.group, StringComparer.Ordinal);
        var sourceVehicles = graph.Customers
            .SelectMany(customer => customer.Vehicles)
            .ToArray();
        var vehiclesById = sourceVehicles.ToDictionary(vehicle => vehicle.VehicleId, StringComparer.Ordinal);
        var documents = graph.AllDocuments
            .GroupBy(GetDocumentKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var documentByKey = documents.ToDictionary(GetDocumentKey, StringComparer.OrdinalIgnoreCase);
        var excludedKeys = snapshot.ExcludedDocumentKeys
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (excludedKeys.Any(key => !documentByKey.ContainsKey(key)))
        {
            throw new InvalidDataException("除外指定に存在しない書類が含まれています。グラフを再読込してください。");
        }

        ValidateManualLinks(snapshot, documentByKey, vehiclesById, groupByKey);
        var finalDocumentCandidates = new List<FinalDocument>(documents.Length - excludedKeys.Count);
        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = GetDocumentKey(document);
            if (excludedKeys.Contains(key))
            {
                continue;
            }

            var linkedVehicleId = ResolveVehicleId(document, key, snapshot, vehiclesById);
            var targetGroup = linkedVehicleId is not null
                ? groupBySourceCustomerId[vehiclesById[linkedVehicleId].CustomerId]
                : ResolveCustomerOnlyGroup(document, key, snapshot, sourceCustomers, groupBySourceCustomerId, groupByKey);
            var vehicle = linkedVehicleId is null ? null : vehiclesById[linkedVehicleId];
            finalDocumentCandidates.Add(new FinalDocument(
                document,
                key,
                CreateStableId(document.Kind == "販売書類" ? "abacus-sales" : "abacus-maintenance", key),
                targetGroup,
                vehicle));
        }

        // ABACUSでは同じ書類番号が別の行で再利用されることがあります。
        // Web側の書類番号は組織内で一意であるため、重複時だけ決定的な枝番を付けます。
        // 元の番号は備考へ残し、書類ID・出典による追跡可能性を維持します。
        var finalDocuments = AssignImportDocumentNumbers(finalDocumentCandidates);

        var customerRows = groups
            .Select(group => new FinalCustomer(group, sourceVehicles.Count(vehicle => groupBySourceCustomerId[vehicle.CustomerId].GroupKey == group.GroupKey), finalDocuments.Count(document => document.Group.GroupKey == group.GroupKey)))
            .OrderBy(customer => customer.Group.CustomerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(customer => customer.Group.CustomerId, StringComparer.Ordinal)
            .ToArray();
        var vehicleRows = sourceVehicles
            .Select(vehicle => new FinalVehicle(vehicle, groupBySourceCustomerId[vehicle.CustomerId]))
            .OrderBy(vehicle => vehicle.Group.CustomerId, StringComparer.Ordinal)
            .ThenBy(vehicle => vehicle.Vehicle.VehicleId, StringComparer.Ordinal)
            .ToArray();

        var packagePath = CreateUniquePackageDirectory(destinationRoot);
        try
        {
            var customersCsvPath = Path.Combine(packagePath, CustomersFileName);
            var vehiclesCsvPath = Path.Combine(packagePath, VehiclesFileName);
            var salesCsvPath = Path.Combine(packagePath, SalesFileName);
            var maintenanceCsvPath = Path.Combine(packagePath, MaintenanceFileName);
            var documentLinksPath = Path.Combine(packagePath, DocumentLinksFileName);
            await WriteAndVerifyAsync(customersCsvPath, BuildCustomersCsv(customerRows), cancellationToken);
            await WriteAndVerifyAsync(vehiclesCsvPath, BuildVehiclesCsv(vehicleRows), cancellationToken);
            await WriteAndVerifyAsync(salesCsvPath, BuildSalesCsv(finalDocuments.Where(document => document.Document.Kind == "販売書類")), cancellationToken);
            await WriteAndVerifyAsync(maintenanceCsvPath, BuildMaintenanceCsv(finalDocuments.Where(document => document.Document.Kind == "整備書類")), cancellationToken);
            var linksDocument = new DocumentLinksDocument(
                1,
                "abacus-export-import-document-links",
                "finalization-preview",
                finalDocuments.Select(document => new DocumentLink(
                    document.DocumentKey,
                    document.DocumentId,
                    document.Document.Kind,
                    document.ImportDocumentNumber,
                    document.Group.CustomerId,
                    document.Group.CustomerName,
                    document.Vehicle?.VehicleId,
                    document.Vehicle?.VehicleName,
                    document.Vehicle is null,
                    document.Document.SourceLocation,
                    document.Document.Warning)).ToArray(),
                excludedKeys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray());
            await WriteAndVerifyAsync(documentLinksPath, JsonSerializer.SerializeToUtf8Bytes(linksDocument, JsonOptions), cancellationToken);

            var sourceManifestPath = Path.Combine(sourceRoot, ManifestFileName);
            var sourceManifestBytes = await ReadRequiredFileAsync(sourceManifestPath, MaximumManifestBytes, cancellationToken);
            var sourceManifestSha256 = Convert.ToHexString(SHA256.HashData(sourceManifestBytes));
            IReadOnlyList<OutputFile> dataFiles = new OutputFile[]
            {
                await DescribeFileAsync(customersCsvPath, CustomersFileName, cancellationToken),
                await DescribeFileAsync(vehiclesCsvPath, VehiclesFileName, cancellationToken),
                await DescribeFileAsync(salesCsvPath, SalesFileName, cancellationToken),
                await DescribeFileAsync(maintenanceCsvPath, MaintenanceFileName, cancellationToken),
                await DescribeFileAsync(documentLinksPath, DocumentLinksFileName, cancellationToken),
            };
            var vehiclelessCount = finalDocuments.Count(document => document.Vehicle is null);
            var warnings = new List<string>
            {
                "このパッケージはグラフ確定後のregistration-previewです。Web API、D1、Object Storage、画像アップロードは行っていません。",
                "未確定トレイに残した書類は最終確定時の除外指定に従い、今回のCSVへ出力していません。",
                "顧客が一意に判定できる車両情報のない書類は顧客だけへ紐付き、vehicleIdを空欄としてdocument-links.jsonへ記録しています。次段階でWeb側の特例登録を実装します。",
            };
            if (vehiclelessCount > 0)
            {
                warnings.Add($"車両情報のない書類を{vehiclelessCount:N0}件出力しました。Web画面では車両を「なし」と表示する特例対象です。");
            }
            if (excludedKeys.Count > 0)
            {
                warnings.Add($"未確定トレイ等の除外指定{excludedKeys.Count:N0}件は出力していません。");
            }
            var renumberedDocumentCount = finalDocuments.Count(document => !string.Equals(document.ImportDocumentNumber, document.Document.DocumentNumber, StringComparison.Ordinal));
            if (renumberedDocumentCount > 0)
            {
                warnings.Add($"ABACUS内で書類番号が重複した{renumberedDocumentCount:N0}件に、登録用の枝番を付けています。元の番号は備考へ記録しています。");
            }

            var manifest = new OutputManifest(
                1,
                "abacus-export-import-final-package",
                "registration-preview",
                DateTime.UtcNow,
                new OutputSource(sourceRoot, sourceManifestSha256),
                new OutputSummary(
                    customerRows.Length,
                    vehicleRows.Length,
                    finalDocuments.Count(document => document.Document.Kind == "販売書類"),
                    finalDocuments.Count(document => document.Document.Kind == "整備書類"),
                    vehiclelessCount,
                    excludedKeys.Count),
                dataFiles,
                warnings,
                groups.Select(group => new ManifestGroup(
                    group.GroupKey,
                    group.Origin,
                    group.Approved,
                    group.SourceCustomerIds,
                    group.CustomerId,
                    group.CustomerName)).ToArray(),
                finalDocuments.Select(document => new ManifestDocument(
                    document.DocumentKey,
                    document.DocumentId,
                    document.Document.Kind,
                    document.Group.CustomerId,
                    document.Vehicle?.VehicleId,
                    document.Document.SourceLocation,
                    document.Vehicle is null)).ToArray(),
                excludedKeys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray());
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
            var manifestPath = Path.Combine(packagePath, ManifestFileName);
            await WriteAndVerifyManifestAsync(manifestPath, manifestBytes, manifest, cancellationToken);
            return new AbacusLegacyGraphFinalPackageResult(
                packagePath,
                manifestPath,
                Convert.ToHexString(SHA256.HashData(manifestBytes)),
                customersCsvPath,
                vehiclesCsvPath,
                salesCsvPath,
                maintenanceCsvPath,
                documentLinksPath,
                customerRows.Length,
                vehicleRows.Length,
                manifest.Summary.SalesRowCount,
                manifest.Summary.MaintenanceRowCount,
                vehiclelessCount,
                excludedKeys.Count,
                customerRows.Select(customer => new AbacusLegacyGraphFinalCustomerPreview(
                    customer.Group.CustomerId,
                    customer.Group.CustomerName,
                    customer.VehicleCount,
                    customer.DocumentCount,
                    customer.Group.Origin,
                    customer.Group.Approved)).ToArray(),
                finalDocuments.Select(document => new AbacusLegacyGraphFinalDocumentPreview(
                    document.DocumentId,
                    document.Document.Kind,
                    document.ImportDocumentNumber,
                    document.Group.CustomerId,
                    document.Group.CustomerName,
                    document.Vehicle?.VehicleId,
                    document.Vehicle?.VehicleName,
                    document.Vehicle is null,
                    document.Document.SourceLocation,
                    false)).ToArray());
        }
        catch
        {
            if (Directory.Exists(packagePath) && IsSameOrSubPath(packagePath, destinationRoot))
            {
                Directory.Delete(packagePath, recursive: true);
            }

            throw;
        }
    }

    private static IReadOnlyList<AbacusLegacyGraphFinalCustomerGroup> ValidateGroups(
        IReadOnlyList<AbacusLegacyGraphFinalCustomerGroup> groups,
        IReadOnlyDictionary<string, AbacusLegacyExportCandidateGraphCustomer> sourceCustomers)
    {
        if (groups.Count == 0)
        {
            throw new InvalidDataException("顧客統合グループがありません。");
        }

        var seenGroupKeys = new HashSet<string>(StringComparer.Ordinal);
        var seenCustomerIds = new HashSet<string>(StringComparer.Ordinal);
        var seenOutputCustomerIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            RequiredText(group.GroupKey, "顧客グループキー");
            if (!seenGroupKeys.Add(group.GroupKey) || group.SourceCustomerIds.Count == 0)
            {
                throw new InvalidDataException("顧客統合グループのキーが重複しているか、構成顧客がありません。");
            }

            foreach (var customerId in group.SourceCustomerIds)
            {
                if (!sourceCustomers.ContainsKey(customerId) || !seenCustomerIds.Add(customerId))
                {
                    throw new InvalidDataException($"顧客統合グループの顧客IDが不正または重複しています: {customerId}");
                }
            }

            if (group.SourceCustomerIds.Count > 1 && !group.Approved)
            {
                throw new InvalidDataException($"未承認の顧客統合候補が残っています: {group.GroupKey}");
            }
            RequiredText(group.CustomerId, "出力顧客ID");
            RequiredText(group.CustomerName, "出力顧客名");
            if (!seenOutputCustomerIds.Add(group.CustomerId))
            {
                throw new InvalidDataException($"統合後の出力顧客IDが重複しています: {group.CustomerId}");
            }
        }

        if (seenCustomerIds.Count != sourceCustomers.Count)
        {
            throw new InvalidDataException("顧客統合グループに含まれていない顧客があります。グラフを再読込してください。");
        }

        return groups;
    }

    private static void ValidateManualLinks(
        AbacusLegacyGraphFinalizationSnapshot snapshot,
        IReadOnlyDictionary<string, AbacusLegacyExportCandidateGraphDocument> documents,
        IReadOnlyDictionary<string, AbacusLegacyExportCandidateGraphVehicle> vehicles,
        IReadOnlyDictionary<string, AbacusLegacyGraphFinalCustomerGroup> groups)
    {
        foreach (var pair in snapshot.ManualDocumentVehicleLinks)
        {
            if (!documents.ContainsKey(pair.Key) || !vehicles.ContainsKey(pair.Value))
            {
                throw new InvalidDataException("手動書類リンクが現在のグラフを参照していません。");
            }
        }

        foreach (var pair in snapshot.ManualDocumentCustomerGroupLinks)
        {
            if (!documents.ContainsKey(pair.Key) || !groups.ContainsKey(pair.Value))
            {
                throw new InvalidDataException("顧客直結書類のリンク先が現在のグラフを参照していません。");
            }
        }
    }

    private static string? ResolveVehicleId(
        AbacusLegacyExportCandidateGraphDocument document,
        string documentKey,
        AbacusLegacyGraphFinalizationSnapshot snapshot,
        IReadOnlyDictionary<string, AbacusLegacyExportCandidateGraphVehicle> vehicles)
    {
        if (snapshot.ManualDocumentVehicleLinks.TryGetValue(documentKey, out var manualVehicleId))
        {
            return manualVehicleId;
        }

        if (document.LinkedVehicleId is not null && vehicles.ContainsKey(document.LinkedVehicleId))
        {
            return document.LinkedVehicleId;
        }

        return null;
    }

    private static AbacusLegacyGraphFinalCustomerGroup ResolveCustomerOnlyGroup(
        AbacusLegacyExportCandidateGraphDocument document,
        string documentKey,
        AbacusLegacyGraphFinalizationSnapshot snapshot,
        IReadOnlyDictionary<string, AbacusLegacyExportCandidateGraphCustomer> sourceCustomers,
        IReadOnlyDictionary<string, AbacusLegacyGraphFinalCustomerGroup> groupsBySourceId,
        IReadOnlyDictionary<string, AbacusLegacyGraphFinalCustomerGroup> groupsByKey)
    {
        if (snapshot.ManualDocumentCustomerGroupLinks.TryGetValue(documentKey, out var manualGroupKey) &&
            groupsByKey.TryGetValue(manualGroupKey, out var manualGroup))
        {
            return manualGroup;
        }

        var sourceCustomer = sourceCustomers.Values.FirstOrDefault(customer =>
            customer.Vehicles.Any(vehicle => vehicle.Documents.Any(item => string.Equals(GetDocumentKey(item), documentKey, StringComparison.OrdinalIgnoreCase))) ||
            customer.UnresolvedDocuments.Any(item => string.Equals(GetDocumentKey(item), documentKey, StringComparison.OrdinalIgnoreCase)));
        if (sourceCustomer is not null && groupsBySourceId.TryGetValue(sourceCustomer.CustomerId, out var sourceGroup))
        {
            return sourceGroup;
        }

        throw new InvalidDataException($"書類の顧客を解決できません: {document.SourceLocation}");
    }

    private static byte[] BuildCustomersCsv(IReadOnlyList<FinalCustomer> customers)
    {
        var builder = new StringBuilder();
        AppendCsvRow(builder, ["顧客ID", "顧客番号", "顧客名", "ふりがな", "電話番号", "メールアドレス", "郵便番号", "住所", "メモ", "車両台数"]);
        foreach (var customer in customers)
        {
            AppendCsvRow(builder, [
                customer.Group.CustomerId,
                customer.Group.CustomerNumber,
                customer.Group.CustomerName,
                customer.Group.NameKana,
                customer.Group.PhoneNumber,
                customer.Group.EmailAddress,
                customer.Group.PostalCode,
                customer.Group.Address,
                Truncate($"ABACUSグラフ確定; グループ={customer.Group.GroupKey}; 起源={customer.Group.Origin}; " + (customer.Group.Approved ? "キャンバス承認済み" : "単体顧客")),
                customer.VehicleCount.ToString(CultureInfo.InvariantCulture),
            ]);
        }

        return Utf8WithBom.GetBytes(builder.ToString());
    }

    private static byte[] BuildVehiclesCsv(IReadOnlyList<FinalVehicle> vehicles)
    {
        var builder = new StringBuilder();
        AppendCsvRow(builder, ["車両ID", "顧客ID", "顧客名", "メーカー", "車名", "型式", "登録番号", "車台番号", "年式", "車検満了日", "走行距離", "車体色", "排気量", "ミッション", "記録簿", "備考"]);
        foreach (var vehicle in vehicles)
        {
            AppendCsvRow(builder, [
                vehicle.Vehicle.VehicleId,
                vehicle.Group.CustomerId,
                vehicle.Group.CustomerName,
                vehicle.Vehicle.Maker,
                vehicle.Vehicle.VehicleName,
                "",
                vehicle.Vehicle.RegistrationNumber,
                vehicle.Vehicle.ChassisNumber,
                vehicle.Vehicle.ModelYear,
                vehicle.Vehicle.InspectionDate,
                vehicle.Vehicle.Mileage,
                "",
                "",
                "",
                "",
                Truncate($"ABACUSグラフ確定; 元顧客ID={vehicle.Vehicle.CustomerId}; 車両ID={vehicle.Vehicle.VehicleId}"),
            ]);
        }

        return Utf8WithBom.GetBytes(builder.ToString());
    }

    private static byte[] BuildSalesCsv(IEnumerable<FinalDocument> documents)
    {
        var builder = new StringBuilder();
        AppendCsvRow(builder, ["書類ID", "書類番号", "書類種別", "ステータス", "顧客名", "車名", "登録番号", "発行日", "支払期限", "税率", "小計", "消費税", "合計", "明細", "備考", "明細詳細"]);
        foreach (var document in documents)
        {
            AppendCsvRow(builder, BuildDocumentFields(document, isMaintenance: false));
        }

        return Utf8WithBom.GetBytes(builder.ToString());
    }

    private static byte[] BuildMaintenanceCsv(IEnumerable<FinalDocument> documents)
    {
        var builder = new StringBuilder();
        AppendCsvRow(builder, ["書類ID", "書類番号", "書類種別", "入庫区分", "ステータス", "顧客名", "車名", "登録番号", "入庫日", "出庫予定日", "支払期限", "税率", "小計", "消費税", "合計", "明細", "備考", "明細詳細"]);
        foreach (var document in documents)
        {
            AppendCsvRow(builder, BuildDocumentFields(document, isMaintenance: true));
        }

        return Utf8WithBom.GetBytes(builder.ToString());
    }

    private static IReadOnlyList<string> BuildDocumentFields(FinalDocument finalDocument, bool isMaintenance)
    {
        var document = finalDocument.Document;
        var vehicleName = finalDocument.Vehicle?.VehicleName ?? "";
        var registrationNumber = finalDocument.Vehicle?.RegistrationNumber ?? "";
        var total = document.TotalAmount;
        var originalNumberMemo = string.Equals(finalDocument.ImportDocumentNumber, document.DocumentNumber, StringComparison.Ordinal)
            ? ""
            : $" 原書類番号={document.DocumentNumber};";
        var memo = Truncate(
            $"ABACUSグラフ確定; 出典={document.SourceLocation}; 元顧客名={document.CustomerName};{originalNumberMemo} " +
            (finalDocument.Vehicle is null ? "車両情報なし（顧客直結の特例）" : $"車両ID={finalDocument.Vehicle.VehicleId}"));
        return isMaintenance
            ? [
                finalDocument.DocumentId,
                finalDocument.ImportDocumentNumber,
                "整備請求書",
                "一般整備",
                "下書き",
                finalDocument.Group.CustomerName,
                vehicleName,
                registrationNumber,
                document.DocumentDate,
                document.DocumentDate,
                "",
                "10",
                total,
                "0",
                total,
                "",
                memo,
                "",
            ]
            : [
                finalDocument.DocumentId,
                finalDocument.ImportDocumentNumber,
                "請求書",
                "下書き",
                finalDocument.Group.CustomerName,
                vehicleName,
                registrationNumber,
                document.DocumentDate,
                "",
                "10",
                total,
                "0",
                total,
                "",
                memo,
                "",
            ];
    }

    private static string GetDocumentKey(AbacusLegacyExportCandidateGraphDocument document) =>
        string.Join("|", document.Kind, document.SourceFileName, document.SourceRowNumber, document.DocumentNumber);

    private static IReadOnlyList<FinalDocument> AssignImportDocumentNumbers(IReadOnlyList<FinalDocument> documents)
    {
        var usedByKind = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var result = new List<FinalDocument>(documents.Count);
        foreach (var document in documents)
        {
            var kind = document.Document.Kind;
            if (!usedByKind.TryGetValue(kind, out var used))
            {
                used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                usedByKind.Add(kind, used);
            }

            var originalNumber = RequiredText(document.Document.DocumentNumber, "書類番号");
            var importNumber = originalNumber;
            var suffix = 2;
            while (!used.Add(importNumber))
            {
                importNumber = $"{originalNumber}-{suffix++}";
            }

            result.Add(document with { ImportDocumentNumber = importNumber });
        }

        return result;
    }

    private static string CreateStableId(string prefix, string value) =>
        $"{prefix}-{Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(value))).ToLowerInvariant()[..24]}";

    private static string RequiredText(string? value, string label)
    {
        var text = value?.Trim() ?? "";
        if (text.Length == 0 || text.Length > 64 * 1024 || text.Any(char.IsControl))
        {
            throw new InvalidDataException($"{label}が空欄または不正です。");
        }

        return text;
    }

    private static string Truncate(string value) => value.Length <= 500 ? value : value[..500];

    private static void AppendCsvRow(StringBuilder builder, IReadOnlyList<string> values)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (index > 0) builder.Append(',');
            var value = values[index] ?? "";
            if (value.Contains('"') || value.Contains(',') || value.Contains('\r') || value.Contains('\n'))
            {
                builder.Append('"').Append(value.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
            }
            else
            {
                builder.Append(value);
            }
        }

        builder.Append("\r\n");
    }

    private static string ValidateFolder(string path, string label)
    {
        var value = RequiredText(path, label);
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"{label}が見つかりません: {fullPath}");
        }

        var info = new DirectoryInfo(fullPath);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException($"{label}がリンクです: {fullPath}");
        }

        return fullPath;
    }

    private static void ValidateDestination(string sourceRoot, string destinationRoot)
    {
        if (IsSameOrSubPath(destinationRoot, sourceRoot))
        {
            throw new InvalidDataException("登録前パッケージ保存先は候補パッケージの外側を選択してください。");
        }
    }

    private static string CreateUniquePackageDirectory(string destinationRoot)
    {
        for (var index = 0; index < 100; index++)
        {
            var suffix = index == 0 ? "" : $"-{index + 1:D2}";
            var path = Path.Combine(destinationRoot, $"ABACUS-Graph-Import-Registration-Preview-{DateTime.Now:yyyyMMdd-HHmmss}{suffix}");
            if (Directory.Exists(path) || File.Exists(path)) continue;
            Directory.CreateDirectory(path);
            return path;
        }

        throw new IOException("グラフ確定パッケージを一意に作成できませんでした。");
    }

    private static bool IsSameOrSubPath(string candidate, string root)
    {
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
            normalizedCandidate.StartsWith($"{normalizedRoot}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<byte[]> ReadRequiredFileAsync(string path, long maximumBytes, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length <= 0 || info.Length > maximumBytes)
        {
            throw new InvalidDataException($"必須マニフェストを読み取れません: {path}");
        }

        return await File.ReadAllBytesAsync(path, cancellationToken);
    }

    private static async Task WriteAndVerifyAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        if (bytes.LongLength > MaximumDataFileBytes)
        {
            throw new InvalidDataException($"出力ファイルが上限{MaximumDataFileBytes:N0} bytesを超えています: {Path.GetFileName(path)}");
        }

        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        var verified = await File.ReadAllBytesAsync(path, cancellationToken);
        if (!verified.AsSpan().SequenceEqual(bytes))
        {
            throw new IOException($"作成したファイルの再読込検証に失敗しました: {Path.GetFileName(path)}");
        }
    }

    private static async Task<OutputFile> DescribeFileAsync(string path, string relativePath, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length <= 0 || info.Length > MaximumDataFileBytes)
        {
            throw new InvalidDataException($"出力ファイルを検証できません: {relativePath}");
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        return new OutputFile(relativePath, info.Length, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static async Task WriteAndVerifyManifestAsync(string path, byte[] bytes, OutputManifest expected, CancellationToken cancellationToken)
    {
        if (bytes.LongLength > MaximumManifestBytes)
        {
            throw new InvalidDataException("出力マニフェストが大きすぎます。");
        }

        await WriteAndVerifyAsync(path, bytes, cancellationToken);
        var actual = JsonSerializer.Deserialize<OutputManifest>(await File.ReadAllBytesAsync(path, cancellationToken), JsonOptions);
        if (actual is null || actual.Version != expected.Version || actual.Kind != expected.Kind || actual.Status != expected.Status || actual.DataFiles.Count != expected.DataFiles.Count)
        {
            throw new InvalidDataException("作成したグラフ確定マニフェストの再読込検証に失敗しました。");
        }
    }

    private sealed record FinalCustomer(AbacusLegacyGraphFinalCustomerGroup Group, int VehicleCount, int DocumentCount);

    private sealed record FinalVehicle(
        AbacusLegacyExportCandidateGraphVehicle Vehicle,
        AbacusLegacyGraphFinalCustomerGroup Group);

    private sealed record FinalDocument(
        AbacusLegacyExportCandidateGraphDocument Document,
        string DocumentKey,
        string DocumentId,
        AbacusLegacyGraphFinalCustomerGroup Group,
        AbacusLegacyExportCandidateGraphVehicle? Vehicle)
    {
        public string ImportDocumentNumber { get; init; } = Document.DocumentNumber;
    }

    private sealed record DocumentLinksDocument(
        int Version,
        string Kind,
        string Status,
        IReadOnlyList<DocumentLink> Documents,
        IReadOnlyList<string> ExcludedDocumentKeys);

    private sealed record DocumentLink(
        string DocumentKey,
        string DocumentId,
        string DocumentKind,
        string DocumentNumber,
        string CustomerId,
        string CustomerName,
        string? VehicleId,
        string? VehicleName,
        bool Vehicleless,
        string SourceLocation,
        string Warning);

    private sealed record OutputManifest(
        int Version,
        string Kind,
        string Status,
        DateTime CreatedAtUtc,
        OutputSource Source,
        OutputSummary Summary,
        IReadOnlyList<OutputFile> DataFiles,
        IReadOnlyList<string> Warnings,
        IReadOnlyList<ManifestGroup> Groups,
        IReadOnlyList<ManifestDocument> Documents,
        IReadOnlyList<string> ExcludedDocumentKeys);

    private sealed record OutputSource(string CandidatePackagePath, string CandidateManifestSha256);

    private sealed record OutputSummary(
        int CustomerRowCount,
        int VehicleRowCount,
        int SalesRowCount,
        int MaintenanceRowCount,
        int VehiclelessDocumentCount,
        int ExcludedDocumentCount);

    private sealed record OutputFile(string FileName, long SizeBytes, string Sha256);

    private sealed record ManifestGroup(
        string GroupKey,
        string Origin,
        bool Approved,
        IReadOnlyList<string> SourceCustomerIds,
        string CustomerId,
        string CustomerName);

    private sealed record ManifestDocument(
        string DocumentKey,
        string DocumentId,
        string Kind,
        string CustomerId,
        string? VehicleId,
        string SourceLocation,
        bool Vehicleless);
}
