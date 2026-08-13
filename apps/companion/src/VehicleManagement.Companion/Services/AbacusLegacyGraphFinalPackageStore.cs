using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VehicleManagement.AbacusImport;

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
    IReadOnlyList<AbacusLegacyGraphFinalDocumentPreview> Documents,
    string? ImageAttachmentsPath = null,
    int ImageCount = 0);

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
    private const string ImageAttachmentsFileName = "image-attachments.json";
    private const long MaximumManifestBytes = 2L * 1024 * 1024;
    private const long MaximumDataFileBytes = 64L * 1024 * 1024;
    private const int MaximumDocumentCount = 20_000;
    private const int MaximumCustomerCount = 10_000;
    private const int MaximumImageCount = 10_000;
    private const long MaximumImageBytes = 256L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
    private static readonly Encoding Utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public Task<AbacusLegacyGraphFinalPackageResult> CreateAsync(
        AbacusLegacyExportCandidateGraphResult graph,
        AbacusLegacyGraphFinalizationSnapshot snapshot,
        string destinationParent,
        string? imageRegistrationPreviewFolder = null,
        CancellationToken cancellationToken = default) =>
        CreateAsyncCore(graph, snapshot, destinationParent, imageRegistrationPreviewFolder, null, cancellationToken);

    public Task<AbacusLegacyGraphFinalPackageResult> CreateAsync(
        AbacusLegacyExportCandidateGraphResult graph,
        AbacusLegacyGraphFinalizationSnapshot snapshot,
        string destinationParent,
        AbacusFp5VehicleImageMappingResult fp5VehicleImageMapping,
        CancellationToken cancellationToken = default) =>
        CreateAsyncCore(graph, snapshot, destinationParent, null, fp5VehicleImageMapping, cancellationToken);

    private async Task<AbacusLegacyGraphFinalPackageResult> CreateAsyncCore(
        AbacusLegacyExportCandidateGraphResult graph,
        AbacusLegacyGraphFinalizationSnapshot snapshot,
        string destinationParent,
        string? imageRegistrationPreviewFolder,
        AbacusFp5VehicleImageMappingResult? fp5VehicleImageMapping,
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
        var imageAttachments = fp5VehicleImageMapping is not null
            ? await ReadFp5ImageAttachmentsAsync(fp5VehicleImageMapping, vehicleRows, cancellationToken)
            : string.IsNullOrWhiteSpace(imageRegistrationPreviewFolder)
                ? Array.Empty<PreparedImageAttachment>()
                : await ReadImageAttachmentsAsync(imageRegistrationPreviewFolder, vehicleRows, cancellationToken);

        var packagePath = CreateUniquePackageDirectory(destinationRoot);
        try
        {
            var customersCsvPath = Path.Combine(packagePath, CustomersFileName);
            var vehiclesCsvPath = Path.Combine(packagePath, VehiclesFileName);
            var salesCsvPath = Path.Combine(packagePath, SalesFileName);
            var maintenanceCsvPath = Path.Combine(packagePath, MaintenanceFileName);
            var documentLinksPath = Path.Combine(packagePath, DocumentLinksFileName);
            string? imageAttachmentsPath = null;
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

            IReadOnlyList<OutputFile> imageFiles = Array.Empty<OutputFile>();
            if (imageAttachments.Count > 0)
            {
                foreach (var image in imageAttachments)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var destinationPath = Path.Combine(packagePath, image.ImagePath.Replace('/', Path.DirectorySeparatorChar));
                    await CopyAndVerifyImageAsync(image.SourcePath, destinationPath, image.ImageSha256, cancellationToken);
                }

                imageAttachmentsPath = Path.Combine(packagePath, ImageAttachmentsFileName);
                var imageLinks = new ImageAttachmentsDocument(
                    1,
                    "abacus-web-import-image-attachments",
                    "manual-upload-required",
                    imageAttachments.Select(image => new ImageAttachment(
                        image.CustomerId,
                        image.VehicleId,
                        image.ImagePath,
                        image.ImageSha256,
                        image.ContentType)).ToArray());
                await WriteAndVerifyAsync(imageAttachmentsPath, JsonSerializer.SerializeToUtf8Bytes(imageLinks, JsonOptions), cancellationToken);
                imageFiles = await Task.WhenAll(imageAttachments.Select(image =>
                    DescribeImageFileAsync(
                        Path.Combine(packagePath, image.ImagePath.Replace('/', Path.DirectorySeparatorChar)),
                        image.ImagePath,
                        cancellationToken)));
            }

            var sourceManifestPath = Path.Combine(sourceRoot, ManifestFileName);
            var sourceManifestBytes = await ReadRequiredFileAsync(sourceManifestPath, MaximumManifestBytes, cancellationToken);
            var sourceManifestSha256 = Convert.ToHexString(SHA256.HashData(sourceManifestBytes));
            var dataFileList = new List<OutputFile>
            {
                await DescribeFileAsync(customersCsvPath, CustomersFileName, cancellationToken),
                await DescribeFileAsync(vehiclesCsvPath, VehiclesFileName, cancellationToken),
                await DescribeFileAsync(salesCsvPath, SalesFileName, cancellationToken),
                await DescribeFileAsync(maintenanceCsvPath, MaintenanceFileName, cancellationToken),
                await DescribeFileAsync(documentLinksPath, DocumentLinksFileName, cancellationToken),
            };
            if (imageAttachmentsPath is not null)
            {
                dataFileList.Add(await DescribeFileAsync(imageAttachmentsPath, ImageAttachmentsFileName, cancellationToken));
            }
            IReadOnlyList<OutputFile> dataFiles = dataFileList;
            var vehiclelessCount = finalDocuments.Count(document => document.Vehicle is null);
            var detailDocuments = finalDocuments
                .Select(document => ParseDetailJson(document.Document.DetailsJson))
                .Where(document => document is not null)
                .Cast<AbacusDetailJsonDocument>()
                .ToArray();
            var detailMappedCount = detailDocuments.Count(document => string.Equals(document.MatchStatus, "matched", StringComparison.Ordinal));
            var detailReviewCount = detailDocuments.Count(document => string.Equals(document.MatchStatus, "review", StringComparison.Ordinal));
            var detailUnsupportedCount = detailDocuments.Count(document => string.Equals(document.MatchStatus, "unmatched", StringComparison.Ordinal));
            var detailExcludedRowCount = detailDocuments.Sum(document => document.ExcludedDetailCount);
            var amountOnlyDetailRowCount = detailDocuments.Sum(document => document.AmountOnlyRowCount);
            var warnings = new List<string>
            {
                "このパッケージはグラフ確定後のregistration-previewです。Web API、D1、Object Storage、画像アップロードは行っていません。",
                "未確定トレイに残した書類は最終確定時の除外指定に従い、今回のCSVへ出力していません。",
                "顧客が一意に判定できる車両情報のない書類は顧客だけへ紐付き、vehicleIdを空欄としてdocument-links.jsonへ記録しています。次段階でWeb側の特例登録を実装します。",
            };
            if (imageAttachments.Count > 0)
            {
                warnings.Add($"確認済み画像を{imageAttachments.Count:N0}件、車両識別子を再照合して同じ登録前パッケージへ含めました。Web登録後に画像アップロード対象になります。");
            }
            else if (fp5VehicleImageMapping is not null)
            {
                warnings.Add("Gate 14のFP5車両対応付けを検証しました。画像なし車両のみで、同梱対象の画像はありません。画像対応付けレポートを保存しています。");
            }
            else
            {
                warnings.Add("画像登録前パッケージは指定されていません。画像が必要な場合は画像準備で作成してから、もう一度このパッケージを作成してください。");
            }
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
            var amountDefaultedDocumentCount = finalDocuments.Count(document => string.IsNullOrWhiteSpace(document.Document.TotalAmount));
            if (amountDefaultedDocumentCount > 0)
            {
                warnings.Add($"ABACUSで金額が未設定の{amountDefaultedDocumentCount:N0}件は、小計・合計を0として登録用CSVへ出力しています。元データに金額がないことは備考へ記録しています。");
            }
            warnings.Add($"Gate 19明細: 対応付け済み{detailMappedCount:N0}件 / 要確認{detailReviewCount:N0}件 / 未対応{detailUnsupportedCount:N0}件 / 除外行{detailExcludedRowCount:N0}件 / 金額のみ行{amountOnlyDetailRowCount:N0}件。");

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
                    excludedKeys.Count,
                    imageAttachments.Count,
                    detailMappedCount,
                    detailReviewCount,
                    detailUnsupportedCount,
                    detailExcludedRowCount,
                    amountOnlyDetailRowCount),
                dataFiles,
                imageFiles,
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
                    false)).ToArray(),
                imageAttachmentsPath,
                imageAttachments.Count);
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
        var detail = ParseDetailJson(document.DetailsJson);
        var vehicleName = finalDocument.Vehicle?.VehicleName ?? "";
        var registrationNumber = finalDocument.Vehicle?.RegistrationNumber ?? "";
        var amountWasMissing = string.IsNullOrWhiteSpace(document.TotalAmount);
        var sourceTotal = amountWasMissing ? 0L : ParseAmount(document.TotalAmount);
        var totalValue = detail?.AbacusTotal ?? (sourceTotal == 0 ? null : sourceTotal);
        var total = (totalValue ?? 0).ToString(CultureInfo.InvariantCulture);
        var taxRateValue = detail?.AbacusTaxRate ?? InferLegacyTaxRate(document.DocumentDate);
        var taxValue = detail?.AbacusTax;
        var subtotalValue = detail?.AbacusSubtotal;
        if (totalValue is not null && taxValue is null)
        {
            if (detail is not null && detail.DetailAmount == totalValue.Value)
            {
                taxValue = CalculateIncludedTax(totalValue.Value, taxRateValue);
                subtotalValue = totalValue.Value - taxValue.Value;
            }
            else if (subtotalValue is not null)
            {
                taxValue = Math.Max(0, totalValue.Value - subtotalValue.Value);
            }
        }
        var subtotal = (subtotalValue ?? totalValue ?? 0).ToString(CultureInfo.InvariantCulture);
        var tax = (taxValue ?? 0).ToString(CultureInfo.InvariantCulture);
        var taxRate = taxRateValue.ToString(CultureInfo.InvariantCulture);
        var originalNumberMemo = string.Equals(finalDocument.ImportDocumentNumber, document.DocumentNumber, StringComparison.Ordinal)
            ? ""
            : $" 原書類番号={document.DocumentNumber};";
        var missingAmountMemo = amountWasMissing ? " 金額未設定（小計・合計を0として登録）;" : "";
        var memo = Truncate(
            $"ABACUSグラフ確定; 出典={document.SourceLocation}; 元顧客名={document.CustomerName};{originalNumberMemo}{missingAmountMemo} " +
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
                taxRate,
                subtotal,
                tax,
                total,
                "",
                memo,
                document.DetailsJson,
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
                taxRate,
                subtotal,
                tax,
                total,
                "",
                memo,
                document.DetailsJson,
            ];
    }

    private static long ParseAmount(string value) =>
        long.TryParse(value.Trim().Replace(",", "", StringComparison.Ordinal), NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount)
            ? amount
            : 0;

    private static long CalculateIncludedTax(long total, long taxRate) =>
        taxRate <= 0 ? 0 : (long)Math.Floor(total * (double)taxRate / (100d + taxRate));

    private static long InferLegacyTaxRate(string date)
    {
        if (!DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed)) return 10;
        return parsed >= new DateTime(2019, 10, 1) ? 10 : parsed >= new DateTime(2014, 4, 1) ? 8 : 5;
    }

    private static string GetDocumentKey(AbacusLegacyExportCandidateGraphDocument document) =>
        string.Join("|", document.Kind, document.SourceFileName, document.SourceRowNumber, document.DocumentNumber);

    private static AbacusDetailJsonDocument? ParseDetailJson(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            return JsonSerializer.Deserialize<AbacusDetailJsonDocument>(value, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

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

    private static async Task<IReadOnlyList<PreparedImageAttachment>> ReadFp5ImageAttachmentsAsync(
        AbacusFp5VehicleImageMappingResult mapping,
        IReadOnlyList<FinalVehicle> vehicles,
        CancellationToken cancellationToken)
    {
        if (!mapping.IsValid || !mapping.IsFullyMatched)
        {
            throw new InvalidDataException("Gate 14のFP5車両画像対応付けが検証済みではありません。");
        }

        var imageRoot = ValidateFolder(mapping.OutputFolderPath, "Gate 14画像復元フォルダー");
        var result = new List<PreparedImageAttachment>(mapping.MatchedImageCount);
        var seenImagePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in mapping.Mappings.Where(item => string.Equals(item.Status, "matched", StringComparison.Ordinal)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(candidate.ImageRelativePath) ||
                string.IsNullOrWhiteSpace(candidate.ImageSha256) ||
                string.IsNullOrWhiteSpace(candidate.ChassisNumber) ||
                string.IsNullOrWhiteSpace(candidate.RegistrationNumber))
            {
                throw new InvalidDataException($"Gate 14画像対応付けの必須項目がありません: {candidate.RecordIdHex}");
            }

            var imagePath = NormalizeImagePath(candidate.ImageRelativePath);
            if (!seenImagePaths.Add(imagePath))
            {
                throw new InvalidDataException($"Gate 14画像パスが重複しています: {imagePath}");
            }

            var sourcePath = Path.Combine(imageRoot, imagePath.Replace('/', Path.DirectorySeparatorChar));
            if (!IsSameOrSubPath(sourcePath, imageRoot))
            {
                throw new InvalidDataException($"Gate 14画像パスが復元フォルダー外を指しています: {imagePath}");
            }

            var imageInfo = ValidateRegularFile(sourcePath, "Gate 14復元画像", MaximumImageBytes);
            var imageSha256 = await CalculateSha256Async(sourcePath, cancellationToken);
            if (!string.Equals(imageSha256, candidate.ImageSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Gate 14復元画像のSHA-256が一致しません: {imagePath}");
            }

            var matches = vehicles
                .Where(vehicle =>
                    string.Equals(
                        NormalizeVehicleIdentifier(candidate.ChassisNumber),
                        NormalizeVehicleIdentifier(vehicle.Vehicle.ChassisNumber),
                        StringComparison.Ordinal) &&
                    string.Equals(
                        NormalizeVehicleIdentifier(candidate.RegistrationNumber),
                        NormalizeVehicleIdentifier(vehicle.Vehicle.RegistrationNumber),
                        StringComparison.Ordinal))
                .ToArray();
            if (matches.Length != 1)
            {
                throw new InvalidDataException(
                    $"Gate 14画像をグラフの車両へ一意に再照合できません: {imagePath}（候補 {matches.Length:N0}件）");
            }

            result.Add(new PreparedImageAttachment(
                sourcePath,
                imagePath,
                imageSha256,
                GetImageContentType(imagePath),
                matches[0].Group.CustomerId,
                matches[0].Vehicle.VehicleId));
        }

        if (result.Count != mapping.MatchedImageCount)
        {
            throw new InvalidDataException("Gate 14画像対応付けの件数と同梱対象画像の件数が一致しません。");
        }

        return result;
    }

    private static async Task<IReadOnlyList<PreparedImageAttachment>> ReadImageAttachmentsAsync(
        string imageRegistrationPreviewFolder,
        IReadOnlyList<FinalVehicle> vehicles,
        CancellationToken cancellationToken)
    {
        var imageRoot = ValidateFolder(imageRegistrationPreviewFolder, "画像登録前パッケージ");
        var manifestPath = Path.Combine(imageRoot, ManifestFileName);
        var imageDirectory = Path.Combine(imageRoot, "images");
        var imageDirectoryInfo = new DirectoryInfo(imageDirectory);
        if (!imageDirectoryInfo.Exists || imageDirectoryInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("画像登録前パッケージのimagesフォルダーを安全に読み取れません。");
        }
        var manifestBytes = await ReadRequiredFileAsync(manifestPath, MaximumManifestBytes, cancellationToken);
        var manifest = JsonSerializer.Deserialize<ImagePreviewManifest>(manifestBytes, JsonOptions);
        if (manifest is null ||
            manifest.Version != 1 ||
            !string.Equals(manifest.Kind, "abacus-image-registration-preview", StringComparison.Ordinal) ||
            !string.Equals(manifest.Status, "preview-only", StringComparison.Ordinal) ||
            manifest.Candidates is null ||
            manifest.Candidates.Count == 0 ||
            manifest.Candidates.Count > MaximumImageCount)
        {
            throw new InvalidDataException("画像登録前パッケージのマニフェスト形式または件数が不正です。");
        }

        var result = new List<PreparedImageAttachment>(manifest.Candidates.Count);
        var seenCandidateIds = new HashSet<string>(StringComparer.Ordinal);
        var seenImagePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalBytes = 0L;
        foreach (var candidate in manifest.Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidateId = RequiredText(candidate.CandidateId, "画像候補ID");
            if (!seenCandidateIds.Add(candidateId))
            {
                throw new InvalidDataException($"画像候補IDが重複しています: {candidateId}");
            }

            var imagePath = NormalizeImagePath(candidate.PackageImageFileName);
            if (!seenImagePaths.Add(imagePath))
            {
                throw new InvalidDataException($"画像パスが重複しています: {imagePath}");
            }

            var sourcePath = Path.Combine(imageRoot, imagePath.Replace('/', Path.DirectorySeparatorChar));
            if (!IsSameOrSubPath(sourcePath, imageRoot))
            {
                throw new InvalidDataException($"画像登録前パッケージの画像パスがパッケージ外を指しています: {imagePath}");
            }
            var imageInfo = ValidateRegularFile(sourcePath, "画像登録前パッケージの画像", MaximumImageBytes);
            totalBytes = checked(totalBytes + imageInfo.Length);
            if (totalBytes > 1L * 1024 * 1024 * 1024)
            {
                throw new InvalidDataException("画像登録前パッケージの合計サイズが上限を超えています。");
            }

            var imageSha256 = await CalculateSha256Async(sourcePath, cancellationToken);
            var expectedSha256 = RequiredText(candidate.ImageSha256, "画像SHA-256");
            if (!string.Equals(imageSha256, expectedSha256, StringComparison.OrdinalIgnoreCase) ||
                expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit))
            {
                throw new InvalidDataException($"画像登録前パッケージの画像SHA-256が一致しません: {imagePath}");
            }

            var matches = vehicles
                .Where(vehicle => IsImageVehicleIdentifierMatch(candidate, vehicle.Vehicle))
                .ToArray();
            if (matches.Length != 1)
            {
                var contextualMatches = matches
                    .Where(vehicle =>
                        string.Equals(candidate.CustomerName?.Trim() ?? "", vehicle.Vehicle.CustomerName.Trim(), StringComparison.Ordinal) &&
                        string.Equals(candidate.VehicleName?.Trim() ?? "", vehicle.Vehicle.VehicleName.Trim(), StringComparison.Ordinal))
                    .ToArray();
                matches = contextualMatches.Length == 1 ? contextualMatches : matches;
            }

            if (matches.Length != 1)
            {
                throw new InvalidDataException(
                    $"画像候補をグラフの車両へ一意に再照合できません: {imagePath}（候補 {matches.Length:N0}件）");
            }

            result.Add(new PreparedImageAttachment(
                sourcePath,
                imagePath,
                imageSha256,
                GetImageContentType(imagePath),
                matches[0].Group.CustomerId,
                matches[0].Vehicle.VehicleId));
        }

        return result;
    }

    private static bool IsImageVehicleIdentifierMatch(
        ImagePreviewCandidate candidate,
        AbacusLegacyExportCandidateGraphVehicle vehicle)
    {
        var chassis = NormalizeVehicleIdentifier(candidate.ChassisNumber);
        var registration = NormalizeVehicleIdentifier(candidate.RegistrationNumber);
        if (chassis.Length == 0 && registration.Length == 0)
        {
            return false;
        }

        return (chassis.Length == 0 || string.Equals(chassis, NormalizeVehicleIdentifier(vehicle.ChassisNumber), StringComparison.Ordinal)) &&
               (registration.Length == 0 || string.Equals(registration, NormalizeVehicleIdentifier(vehicle.RegistrationNumber), StringComparison.Ordinal));
    }

    private static string NormalizeVehicleIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        // ABACUS/FP5側と候補CSV側で、登録番号の区切りが半角空白・全角空白
        // として異なることがあります。候補グラフと同じく、識別子として意味を
        // 持たない空白・ハイフンをUnicode互換正規化後に除去して比較します。
        return string.Concat(
                value.Normalize(NormalizationForm.FormKC)
                    .Where(character => !char.IsWhiteSpace(character) && character != '-'))
            .ToUpperInvariant();
    }

    private static string NormalizeImagePath(string value)
    {
        var path = (value ?? "").Trim().Replace('\\', '/');
        if (!path.StartsWith("images/", StringComparison.Ordinal) ||
            path.Contains("..", StringComparison.Ordinal) ||
            path.Contains(':', StringComparison.Ordinal) ||
            path.Split('/').Any(part => part.Length == 0) ||
            path.Split('/').Length != 2 ||
            path.Any(char.IsControl))
        {
            throw new InvalidDataException($"画像パスがパッケージ内の安全な相対パスではありません: {value}");
        }

        var extension = Path.GetExtension(path);
        if (!string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"対応していない画像形式です: {path}");
        }

        return path;
    }

    private static string GetImageContentType(string imagePath) =>
        Path.GetExtension(imagePath).Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(imagePath).Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            ? "image/jpeg"
            : "image/png";

    private static FileInfo ValidateRegularFile(string path, string label, long maximumBytes)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length <= 0 || info.Length > maximumBytes)
        {
            throw new InvalidDataException($"{label}を読み取れません: {path}");
        }

        return info;
    }

    private static async Task CopyAndVerifyImageAsync(
        string sourcePath,
        string destinationPath,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidDataException("画像コピー先を確認できません。");
        Directory.CreateDirectory(destinationDirectory);
        var temporaryPath = destinationPath + ".partial";
        try
        {
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, 1024 * 1024, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }

            var copiedSha256 = await CalculateSha256Async(temporaryPath, cancellationToken);
            if (!string.Equals(copiedSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"画像コピー後のSHA-256が一致しません: {Path.GetFileName(sourcePath)}");
            }

            File.Move(temporaryPath, destinationPath, overwrite: false);
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            throw;
        }
    }

    private static async Task<string> CalculateSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

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

    private static async Task<OutputFile> DescribeImageFileAsync(string path, string relativePath, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length <= 0 || info.Length > MaximumImageBytes)
        {
            throw new InvalidDataException($"出力画像を検証できません: {relativePath}");
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new OutputFile(relativePath, info.Length, Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)));
    }

    private static async Task WriteAndVerifyManifestAsync(string path, byte[] bytes, OutputManifest expected, CancellationToken cancellationToken)
    {
        if (bytes.LongLength > MaximumManifestBytes)
        {
            throw new InvalidDataException("出力マニフェストが大きすぎます。");
        }

        await WriteAndVerifyAsync(path, bytes, cancellationToken);
        var actual = JsonSerializer.Deserialize<OutputManifest>(await File.ReadAllBytesAsync(path, cancellationToken), JsonOptions);
        if (actual is null || actual.Version != expected.Version || actual.Kind != expected.Kind || actual.Status != expected.Status || actual.DataFiles.Count != expected.DataFiles.Count || actual.ImageFiles.Count != expected.ImageFiles.Count || actual.Summary.ImageCount != expected.Summary.ImageCount)
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

    private sealed record ImageAttachmentsDocument(
        int Version,
        string Kind,
        string Status,
        IReadOnlyList<ImageAttachment> Attachments);

    private sealed record ImageAttachment(
        string CustomerId,
        string VehicleId,
        string ImagePath,
        string ImageSha256,
        string ContentType);

    private sealed record OutputManifest(
        int Version,
        string Kind,
        string Status,
        DateTime CreatedAtUtc,
        OutputSource Source,
        OutputSummary Summary,
        IReadOnlyList<OutputFile> DataFiles,
        IReadOnlyList<OutputFile> ImageFiles,
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
        int ExcludedDocumentCount,
        int ImageCount,
        int DetailMappedDocumentCount = 0,
        int DetailReviewDocumentCount = 0,
        int DetailUnsupportedDocumentCount = 0,
        int DetailExcludedRowCount = 0,
        int AmountOnlyDetailRowCount = 0);

    private sealed record OutputFile(string FileName, long SizeBytes, string Sha256);

    private sealed record PreparedImageAttachment(
        string SourcePath,
        string ImagePath,
        string ImageSha256,
        string ContentType,
        string CustomerId,
        string VehicleId);

    private sealed record ImagePreviewManifest(
        int Version,
        string Kind,
        string Status,
        IReadOnlyList<ImagePreviewCandidate> Candidates);

    private sealed record ImagePreviewCandidate(
        string CandidateId,
        string PackageImageFileName,
        string ImageSha256,
        string CustomerName,
        string VehicleName,
        string ChassisNumber,
        string RegistrationNumber);

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
