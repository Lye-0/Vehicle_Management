using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VehicleManagement.AbacusImport;

namespace VehicleManagement.Companion.Services;

public sealed record AbacusLegacyExportPreviewRow(
    string Kind,
    string SourceFileName,
    int SourceRowNumber,
    string CustomerName,
    string VehicleName,
    string DocumentNumber,
    string MatchStatus,
    string Warning);

public sealed record AbacusLegacyExportPreviewResult(
    string PackagePath,
    string ManifestPath,
    string ManifestSha256,
    IReadOnlyList<string> DataFilePaths,
    int CustomerRowCount,
    int VehicleRowCount,
    int SalesRowCount,
    int MaintenanceRowCount,
    int SkippedBlankCustomerRows,
    int SkippedMaintenanceWithoutVehicleRows,
    int AmbiguousVehicleRows,
    IReadOnlyList<AbacusLegacyExportPreviewRow> PreviewRows,
    int DetailMappedDocumentCount = 0,
    int DetailReviewDocumentCount = 0,
    int DetailUnsupportedDocumentCount = 0,
    int DetailExcludedRowCount = 0,
    int AmountOnlyDetailRowCount = 0);

/// <summary>
/// ABACUSの販売・整備・車両一覧CSVを、既存Web CSV形式の登録前候補へ変換します。
/// DB、Web API、Object Storage、ABACUS原本には接続せず、読み取りと新規パッケージ作成だけを行います。
/// </summary>
public sealed class AbacusLegacyExportPreviewStore
{
    private const string ManifestFileName = "manifest.json";
    private const string CustomersFileName = "customers.csv";
    private const string VehiclesFileName = "vehicles.csv";
    private const string SalesFileName = "sales.csv";
    private const string MaintenanceFileName = "maintenance.csv";
    private const int MaximumPreviewRows = 5_000;
    private const int MaximumOutputTextCharacters = 500;
    private const long MaximumOutputBytes = 64L * 1024 * 1024;
    private static readonly Encoding Utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
    private static readonly Regex EraDatePattern = new(
        "^(?<era>明治|大正|昭和|平成|令和|[MTSHRmts])\\s*(?<year>\\d{1,2})[./年月-](?<month>\\d{1,2})[./月-](?<day>\\d{1,2})日?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex EraYearPattern = new(
        "^(?<era>明治|大正|昭和|平成|令和|[MTSHRmts])\\s*(?<year>\\d{1,2})(?:年|/\\d{1,2})?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly AbacusLegacyExportReader reader;

    public AbacusLegacyExportPreviewStore(AbacusLegacyExportReader? reader = null)
    {
        this.reader = reader ?? new AbacusLegacyExportReader();
    }

    public async Task<AbacusLegacyExportPreviewResult> CreateAsync(
        string sourceFolder,
        string destinationParent,
        CancellationToken cancellationToken = default)
    {
        var sourceRoot = ValidateFolder(sourceFolder, "ABACUSエクスポートフォルダー");
        var destinationRoot = ValidateFolder(destinationParent, "登録前候補の保存先");
        ValidateDestination(sourceRoot, destinationRoot);

        var input = await reader.ReadAsync(sourceRoot, cancellationToken);
        if (!input.IsValid)
        {
            throw new InvalidDataException("固定列CSVの診断に合格していないため、登録前候補を作成できません。先に診断結果を確認してください。");
        }

        var detailRoot = FindDetailRoot(sourceRoot);
        var detailRead = detailRoot is null
            ? new AbacusUcsDetailReadResult(sourceRoot, [], 2, ["abx-cs-hb.ucs / abx-cs-sb.ucs が見つからないため、UCS明細を抽出していません。"])
            : await new AbacusFp5DetailReader().ReadFolderAsync(detailRoot, cancellationToken);
        var detailMapper = new AbacusDetailMapper(detailRead.Documents);
        var detailMappedDocumentCount = 0;
        var detailReviewDocumentCount = 0;
        var detailUnsupportedDocumentCount = 0;
        var detailExcludedRowCount = 0;
        var amountOnlyDetailRowCount = 0;

        var vehicleFiles = input.Files.Where(file => file.Kind == "車両一覧").ToList();
        if (vehicleFiles.Count != 1)
        {
            throw new InvalidDataException(
                vehicleFiles.Count == 0
                    ? "車両一覧CSVがないため、書類を安全に車両へ紐付けできません。"
                    : "車両一覧CSVが複数あります。重複を自動結合せず、全件を含む1ファイルだけを選んでから再実行してください。");
        }

        var sourceFiles = input.Files
            .Select(file => new SourceFile(
                file.FileName,
                file.Kind,
                file.Sha256 ?? throw new InvalidDataException($"{file.FileName}のSHA-256を確認できません。"),
                file.TotalRows,
                file.ValidRows))
            .ToArray();
        var sourceManifestHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join("|", sourceFiles.Select(file => $"{file.FileName}:{file.Sha256}")))));

        var customers = new Dictionary<string, CustomerCandidate>(StringComparer.OrdinalIgnoreCase);
        var vehicles = new List<VehicleCandidate>();
        var vehicleByChassis = new Dictionary<string, List<VehicleCandidate>>(StringComparer.OrdinalIgnoreCase);
        var vehicleByRegistration = new Dictionary<string, List<VehicleCandidate>>(StringComparer.OrdinalIgnoreCase);
        var previewRows = new List<AbacusLegacyExportPreviewRow>();
        var skippedBlankCustomerRows = 0;
        var skippedMaintenanceWithoutVehicleRows = 0;
        var ambiguousVehicleRows = 0;

        var vehicleFile = vehicleFiles[0];
        foreach (var row in vehicleFile.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = row.Fields;
            var customerName = Text(source, 0);
            var vehicleName = Text(source, 12);
            CustomerCandidate? customer = null;
            if (string.IsNullOrEmpty(customerName))
            {
                // 顧客情報がない車両も候補から捨てず、Gate22の未確定車両へ渡します。
                // 顧客IDは空欄のまま出力し、最終確定時にユーザーが顧客へ接続しなければ除外します。
                skippedBlankCustomerRows++;
            }
            else
            {
                customer = EnsureCustomer(
                    customers,
                    BuildCustomerSource(
                        customerName,
                        Text(source, 1),
                        "",
                        Text(source, 3),
                        row.FileName,
                        row.RowNumber));
            }
            var candidate = new VehicleCandidate(
                CreateStableId("abacus-vehicle", $"{row.FileName}:{row.RowNumber}"),
                customer?.Key ?? "",
                Text(source, 11),
                vehicleName,
                Text(source, 17),
                Text(source, 18),
                Text(source, 19),
                NormalizeModelYear(Text(source, 13)),
                NormalizeCalendarDate(Text(source, 21)),
                NormalizeNonNegativeInteger(Text(source, 20)),
                Text(source, 16),
                NormalizeNonNegativeInteger(Text(source, 14)),
                Text(source, 15),
                NormalizeInspectionRecord(Text(source, 22)),
                row.FileName,
                row.RowNumber);
            vehicles.Add(candidate);
            if (customer is not null)
            {
                customer.VehicleCount++;
            }
            AddIndex(vehicleByChassis, NormalizeIdentifier(candidate.ChassisNumber), candidate);
            AddIndex(vehicleByRegistration, NormalizeIdentifier(candidate.RegistrationNumber), candidate);
            previewRows.Add(new AbacusLegacyExportPreviewRow(
                "車両一覧",
                row.FileName,
                row.RowNumber,
                customer?.Name ?? "",
                candidate.VehicleName,
                candidate.RegistrationNumber,
                string.IsNullOrEmpty(customerName) ? "要確認" : "候補",
                string.IsNullOrEmpty(customerName)
                    ? "顧客名が空欄のため、未確定車両として保持しています。顧客へ接続しない場合は最終パッケージから除外されます。"
                    : string.IsNullOrEmpty(vehicleName)
                        ? "車名が空欄ですが、車両一覧の1行を候補として保持しています。"
                        : "車両一覧の1行を独立した車両候補として扱います。"));
        }

        var salesRows = input.Files.FirstOrDefault(file => file.Kind == "販売書類")?.Rows ?? [];
        var maintenanceRows = input.Files.FirstOrDefault(file => file.Kind == "整備書類")?.Rows ?? [];
        var sales = new List<DocumentCandidate>();
        var maintenance = new List<DocumentCandidate>();
        foreach (var row in salesRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = row.Fields;
            var customerName = Text(source, 5);
            if (string.IsNullOrEmpty(customerName))
            {
                skippedBlankCustomerRows++;
                previewRows.Add(new AbacusLegacyExportPreviewRow("販売書類", row.FileName, row.RowNumber, "", Text(source, 15), Text(source, 1), "除外", "顧客名が空欄のため無視しました。"));
                continue;
            }

            var customerSource = BuildCustomerSource(
                customerName,
                Text(source, 6),
                Text(source, 7),
                Text(source, 8),
                row.FileName,
                row.RowNumber);
            var match = ConfirmVehicleCustomer(
                FindVehicle(
                    vehicleByChassis,
                    vehicleByRegistration,
                    Text(source, 21),
                    Text(source, 22)),
                customerSource,
                customers);
            var customer = EnsureDocumentCustomer(customers, match, customerSource);
            var warning = match.Status switch
            {
                VehicleMatchStatus.Unique => "車台番号または登録番号が車両一覧の1行に一致しました。",
                VehicleMatchStatus.Ambiguous => "複数の車両候補に一致するため、車両への自動紐付けは保留しました。",
                VehicleMatchStatus.Conflict => "車両識別子は一致しましたが、顧客候補が異なるため自動紐付けを保留しました。",
                _ => "車台番号・登録番号の一致がないため、車両への自動紐付けは行っていません。",
            };
            if (match.Status is VehicleMatchStatus.Ambiguous or VehicleMatchStatus.Conflict)
            {
                ambiguousVehicleRows++;
            }

            var document = CreateDocumentCandidate(
                "販売書類",
                row,
                customer,
                match.Vehicle,
                Text(source, 1),
                NormalizeDocumentType(Text(source, 2), "請求書"),
                NormalizeCalendarDate(Text(source, 0)),
                NormalizeNonNegativeInteger(Text(source, 31)),
                $"ABACUS={row.FileName}#{row.RowNumber}; 区分原文={Text(source, 3)}; 金額は合計欄のみで税・明細は未確定。{warning}");
            var detailMatch = detailMapper.Match("販売書類", document.Number, customerName, document.VehicleName, Text(source, 22), Text(source, 21));
            document = document with { DetailsJson = AbacusDetailMapper.Serialize(detailMatch) };
            CountDetailMatch(detailMatch, ref detailMappedDocumentCount, ref detailReviewDocumentCount, ref detailUnsupportedDocumentCount, ref detailExcludedRowCount, ref amountOnlyDetailRowCount);
            sales.Add(document);
            previewRows.Add(new AbacusLegacyExportPreviewRow("販売書類", row.FileName, row.RowNumber, customer.Name, document.VehicleName, document.Number, match.StatusLabel, warning));
        }

        foreach (var row in maintenanceRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = row.Fields;
            var customerName = Text(source, 4);
            if (string.IsNullOrEmpty(customerName))
            {
                skippedBlankCustomerRows++;
                previewRows.Add(new AbacusLegacyExportPreviewRow("整備書類", row.FileName, row.RowNumber, "", Text(source, 13), Text(source, 1), "除外", "顧客名が空欄のため無視しました。"));
                continue;
            }

            var customerSource = BuildCustomerSource(
                customerName,
                Text(source, 5),
                Text(source, 6),
                Text(source, 7),
                row.FileName,
                row.RowNumber);
            var match = ConfirmVehicleCustomer(
                FindVehicle(
                    vehicleByChassis,
                    vehicleByRegistration,
                    Text(source, 19),
                    Text(source, 20)),
                customerSource,
                customers);
            var customer = EnsureDocumentCustomer(customers, match, customerSource);
            if (match.Status is VehicleMatchStatus.Ambiguous or VehicleMatchStatus.Conflict)
            {
                ambiguousVehicleRows++;
            }

            // 整備書類は車両一覧に一意一致しなくても、顧客直結の未確定書類として保持します。
            // 車両を失うと、UCS明細・金額・入庫日を後から復元できなくなるためです。
            var vehicle = match.Status == VehicleMatchStatus.Unique ? match.Vehicle : null;
            var warning = match.Status switch
            {
                VehicleMatchStatus.Unique => "車台番号または登録番号が車両一覧の1行に一致しました。",
                VehicleMatchStatus.Ambiguous => "複数の車両候補に一致するため、車両未確定のまま顧客直結で保持しました。",
                VehicleMatchStatus.Conflict => "車両識別子は一致しましたが顧客候補が異なるため、車両未確定のまま顧客直結で保持しました。",
                _ => "車両一覧の一意な一致がないため、車両未確定のまま顧客直結で保持しました。",
            };

            var document = CreateDocumentCandidate(
                "整備書類",
                row,
                customer,
                vehicle,
                Text(source, 1),
                NormalizeDocumentType(Text(source, 2), "整備請求書"),
                NormalizeCalendarDate(Text(source, 0)),
                NormalizeNonNegativeInteger(Text(source, 27)),
                $"ABACUS={row.FileName}#{row.RowNumber}; 備考原文={Text(source, 24)}; {warning}" );
            var detailMatch = detailMapper.Match("整備書類", document.Number, customerName, document.VehicleName, Text(source, 20), Text(source, 19));
            document = document with { DetailsJson = AbacusDetailMapper.Serialize(detailMatch) };
            CountDetailMatch(detailMatch, ref detailMappedDocumentCount, ref detailReviewDocumentCount, ref detailUnsupportedDocumentCount, ref detailExcludedRowCount, ref amountOnlyDetailRowCount);
            var intakeDate = NormalizeCalendarDate(Text(source, 25));
            document = document with
            {
                Category = "一般整備",
                // #635のようにABACUSの入庫日欄が空欄でも、書類日付は失わない。
                // Web側の発行日・入庫日の基準として書類日付を使用します。
                IntakeDate = string.IsNullOrWhiteSpace(intakeDate) ? document.IssuedAt : intakeDate,
                CompletionDate = NormalizeCalendarDate(Text(source, 26)),
            };
            maintenance.Add(document);
            previewRows.Add(new AbacusLegacyExportPreviewRow("整備書類", row.FileName, row.RowNumber, customer.Name, document.VehicleName, document.Number, match.StatusLabel, warning));
        }

        if (previewRows.Count > MaximumPreviewRows)
        {
            previewRows = previewRows.Take(MaximumPreviewRows).ToList();
        }

        var packagePath = CreateUniquePackageDirectory(destinationRoot);
        try
        {
            var customersCsv = BuildCustomersCsv(customers.Values.OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase));
            var vehiclesCsv = BuildVehiclesCsv(vehicles, customers);
            var salesCsv = BuildSalesCsv(sales, customers);
            var maintenanceCsv = BuildMaintenanceCsv(maintenance, customers);
            var outputFiles = new List<OutputFile>();
            outputFiles.Add(await WriteAndDescribeAsync(Path.Combine(packagePath, CustomersFileName), customersCsv, cancellationToken));
            outputFiles.Add(await WriteAndDescribeAsync(Path.Combine(packagePath, VehiclesFileName), vehiclesCsv, cancellationToken));
            outputFiles.Add(await WriteAndDescribeAsync(Path.Combine(packagePath, SalesFileName), salesCsv, cancellationToken));
            outputFiles.Add(await WriteAndDescribeAsync(Path.Combine(packagePath, MaintenanceFileName), maintenanceCsv, cancellationToken));
            if (outputFiles.Sum(file => file.SizeBytes) > MaximumOutputBytes)
            {
                throw new InvalidDataException($"候補CSVの合計サイズが上限{MaximumOutputBytes:N0} bytesを超えています。");
            }

            var warnings = new List<string>
            {
                "このパッケージはpreview-onlyです。顧客・車両・書類の登録、API送信、画像アップロードは行っていません。",
                "顧客名だけでは統合せず、住所または車両識別子を根拠に候補を作成しています。",
                "書類の税額・明細はABACUS出力の確定位置を確認するまで未確定として、合計欄を仮の合計として出力しています。",
            };
            if (skippedBlankCustomerRows > 0)
            {
                warnings.Add($"顧客名空欄の書類は除外し、顧客名空欄の車両は未確定候補として保持しました（対象{skippedBlankCustomerRows:N0}行）。");
            }
            if (skippedMaintenanceWithoutVehicleRows > 0)
            {
                warnings.Add($"車両の一意一致がない整備書類{skippedMaintenanceWithoutVehicleRows:N0}行を登録候補から除外しました。");
            }
            if (ambiguousVehicleRows > 0)
            {
                warnings.Add($"複数候補または顧客競合のある書類{ambiguousVehicleRows:N0}行は車両への自動紐付けを保留しました。");
            }
            warnings.Add(detailRoot is null
                ? "ABACUSのhb/sb UCSが見つからないため、書類明細は未対応として出力しました。"
                : $"Gate 19明細: 対応付け{detailMappedDocumentCount:N0}件 / 要確認{detailReviewDocumentCount:N0}件 / 未対応{detailUnsupportedDocumentCount:N0}件 / 除外行{detailExcludedRowCount:N0}件 / 金額のみ行{amountOnlyDetailRowCount:N0}件。");

            var manifest = new OutputManifest(
                Version: 1,
                Kind: "abacus-export-import-preview",
                Status: "preview-only",
                CreatedAtUtc: DateTime.UtcNow,
                Source: new OutputSource(sourceRoot, sourceFiles, sourceManifestHash),
                Summary: new OutputSummary(
                    customers.Count,
                    vehicles.Count,
                    sales.Count,
                    maintenance.Count,
                    skippedBlankCustomerRows,
                    skippedMaintenanceWithoutVehicleRows,
                    ambiguousVehicleRows,
                    detailMappedDocumentCount,
                    detailReviewDocumentCount,
                    detailUnsupportedDocumentCount,
                    detailExcludedRowCount,
                    amountOnlyDetailRowCount),
                DataFiles: outputFiles,
                Warnings: warnings,
                Rows: previewRows);
            var manifestPath = Path.Combine(packagePath, ManifestFileName);
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
            await WriteAndVerifyManifestAsync(manifestPath, manifestBytes, manifest, cancellationToken);
            var manifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes));

            return new AbacusLegacyExportPreviewResult(
                packagePath,
                manifestPath,
                manifestHash,
                outputFiles.Select(file => Path.Combine(packagePath, file.FileName)).ToArray(),
                customers.Count,
                vehicles.Count,
                sales.Count,
                maintenance.Count,
                skippedBlankCustomerRows,
                skippedMaintenanceWithoutVehicleRows,
                ambiguousVehicleRows,
                previewRows,
                detailMappedDocumentCount,
                detailReviewDocumentCount,
                detailUnsupportedDocumentCount,
                detailExcludedRowCount,
                amountOnlyDetailRowCount);
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

    private static CustomerCandidate EnsureDocumentCustomer(
        IDictionary<string, CustomerCandidate> customers,
        VehicleMatch match,
        CustomerSource source)
    {
        if (match.Status == VehicleMatchStatus.Unique && match.Vehicle is not null)
        {
            return EnsureCustomer(customers, source with
            {
                Key = match.Vehicle.CustomerKey,
            });
        }

        return EnsureCustomer(customers, source);
    }

    private static CustomerCandidate EnsureCustomer(
        IDictionary<string, CustomerCandidate> customers,
        CustomerSource source)
    {
        if (customers.TryGetValue(source.Key, out var existing))
        {
            if (string.IsNullOrEmpty(existing.NameKana) && !string.IsNullOrEmpty(source.NameKana)) existing.NameKana = source.NameKana;
            if (string.IsNullOrEmpty(existing.PostalCode) && !string.IsNullOrEmpty(source.PostalCode)) existing.PostalCode = source.PostalCode;
            if (string.IsNullOrEmpty(existing.Address) && !string.IsNullOrEmpty(source.Address)) existing.Address = source.Address;
            return existing;
        }

        var candidate = new CustomerCandidate(
            source.Key,
            CreateStableId("abacus-customer", source.Key),
            $"ABACUS-{CreateStableId("customer-number", source.Key)[..16].ToUpperInvariant()}",
            source.Name,
            source.NameKana,
            source.PostalCode,
            source.Address,
            0);
        customers.Add(source.Key, candidate);
        return candidate;
    }

    private static DocumentCandidate CreateDocumentCandidate(
        string kind,
        AbacusLegacyExportRow row,
        CustomerCandidate customer,
        VehicleCandidate? vehicle,
        string number,
        string type,
        string issuedAt,
        string total,
        string memo)
    {
        var safeNumber = string.IsNullOrEmpty(number) ? $"ABACUS-{row.FileName}-{row.RowNumber}" : number;
        return new DocumentCandidate(
            kind,
            CreateStableId(kind == "販売書類" ? "abacus-sales" : "abacus-maintenance", $"{row.FileName}:{row.RowNumber}"),
            safeNumber,
            type,
            customer.Key,
            vehicle?.VehicleId,
            vehicle?.VehicleName ?? "",
            vehicle?.RegistrationNumber ?? "",
            issuedAt,
            total,
            memo,
            kind == "整備書類" ? "一般整備" : "",
            "",
            "",
            "");
    }

    private static VehicleMatch FindVehicle(
        IReadOnlyDictionary<string, List<VehicleCandidate>> byChassis,
        IReadOnlyDictionary<string, List<VehicleCandidate>> byRegistration,
        string chassis,
        string registration)
    {
        var chassisKey = NormalizeIdentifier(chassis);
        var registrationKey = NormalizeIdentifier(registration);
        var chassisMatches = GetIndex(byChassis, chassisKey);
        var registrationMatches = GetIndex(byRegistration, registrationKey);
        IReadOnlyList<VehicleCandidate> matches;
        if (chassisMatches.Count > 0 && registrationMatches.Count > 0)
        {
            matches = chassisMatches.Intersect(registrationMatches).ToArray();
            if (matches.Count == 0)
            {
                return new VehicleMatch(VehicleMatchStatus.Conflict, null);
            }
        }
        else
        {
            matches = chassisMatches.Count > 0 ? chassisMatches : registrationMatches;
        }

        if (matches.Count == 1)
        {
            return new VehicleMatch(VehicleMatchStatus.Unique, matches[0]);
        }

        return matches.Count > 1
            ? new VehicleMatch(VehicleMatchStatus.Ambiguous, null)
            : new VehicleMatch(VehicleMatchStatus.None, null);
    }

    private static VehicleMatch ConfirmVehicleCustomer(
        VehicleMatch match,
        CustomerSource documentCustomer,
        IReadOnlyDictionary<string, CustomerCandidate> customers)
    {
        if (match.Status != VehicleMatchStatus.Unique || match.Vehicle is null)
        {
            return match;
        }

        if (!customers.TryGetValue(match.Vehicle.CustomerKey, out var vehicleCustomer))
        {
            return new VehicleMatch(VehicleMatchStatus.Conflict, null);
        }

        var documentName = NormalizeIdentifier(documentCustomer.Name);
        var vehicleName = NormalizeIdentifier(vehicleCustomer.Name);
        if (documentName.Length == 0 || vehicleName.Length == 0 ||
            !string.Equals(documentName, vehicleName, StringComparison.OrdinalIgnoreCase))
        {
            return new VehicleMatch(VehicleMatchStatus.Conflict, null);
        }

        var documentAddress = NormalizeIdentifier(documentCustomer.Address);
        var vehicleAddress = NormalizeIdentifier(vehicleCustomer.Address);
        if (documentAddress.Length > 0 && vehicleAddress.Length > 0 &&
            !string.Equals(documentAddress, vehicleAddress, StringComparison.OrdinalIgnoreCase))
        {
            return new VehicleMatch(VehicleMatchStatus.Conflict, null);
        }

        return match;
    }

    private static void AddIndex(
        IDictionary<string, List<VehicleCandidate>> index,
        string key,
        VehicleCandidate candidate)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (!index.TryGetValue(key, out var list))
        {
            list = [];
            index.Add(key, list);
        }

        list.Add(candidate);
    }

    private static IReadOnlyList<VehicleCandidate> GetIndex(
        IReadOnlyDictionary<string, List<VehicleCandidate>> index,
        string key) =>
        string.IsNullOrEmpty(key) || !index.TryGetValue(key, out var matches) ? [] : matches;

    private static CustomerSource BuildCustomerSource(
        string name,
        string nameKana,
        string postalCode,
        string address,
        string fileName,
        int rowNumber) =>
        new(
            BuildCustomerKey(name, address, fileName, rowNumber),
            name,
            nameKana,
            NormalizePostalCode(postalCode),
            address);

    private static string BuildCustomerKey(string name, string address, string fileName, int rowNumber)
    {
        var nameKey = NormalizeIdentifier(name);
        var addressKey = NormalizeIdentifier(address);
        return string.IsNullOrEmpty(addressKey)
            ? $"{nameKey}|source:{fileName}:{rowNumber}"
            : $"{nameKey}|address:{addressKey}";
    }

    private static byte[] BuildCustomersCsv(IEnumerable<CustomerCandidate> candidates)
    {
        var builder = new StringBuilder();
        AppendCsvRow(builder, ["顧客ID", "顧客番号", "顧客名", "ふりがな", "電話番号", "メールアドレス", "郵便番号", "住所", "メモ", "車両台数"]);
        foreach (var candidate in candidates)
        {
            AppendCsvRow(builder, [
                candidate.CustomerId,
                candidate.CustomerNumber,
                candidate.Name,
                candidate.NameKana,
                "",
                "",
                candidate.PostalCode,
                candidate.Address,
                "ABACUSエクスポート候補。顧客名だけによる自動統合なし。",
                candidate.VehicleCount.ToString(CultureInfo.InvariantCulture),
            ]);
        }

        return Utf8WithBom.GetBytes(builder.ToString());
    }

    private static byte[] BuildVehiclesCsv(
        IEnumerable<VehicleCandidate> candidates,
        IReadOnlyDictionary<string, CustomerCandidate> customers)
    {
        var builder = new StringBuilder();
        AppendCsvRow(builder, ["車両ID", "顧客ID", "顧客名", "メーカー", "車名", "型式", "登録番号", "車台番号", "年式", "車検満了日", "走行距離", "車体色", "排気量", "ミッション", "記録簿", "備考"]);
        foreach (var candidate in candidates)
        {
            var customer = string.IsNullOrWhiteSpace(candidate.CustomerKey)
                ? null
                : customers[candidate.CustomerKey];
            AppendCsvRow(builder, [
                candidate.VehicleId,
                customer?.CustomerId ?? "",
                customer?.Name ?? "",
                candidate.Maker,
                candidate.VehicleName,
                candidate.Model,
                candidate.RegistrationNumber,
                candidate.ChassisNumber,
                candidate.ModelYear,
                candidate.InspectionDate,
                candidate.Mileage,
                candidate.BodyColor,
                candidate.Displacement,
                candidate.Transmission,
                candidate.InspectionRecord,
                $"ABACUS={candidate.SourceFileName}#{candidate.SourceRowNumber}; 車両一覧の1行を独立候補として出力。",
            ]);
        }

        return Utf8WithBom.GetBytes(builder.ToString());
    }

    private static byte[] BuildSalesCsv(
        IEnumerable<DocumentCandidate> candidates,
        IReadOnlyDictionary<string, CustomerCandidate> customers)
    {
        var builder = new StringBuilder();
        AppendCsvRow(builder, ["書類ID", "書類番号", "書類種別", "ステータス", "顧客名", "車名", "登録番号", "発行日", "支払期限", "税率", "小計", "消費税", "合計", "明細", "備考", "明細詳細"]);
        foreach (var candidate in candidates)
        {
            var customer = customers[candidate.CustomerKey];
            var amounts = ResolveAmounts(candidate.DetailsJson, candidate.Total, candidate.IssuedAt);
            AppendCsvRow(builder, [
                candidate.DocumentId,
                candidate.Number,
                candidate.Type,
                "下書き",
                customer.Name,
                candidate.VehicleName,
                candidate.RegistrationNumber,
                candidate.IssuedAt,
                "",
                amounts.TaxRate,
                amounts.Subtotal,
                amounts.Tax,
                amounts.Total,
                "",
                candidate.Memo,
                candidate.DetailsJson,
            ]);
        }

        return Utf8WithBom.GetBytes(builder.ToString());
    }

    private static byte[] BuildMaintenanceCsv(
        IEnumerable<DocumentCandidate> candidates,
        IReadOnlyDictionary<string, CustomerCandidate> customers)
    {
        var builder = new StringBuilder();
        AppendCsvRow(builder, ["書類ID", "書類番号", "書類種別", "入庫区分", "ステータス", "顧客名", "車名", "登録番号", "入庫日", "出庫予定日", "支払期限", "税率", "小計", "消費税", "合計", "明細", "備考", "明細詳細"]);
        foreach (var candidate in candidates)
        {
            var customer = customers[candidate.CustomerKey];
            var amounts = ResolveAmounts(candidate.DetailsJson, candidate.Total, candidate.IssuedAt);
            AppendCsvRow(builder, [
                candidate.DocumentId,
                candidate.Number,
                candidate.Type,
                candidate.Category,
                "下書き",
                customer.Name,
                candidate.VehicleName,
                candidate.RegistrationNumber,
                candidate.IntakeDate,
                candidate.CompletionDate,
                "",
                amounts.TaxRate,
                amounts.Subtotal,
                amounts.Tax,
                amounts.Total,
                "",
                candidate.Memo,
                candidate.DetailsJson,
            ]);
        }

        return Utf8WithBom.GetBytes(builder.ToString());
    }

    private static (string TaxRate, string Subtotal, string Tax, string Total) ResolveAmounts(string detailsJson, string sourceTotal, string issuedAt)
    {
        var detail = ParseDetailJson(detailsJson);
        var sourceTotalValue = ParseAmount(sourceTotal);
        var totalValue = detail?.AbacusTotal ?? (sourceTotalValue == 0 ? null : sourceTotalValue);
        var taxRateValue = detail?.AbacusTaxRate ?? InferLegacyTaxRate(issuedAt);
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

        return (
            taxRateValue.ToString(CultureInfo.InvariantCulture),
            (subtotalValue ?? totalValue ?? 0).ToString(CultureInfo.InvariantCulture),
            (taxValue ?? 0).ToString(CultureInfo.InvariantCulture),
            (totalValue ?? 0).ToString(CultureInfo.InvariantCulture));
    }

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

    private static async Task<OutputFile> WriteAndDescribeAsync(
        string path,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        var verified = await File.ReadAllBytesAsync(path, cancellationToken);
        if (!verified.AsSpan().SequenceEqual(bytes))
        {
            throw new InvalidDataException($"作成したCSVの再読込検証に失敗しました: {Path.GetFileName(path)}");
        }

        return new OutputFile(Path.GetFileName(path), verified.LongLength, Convert.ToHexString(SHA256.HashData(verified)));
    }

    private static async Task WriteAndVerifyManifestAsync(
        string path,
        byte[] bytes,
        OutputManifest expected,
        CancellationToken cancellationToken)
    {
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        var verifiedBytes = await File.ReadAllBytesAsync(path, cancellationToken);
        try
        {
            var actual = JsonSerializer.Deserialize<OutputManifest>(verifiedBytes, JsonOptions);
            if (actual is null || actual.Version != expected.Version || actual.Kind != expected.Kind || actual.Status != expected.Status || actual.DataFiles.Count != expected.DataFiles.Count || actual.Rows.Count != expected.Rows.Count)
            {
                throw new InvalidDataException("作成した登録前候補マニフェストの再読込検証に失敗しました。");
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("作成した登録前候補マニフェストのJSONが不正です。", exception);
        }
    }

    private static string ValidateFolder(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException($"{label}を指定してください。", nameof(path));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var directory = new DirectoryInfo(root);
        if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException($"{label}が存在しないか、リンクです。");
        return root;
    }

    private static void ValidateDestination(string sourceRoot, string destinationRoot)
    {
        if (sourceRoot.Equals(destinationRoot, StringComparison.OrdinalIgnoreCase) || destinationRoot.StartsWith($"{sourceRoot}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("ABACUSエクスポートフォルダーの内部には候補パッケージを保存できません。");
        }
    }

    private static string CreateUniquePackageDirectory(string destinationRoot)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var suffix = attempt == 0 ? "" : $"-{attempt:D2}";
            var path = Path.Combine(destinationRoot, $"ABACUS-Export-Import-Preview-{DateTime.Now:yyyyMMdd-HHmmss}{suffix}");
            if (Directory.Exists(path) || File.Exists(path)) continue;
            Directory.CreateDirectory(path);
            return path;
        }

        throw new IOException("一意な登録前候補フォルダーを作成できませんでした。");
    }

    private static bool IsSameOrSubPath(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith($"{root}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static string Text(IReadOnlyList<string> fields, int index) =>
        index >= 0 && index < fields.Count ? CleanText(fields[index]) : "";

    private static string CleanText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var builder = new StringBuilder(value.Length);
        var previousWasSpace = false;
        foreach (var character in value.Trim())
        {
            var normalized = character is '\u0004' or '\u000B' or '\u001D' ? ' ' : character;
            if (char.IsControl(normalized) || char.IsWhiteSpace(normalized) || normalized == '　')
            {
                if (!previousWasSpace) builder.Append(' ');
                previousWasSpace = true;
            }
            else
            {
                builder.Append(normalized);
                previousWasSpace = false;
            }
        }

        return builder.ToString().Trim();
    }

    private static string NormalizeIdentifier(string? value) =>
        string.Concat(CleanText(value).Normalize(NormalizationForm.FormKC).Where(character => !char.IsWhiteSpace(character) && character != '-')).ToUpperInvariant();

    private static void CountDetailMatch(
        AbacusDetailMatch match,
        ref int mapped,
        ref int review,
        ref int unsupported,
        ref int excluded,
        ref int amountOnly)
    {
        if (match.IsMapped && match.Document is not null)
        {
            mapped++;
            excluded += match.Document.ExcludedDetailCount;
            amountOnly += match.Document.Lines.Count(line => line.IsAmountOnly);
        }
        else if (string.Equals(match.Status, "review", StringComparison.Ordinal)) review++;
        else unsupported++;
    }

    private static string? FindDetailRoot(string sourceRoot)
    {
        var current = new DirectoryInfo(sourceRoot);
        for (var depth = 0; current is not null && depth < 3; depth++, current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "abx-cs-hb.ucs")) || File.Exists(Path.Combine(current.FullName, "abx-cs-sb.ucs"))) return current.FullName;
        }

        return null;
    }

    private static string NormalizePostalCode(string value) =>
        string.Concat(value.Normalize(NormalizationForm.FormKC).Where(char.IsDigit));

    private static string NormalizeNonNegativeInteger(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC).Trim().Replace(",", "", StringComparison.Ordinal);
        if (normalized.Length == 0 || !normalized.All(char.IsDigit)) return "";
        return long.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number <= int.MaxValue
            ? number.ToString(CultureInfo.InvariantCulture)
            : "";
    }

    private static string NormalizeModelYear(string value)
    {
        var normalized = CleanText(value).Replace('．', '.').Replace('／', '/').Replace('－', '-');
        if (int.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year is >= 1 and <= 9999) return year.ToString(CultureInfo.InvariantCulture);
        var match = EraYearPattern.Match(normalized);
        return match.Success && int.TryParse(match.Groups["year"].Value, out var eraYear) && TryConvertEraYear(match.Groups["era"].Value, eraYear, out var converted)
            ? converted.ToString(CultureInfo.InvariantCulture)
            : "";
    }

    private static string NormalizeCalendarDate(string value)
    {
        var normalized = CleanText(value).Replace('．', '.').Replace('／', '/').Replace('－', '-');
        if (DateTime.TryParseExact(normalized, ["yyyy/M/d", "yyyy/M/dd", "yyyy/MM/d", "yyyy/MM/dd", "yyyy-M-d", "yyyy-M-dd", "yyyy.MM.dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var gregorian)) return gregorian.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var match = EraDatePattern.Match(normalized);
        if (!match.Success || !int.TryParse(match.Groups["year"].Value, out var eraYear) || !int.TryParse(match.Groups["month"].Value, out var month) || !int.TryParse(match.Groups["day"].Value, out var day) || !TryConvertEraYear(match.Groups["era"].Value, eraYear, out var year)) return "";
        try { return new DateTime(year, month, day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
        catch (ArgumentOutOfRangeException) { return ""; }
    }

    private static bool TryConvertEraYear(string era, int year, out int converted)
    {
        converted = 0;
        if (year is < 1 or > 99) return false;
        var baseYear = era.Trim().ToUpperInvariant() switch { "M" or "明治" => 1867, "T" or "大正" => 1911, "S" or "昭和" => 1925, "H" or "平成" => 1988, "R" or "令和" => 2018, _ => 0 };
        if (baseYear == 0) return false;
        converted = baseYear + year;
        return converted is >= 1 and <= 9999;
    }

    private static string NormalizeInspectionRecord(string value) =>
        CleanText(value).ToLowerInvariant() switch { "有" or "あり" or "○" or "〇" or "true" or "yes" => "あり", "無" or "なし" or "×" or "false" or "no" => "なし", _ => "" };

    private static string NormalizeDocumentType(string value, string fallback) =>
        value is "見積書" or "請求書" ? value : fallback;

    private static string CreateStableId(string prefix, string value)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        return $"{prefix}-{hash[..24]}";
    }

    private sealed class CustomerCandidate(
        string key,
        string customerId,
        string customerNumber,
        string name,
        string nameKana,
        string postalCode,
        string address,
        int vehicleCount)
    {
        public string Key { get; } = key;
        public string CustomerId { get; } = customerId;
        public string CustomerNumber { get; } = customerNumber;
        public string Name { get; } = name;
        public string NameKana { get; set; } = nameKana;
        public string PostalCode { get; set; } = postalCode;
        public string Address { get; set; } = address;
        public int VehicleCount { get; set; } = vehicleCount;
    }

    private sealed record VehicleCandidate(
        string VehicleId,
        string CustomerKey,
        string Maker,
        string VehicleName,
        string Model,
        string ChassisNumber,
        string RegistrationNumber,
        string ModelYear,
        string InspectionDate,
        string Mileage,
        string BodyColor,
        string Displacement,
        string Transmission,
        string InspectionRecord,
        string SourceFileName,
        int SourceRowNumber);

    private sealed record CustomerSource(string Key, string Name, string NameKana, string PostalCode, string Address);

    private sealed record DocumentCandidate(
        string Kind,
        string DocumentId,
        string DocumentNumber,
        string Type,
        string CustomerKey,
        string? VehicleId,
        string VehicleName,
        string RegistrationNumber,
        string IssuedAt,
        string Total,
        string Memo,
        string Category,
        string IntakeDate,
        string CompletionDate,
        string DetailsJson)
    {
        public string Number { get; } = string.IsNullOrEmpty(DocumentNumber) ? DocumentId : DocumentNumber;
        public string NumberForDisplay => Number;
        public string MatchStatusLabel => VehicleId is null ? "要確認" : "一意一致";
    }

    private enum VehicleMatchStatus { None, Unique, Ambiguous, Conflict }

    private sealed record VehicleMatch(VehicleMatchStatus Status, VehicleCandidate? Vehicle)
    {
        public string StatusLabel => Status switch { VehicleMatchStatus.Unique => "一意一致", VehicleMatchStatus.Ambiguous or VehicleMatchStatus.Conflict => "要確認", _ => "未一致" };
    }

    private sealed record SourceFile(string FileName, string Kind, string Sha256, int TotalRows, int ValidRows);
    private sealed record OutputFile(string FileName, long SizeBytes, string Sha256);
    private sealed record OutputSource(string FolderPath, IReadOnlyList<SourceFile> Files, string Fingerprint);
    private sealed record OutputSummary(int CustomerRows, int VehicleRows, int SalesRows, int MaintenanceRows, int SkippedBlankCustomerRows, int SkippedMaintenanceWithoutVehicleRows, int AmbiguousVehicleRows, int DetailMappedDocumentCount, int DetailReviewDocumentCount, int DetailUnsupportedDocumentCount, int DetailExcludedRowCount, int AmountOnlyDetailRowCount);
    private sealed record OutputManifest(int Version, string Kind, string Status, DateTime CreatedAtUtc, OutputSource Source, OutputSummary Summary, IReadOnlyList<OutputFile> DataFiles, IReadOnlyList<string> Warnings, IReadOnlyList<AbacusLegacyExportPreviewRow> Rows);
}
