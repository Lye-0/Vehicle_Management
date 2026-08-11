using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VehicleManagement.Companion.Services;

public sealed record AbacusWebImportRegistrationGroupPreview(
    string CustomerId,
    string CustomerNumber,
    string CustomerName,
    string CustomerGroupKey,
    int VehicleCount);

public sealed record AbacusWebImportRegistrationVehiclePreview(
    string CandidateId,
    string VehicleId,
    string CustomerId,
    string CustomerName,
    string Maker,
    string VehicleName,
    string ChassisNumber,
    string RegistrationNumber,
    string PackageImageFileName,
    string ImageSha256,
    string Decision);

public sealed record AbacusWebImportRegistrationPackageResult(
    string PackagePath,
    string ManifestPath,
    string ManifestSha256,
    string CustomersCsvPath,
    string VehiclesCsvPath,
    string ImageAttachmentsPath,
    int CandidateCount,
    int CustomerRowCount,
    int VehicleRowCount,
    int ImageCount,
    int MergedVehicleCount,
    IReadOnlyList<AbacusWebImportRegistrationGroupPreview> Groups,
    IReadOnlyList<AbacusWebImportRegistrationVehiclePreview> Vehicles);

/// <summary>
/// Gate5Lの人手確認済み対応付けを、Web画面へ渡す登録前パッケージへ変換します。
/// この段階ではWeb API、D1、Object Storage、ABACUSフォルダーへ接続・書込しません。
/// </summary>
public sealed class AbacusWebImportRegistrationPackageStore
{
    private const string MappingManifestFileName = "mapping.json";
    private const string SourceManifestFileName = "manifest.json";
    private const string CustomersFileName = "customers.csv";
    private const string VehiclesFileName = "vehicles.csv";
    private const string ImageAttachmentsFileName = "image-attachments.json";
    private const long MaximumManifestBytes = 1L * 1024 * 1024;
    private const long MaximumDataFileBytes = 5L * 1024 * 1024;
    private const long MaximumImageBytes = 256L * 1024 * 1024;
    private const long MaximumPackageBytes = 1L * 1024 * 1024 * 1024;
    private const int MaximumCandidateCount = 1_000;
    private const int MaximumTextCharacters = 500;
    private static readonly string[] CustomerHeaders =
    [
        "顧客ID", "顧客番号", "顧客名", "ふりがな", "電話番号", "メールアドレス", "郵便番号", "住所", "メモ", "車両台数",
    ];
    private static readonly string[] VehicleHeaders =
    [
        "車両ID", "顧客ID", "顧客名", "メーカー", "車名", "型式", "登録番号", "車台番号", "年式", "車検満了日", "走行距離", "車体色", "排気量", "ミッション", "記録簿", "備考",
    ];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private static readonly JsonSerializerOptions OutputJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Encoding Utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    private readonly AbacusWebImportMappingStore mappingStore = new();

    public async Task<AbacusWebImportRegistrationPackageResult> CreateAsync(
        string mappingPackageFolder,
        string destinationParent,
        CancellationToken cancellationToken = default)
    {
        var mappingRoot = ValidateFolder(mappingPackageFolder, "対応付け証跡パッケージ");
        var destinationRoot = ValidateFolder(destinationParent, "登録前パッケージ保存先");
        ValidateDestination(mappingRoot, destinationRoot);

        var mappingManifestPath = Path.Combine(mappingRoot, MappingManifestFileName);
        ValidateRegularFile(mappingManifestPath, "対応付けマニフェスト", MaximumManifestBytes);
        var mappingBytes = await File.ReadAllBytesAsync(mappingManifestPath, cancellationToken);
        var mappingSha256 = Convert.ToHexString(SHA256.HashData(mappingBytes));
        var mappingManifest = DeserializeMappingManifest(mappingBytes);
        ValidateMappingManifest(mappingManifest);

        var sourcePackagePath = ValidateFolder(mappingManifest.Source.PackagePath, "対応付け元Web候補パッケージ");
        ValidateDestination(sourcePackagePath, destinationRoot);
        var sourcePackage = await mappingStore.ReadPackageAsync(sourcePackagePath, cancellationToken);
        if (!string.Equals(sourcePackage.ManifestSha256, mappingManifest.Source.ManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("対応付け証跡の入力候補マニフェストSHA-256と現在のパッケージが一致しません。");
        }
        if (!string.Equals(mappingManifest.Source.ManifestFileName, SourceManifestFileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("対応付け証跡の入力マニフェスト名が不正です。");
        }

        var candidatesById = sourcePackage.Candidates.ToDictionary(
            candidate => candidate.CandidateId,
            candidate => candidate,
            StringComparer.OrdinalIgnoreCase);
        var entries = ValidateMappings(mappingManifest.Mappings, candidatesById);
        var groups = entries
            .GroupBy(entry => entry.CustomerGroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => BuildGroup(group.Key, group.ToList()))
            .OrderBy(group => group.CustomerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.CustomerId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var customerIdByCandidateId = entries.ToDictionary(
            entry => entry.CandidateId,
            entry => groups.Single(group => group.CustomerGroupKey.Equals(entry.CustomerGroupKey, StringComparison.OrdinalIgnoreCase)).CustomerId,
            StringComparer.OrdinalIgnoreCase);
        var vehicles = entries
            .Select(entry => BuildVehicle(entry, candidatesById[entry.CandidateId], customerIdByCandidateId[entry.CandidateId]))
            .OrderBy(vehicle => vehicle.CustomerId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(vehicle => vehicle.VehicleId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var packagePath = CreateUniquePackageDirectory(destinationRoot);
        var imageDirectory = Path.Combine(packagePath, "images");
        Directory.CreateDirectory(imageDirectory);
        try
        {
            var customersCsvBytes = BuildCustomersCsv(groups);
            var vehiclesCsvBytes = BuildVehiclesCsv(vehicles, entries, candidatesById);
            var attachmentsDocument = BuildImageAttachmentDocument(
                mappingRoot,
                mappingSha256,
                sourcePackage,
                entries,
                customerIdByCandidateId);
            var attachmentsBytes = JsonSerializer.SerializeToUtf8Bytes(attachmentsDocument, OutputJsonOptions);
            var customersCsvPath = Path.Combine(packagePath, CustomersFileName);
            var vehiclesCsvPath = Path.Combine(packagePath, VehiclesFileName);
            var attachmentsPath = Path.Combine(packagePath, ImageAttachmentsFileName);
            await WriteAndVerifyAsync(customersCsvPath, customersCsvBytes, cancellationToken);
            await WriteAndVerifyAsync(vehiclesCsvPath, vehiclesCsvBytes, cancellationToken);
            await WriteAndVerifyAsync(attachmentsPath, attachmentsBytes, cancellationToken);

            var copiedImages = new List<OutputFile>(entries.Count);
            var totalImageBytes = 0L;
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceImagePath = ResolvePackageRelativePath(sourcePackage.PackagePath, entry.PackageImageFileName);
                var sourceImageInfo = ValidateRegularFile(sourceImagePath, "登録前画像", MaximumImageBytes);
                totalImageBytes = checked(totalImageBytes + sourceImageInfo.Length);
                if (totalImageBytes > MaximumPackageBytes)
                {
                    throw new InvalidDataException($"画像合計サイズが上限{MaximumPackageBytes:N0} bytesを超えています。");
                }

                var packageImagePath = ResolvePackageRelativePath(packagePath, entry.PackageImageFileName);
                await CopyAndVerifyAsync(sourceImagePath, packageImagePath, entry.ImageSha256, cancellationToken);
                copiedImages.Add(await DescribeFileAsync(packageImagePath, entry.PackageImageFileName, cancellationToken));
            }

            var dataFiles = new[]
            {
                await DescribeFileAsync(customersCsvPath, CustomersFileName, cancellationToken),
                await DescribeFileAsync(vehiclesCsvPath, VehiclesFileName, cancellationToken),
                await DescribeFileAsync(attachmentsPath, ImageAttachmentsFileName, cancellationToken),
            };
            var warnings = BuildWarnings(mappingManifest, entries, groups);
            var outputManifest = new OutputManifest(
                Version: 1,
                Kind: "abacus-web-import-registration-package",
                Status: "registration-preview",
                CreatedAtUtc: DateTime.UtcNow,
                Source: new OutputSource(
                    mappingRoot,
                    MappingManifestFileName,
                    mappingSha256,
                    sourcePackage.PackagePath,
                    SourceManifestFileName,
                    sourcePackage.ManifestSha256),
                Summary: new OutputSummary(
                    entries.Count,
                    groups.Length,
                    vehicles.Length,
                    copiedImages.Count,
                    entries.Count - groups.Length,
                    "Gate5Lの人手確認済み顧客グループを反映した登録前パッケージです。Web API、DB、画像アップロードは行っていません。"),
                DataFiles: dataFiles,
                ImageFiles: copiedImages,
                Warnings: warnings,
                Groups: groups.Select(group => new ManifestGroup(
                    group.CustomerId,
                    group.CustomerNumber,
                    group.CustomerName,
                    group.CustomerGroupKey,
                    group.VehicleCount)).ToArray(),
                Candidates: entries.Select(entry => new ManifestCandidate(
                    entry.CandidateId,
                    customerIdByCandidateId[entry.CandidateId],
                    entry.SourceVehicleId,
                    entry.CustomerName,
                    entry.VehicleCsvFileName,
                    entry.VehicleCsvRowNumber,
                    entry.PackageImageFileName,
                    entry.ImageSha256,
                    entry.Decision,
                    entry.CustomerGroupKey)).ToArray());
            var manifestPath = Path.Combine(packagePath, SourceManifestFileName);
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(outputManifest, OutputJsonOptions);
            await WriteAndVerifyManifestAsync(manifestPath, manifestBytes, outputManifest, cancellationToken);

            return new AbacusWebImportRegistrationPackageResult(
                packagePath,
                manifestPath,
                Convert.ToHexString(SHA256.HashData(manifestBytes)),
                customersCsvPath,
                vehiclesCsvPath,
                attachmentsPath,
                entries.Count,
                groups.Length,
                vehicles.Length,
                copiedImages.Count,
                entries.Count - groups.Length,
                groups,
                vehicles);
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

    private static IReadOnlyList<MappingEntry> ValidateMappings(
        IReadOnlyList<MappingEntry>? mappings,
        IReadOnlyDictionary<string, AbacusWebImportMappingCandidate> candidatesById)
    {
        if (mappings is null || mappings.Count == 0 || mappings.Count > MaximumCandidateCount)
        {
            throw new InvalidDataException($"対応付け証跡は1〜{MaximumCandidateCount:N0}件で指定してください。");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<MappingEntry>(mappings.Count);
        foreach (var mapping in mappings)
        {
            var candidateId = RequiredText(mapping.CandidateId, "候補ID");
            if (!seen.Add(candidateId))
            {
                throw new InvalidDataException($"対応付け証跡の候補IDが重複しています: {candidateId}");
            }
            if (!candidatesById.TryGetValue(candidateId, out var candidate))
            {
                throw new InvalidDataException($"対応付け証跡が参照する候補がありません: {candidateId}");
            }

            if (!string.Equals(RequiredText(mapping.SourceCustomerId, "元顧客ID"), candidate.CustomerId, StringComparison.Ordinal) ||
                !string.Equals(RequiredText(mapping.SourceCustomerNumber, "元顧客番号"), candidate.CustomerNumber, StringComparison.Ordinal) ||
                !string.Equals(RequiredText(mapping.SourceVehicleId, "元車両ID"), candidate.VehicleId, StringComparison.Ordinal) ||
                !string.Equals(RequiredText(mapping.CustomerName, "顧客名"), candidate.CustomerName, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"対応付け証跡の候補情報が入力パッケージと一致しません: {candidateId}");
            }
            if (mapping.VehicleCsvRowNumber != candidate.VehicleCsvRowNumber ||
                !string.Equals(RequiredText(mapping.VehicleCsvFileName, "車両一覧CSVファイル名"), candidate.VehicleCsvFileName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(RequiredRelativePath(mapping.PackageImageFileName, "画像パス"), candidate.PackageImageFileName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(RequiredSha256(mapping.ImageSha256, "画像SHA-256"), candidate.ImageSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"対応付け証跡の出典情報が入力パッケージと一致しません: {candidateId}");
            }

            var groupKey = RequiredGroupKey(mapping.CustomerGroupKey);
            var decision = RequiredText(mapping.Decision, "対応付け判断");
            if (decision is not ("new-customer" or "same-customer-multiple-vehicles"))
            {
                throw new InvalidDataException($"対応付け判断が不正です: {decision}");
            }
            result.Add(mapping with
            {
                CandidateId = candidateId,
                SourceCustomerId = candidate.CustomerId,
                SourceVehicleId = candidate.VehicleId,
                CustomerName = candidate.CustomerName,
                VehicleCsvFileName = candidate.VehicleCsvFileName,
                VehicleCsvRowNumber = candidate.VehicleCsvRowNumber,
                PackageImageFileName = candidate.PackageImageFileName,
                ImageSha256 = candidate.ImageSha256,
                CustomerGroupKey = groupKey,
                Decision = decision,
                Warning = CleanText(mapping.Warning),
            });
        }

        foreach (var group in result.GroupBy(entry => entry.CustomerGroupKey, StringComparer.OrdinalIgnoreCase))
        {
            var names = group.Select(entry => NormalizeNameKey(entry.CustomerName)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (names.Length != 1)
            {
                throw new InvalidDataException($"異なる顧客名を同じ顧客グループへ登録できません: {string.Join("、", group.Select(entry => entry.CustomerName).Distinct())}");
            }
            var expectedDecision = group.Count() > 1 ? "same-customer-multiple-vehicles" : "new-customer";
            if (group.Any(entry => !string.Equals(entry.Decision, expectedDecision, StringComparison.Ordinal)))
            {
                throw new InvalidDataException($"顧客グループの対応付け判断が行数と一致しません: {group.Key}");
            }
        }

        return result;
    }

    private static AbacusWebImportRegistrationGroupPreview BuildGroup(
        string groupKey,
        IReadOnlyList<MappingEntry> entries)
    {
        var hash = CreateStableHash($"customer-group:{groupKey}");
        var customerId = $"abacus-customer-group-{hash[..32].ToLowerInvariant()}";
        var customerNumber = $"ABACUS-{hash[..18].ToUpperInvariant()}";
        return new AbacusWebImportRegistrationGroupPreview(
            customerId,
            customerNumber,
            entries[0].CustomerName,
            groupKey,
            entries.Count);
    }

    private static AbacusWebImportRegistrationVehiclePreview BuildVehicle(
        MappingEntry entry,
        AbacusWebImportMappingCandidate candidate,
        string customerId)
    {
        return new AbacusWebImportRegistrationVehiclePreview(
            entry.CandidateId,
            entry.SourceVehicleId,
            customerId,
            entry.CustomerName,
            candidate.Maker,
            candidate.VehicleName,
            candidate.ChassisNumber,
            candidate.RegistrationNumber,
            entry.PackageImageFileName,
            entry.ImageSha256,
            entry.Decision);
    }

    private static byte[] BuildCustomersCsv(IReadOnlyList<AbacusWebImportRegistrationGroupPreview> groups)
    {
        var builder = new StringBuilder();
        AppendCsvRow(builder, CustomerHeaders);
        foreach (var group in groups)
        {
            var memo = Truncate(
                $"ABACUS対応付けグループ={group.CustomerGroupKey}; Gate5L確認済み; 顧客名による自動統合なし。",
                MaximumTextCharacters);
            AppendCsvRow(builder, [group.CustomerId, group.CustomerNumber, group.CustomerName, "", "", "", "", "", memo, group.VehicleCount.ToString(CultureInfo.InvariantCulture)]);
        }

        return Utf8WithBom.GetBytes(builder.ToString());
    }

    private static byte[] BuildVehiclesCsv(
        IReadOnlyList<AbacusWebImportRegistrationVehiclePreview> vehicles,
        IReadOnlyList<MappingEntry> entries,
        IReadOnlyDictionary<string, AbacusWebImportMappingCandidate> candidatesById)
    {
        var entryByCandidateId = entries.ToDictionary(entry => entry.CandidateId, StringComparer.OrdinalIgnoreCase);
        var builder = new StringBuilder();
        AppendCsvRow(builder, VehicleHeaders);
        foreach (var vehicle in vehicles)
        {
            var entry = entryByCandidateId[vehicle.CandidateId];
            var memo = Truncate(
                $"ABACUS候補ID={entry.CandidateId}; 顧客グループ={entry.CustomerGroupKey}; 判断={entry.Decision}; " +
                $"CSV={entry.VehicleCsvFileName}#{entry.VehicleCsvRowNumber}; 画像={entry.PackageImageFileName}; {entry.Warning}",
                MaximumTextCharacters);
            AppendCsvRow(builder, [
                vehicle.VehicleId,
                vehicle.CustomerId,
                vehicle.CustomerName,
                candidatesById[vehicle.CandidateId].Maker,
                candidatesById[vehicle.CandidateId].VehicleName,
                candidatesById[vehicle.CandidateId].Model,
                candidatesById[vehicle.CandidateId].RegistrationNumber,
                candidatesById[vehicle.CandidateId].ChassisNumber,
                candidatesById[vehicle.CandidateId].ModelYear,
                candidatesById[vehicle.CandidateId].InspectionDate,
                candidatesById[vehicle.CandidateId].Mileage,
                candidatesById[vehicle.CandidateId].BodyColor,
                candidatesById[vehicle.CandidateId].Displacement,
                candidatesById[vehicle.CandidateId].Transmission,
                candidatesById[vehicle.CandidateId].InspectionRecord,
                memo,
            ]);
        }

        return Utf8WithBom.GetBytes(builder.ToString());
    }

    private static ImageAttachmentDocument BuildImageAttachmentDocument(
        string mappingRoot,
        string mappingSha256,
        AbacusWebImportMappingPackage sourcePackage,
        IReadOnlyList<MappingEntry> entries,
        IReadOnlyDictionary<string, string> customerIdByCandidateId) =>
        new(
            Version: 1,
            Kind: "abacus-web-import-image-attachments",
            Status: "manual-upload-required",
            Source: new AttachmentSource(
                Path.GetFileName(mappingRoot),
                mappingSha256,
                Path.GetFileName(sourcePackage.PackagePath),
                sourcePackage.ManifestSha256),
            Attachments: entries.Select(entry => new ImageAttachment(
                entry.CandidateId,
                customerIdByCandidateId[entry.CandidateId],
                entry.SourceVehicleId,
                entry.CustomerGroupKey,
                entry.PackageImageFileName,
                entry.ImageSha256,
                "image/png",
                "manual-upload-required",
                "POST /api/vehicles/{vehicleId}/files")).ToArray());

    private static IReadOnlyList<string> BuildWarnings(
        MappingManifest mappingManifest,
        IReadOnlyList<MappingEntry> entries,
        IReadOnlyList<AbacusWebImportRegistrationGroupPreview> groups)
    {
        var warnings = new List<string>
        {
            "このパッケージはregistration-previewです。CSVのプレビュー確認が完了するまでWebインポートを実行しないでください。",
            "顧客ID・車両IDはABACUS移行用の仮IDです。既存顧客との自動照合は行っていません。",
            "画像はimage-attachments.jsonを参照して手動アップロードする段階であり、自動アップロードは行っていません。",
        };
        if (entries.Count != groups.Count)
        {
            warnings.Add($"Gate5Lで{entries.Count - groups.Count:N0}台を同一顧客の複数車両としてまとめました。顧客CSVはグループ単位で出力しています。");
        }
        if (mappingManifest.Warnings is { Count: > 0 })
        {
            warnings.AddRange(mappingManifest.Warnings.Select(CleanText).Where(value => value.Length > 0));
        }

        return warnings.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static MappingManifest DeserializeMappingManifest(byte[] bytes)
    {
        try
        {
            return JsonSerializer.Deserialize<MappingManifest>(bytes, JsonOptions)
                ?? throw new InvalidDataException("対応付けマニフェストが空です。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("対応付けマニフェストのJSONが不正です。", exception);
        }
    }

    private static void ValidateMappingManifest(MappingManifest manifest)
    {
        if (manifest.Version != 1 ||
            !string.Equals(manifest.Kind, "abacus-web-import-mapping", StringComparison.Ordinal) ||
            !string.Equals(manifest.Status, "human-reviewed", StringComparison.Ordinal))
        {
            throw new InvalidDataException("入力はGate5Lのhuman-reviewed対応付け証跡ではありません。");
        }
        if (manifest.Source is null)
        {
            throw new InvalidDataException("対応付け証跡に入力候補パッケージがありません。");
        }
        RequiredText(manifest.Source.PackagePath, "入力候補パッケージパス");
        if (!string.Equals(RequiredText(manifest.Source.ManifestFileName, "入力マニフェスト名"), SourceManifestFileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("対応付け証跡の入力マニフェスト名が不正です。");
        }
        RequiredSha256(manifest.Source.ManifestSha256, "入力マニフェストSHA-256");
    }

    private static string CreateUniquePackageDirectory(string destinationRoot)
    {
        var baseName = $"ABACUS-Web-Import-Registration-Preview-{DateTime.Now:yyyyMMdd-HHmmss}";
        for (var index = 0; index < 100; index++)
        {
            var suffix = index == 0 ? string.Empty : $"-{index + 1:D2}";
            var path = Path.Combine(destinationRoot, baseName + suffix);
            if (Directory.Exists(path) || File.Exists(path))
            {
                continue;
            }

            Directory.CreateDirectory(path);
            return path;
        }

        throw new IOException("Web登録前パッケージを一意に作成できませんでした。");
    }

    private static async Task WriteAndVerifyAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
        var actual = await File.ReadAllBytesAsync(path, cancellationToken);
        if (!actual.AsSpan().SequenceEqual(bytes))
        {
            throw new IOException($"書き込んだファイルを再読込検証できません: {path}");
        }
    }

    private static async Task WriteAndVerifyManifestAsync(
        string path,
        byte[] bytes,
        OutputManifest manifest,
        CancellationToken cancellationToken)
    {
        await WriteAndVerifyAsync(path, bytes, cancellationToken);
        var reloadedBytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var reloaded = JsonSerializer.Deserialize<OutputManifest>(reloadedBytes, JsonOptions);
        if (reloaded is null ||
            reloaded.Version != manifest.Version ||
            !string.Equals(reloaded.Kind, manifest.Kind, StringComparison.Ordinal) ||
            !string.Equals(reloaded.Status, manifest.Status, StringComparison.Ordinal) ||
            reloaded.Summary.CandidateCount != manifest.Summary.CandidateCount ||
            reloaded.DataFiles.Count != manifest.DataFiles.Count ||
            reloaded.ImageFiles.Count != manifest.ImageFiles.Count)
        {
            throw new IOException("作成したWeb登録前マニフェストの再読込検証に失敗しました。");
        }
    }

    private static async Task<OutputFile> DescribeFileAsync(
        string path,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var info = ValidateRegularFile(path, "登録前パッケージファイル", MaximumPackageBytes);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        return new OutputFile(relativePath, info.Length, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static async Task CopyAndVerifyAsync(
        string sourcePath,
        string destinationPath,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidDataException("画像保存先を解決できません。");
        Directory.CreateDirectory(destinationDirectory);
        File.Copy(sourcePath, destinationPath, overwrite: false);
        var actualSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(destinationPath, cancellationToken)));
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"コピーした画像のSHA-256が一致しません: {destinationPath}");
        }
    }

    private static FileInfo ValidateRegularFile(string path, string label, long maximumBytes)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{label}が見つかりません。", path);
        }
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException($"{label}が通常ファイルではありません: {path}");
        }
        var info = new FileInfo(path);
        if (info.Length <= 0 || info.Length > maximumBytes)
        {
            throw new InvalidDataException($"{label}のサイズが上限または下限を超えています: {path}");
        }

        return info;
    }

    private static string ValidateFolder(string path, string label)
    {
        var value = RequiredText(path, label);
        var fullPath = Path.GetFullPath(value);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"{label}が見つかりません: {fullPath}");
        }

        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    private static void ValidateDestination(string inputRoot, string destinationRoot)
    {
        if (IsSameOrSubPath(destinationRoot, inputRoot))
        {
            throw new InvalidDataException("登録前パッケージ保存先は入力パッケージの外側を選択してください。");
        }
    }

    private static string ResolvePackageRelativePath(string packageRoot, string relativePath)
    {
        var normalized = RequiredRelativePath(relativePath, "パッケージ相対パス");
        var fullPath = Path.GetFullPath(Path.Combine(packageRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsSameOrSubPath(fullPath, packageRoot))
        {
            throw new InvalidDataException("パッケージ相対パスが入力フォルダーの外側を指しています。");
        }

        return fullPath;
    }

    private static string RequiredRelativePath(string? value, string label)
    {
        var normalized = RequiredText(value, label).Replace('\\', '/');
        if (normalized.StartsWith('/') ||
            normalized.Contains(':') ||
            normalized.Split('/').Any(segment => segment is ".." or "") ||
            normalized.Any(char.IsControl) ||
            normalized.Length > 500)
        {
            throw new InvalidDataException($"{label}が不正です: {normalized}");
        }

        return normalized;
    }

    private static string RequiredGroupKey(string? value)
    {
        var normalized = RequiredText(value, "顧客グループキー");
        if (normalized.Length > 256 || normalized.Any(char.IsControl))
        {
            throw new InvalidDataException("顧客グループキーが不正です。");
        }

        return normalized;
    }

    private static string RequiredText(string? value, string label)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length == 0 || text.Length > 64 * 1024 || text.Any(char.IsControl))
        {
            throw new InvalidDataException($"{label}が空欄または不正です。");
        }

        return text;
    }

    private static string RequiredSha256(string? value, string label)
    {
        var text = RequiredText(value, label);
        if (text.Length != 64 || text.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException($"{label}がSHA-256形式ではありません。");
        }

        return text.ToUpperInvariant();
    }

    private static string CleanText(string? value) =>
        (value ?? string.Empty).Trim().Replace('\r', ' ').Replace('\n', ' ');

    private static string NormalizeNameKey(string value) =>
        string.Concat(value.Normalize().Where(character => !char.IsWhiteSpace(character))).ToUpperInvariant();

    private static string Truncate(string value, int maximumCharacters) =>
        value.Length <= maximumCharacters ? value : value[..maximumCharacters];

    private static string CreateStableHash(string value) =>
        Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(value)));

    private static bool IsSameOrSubPath(string candidate, string root)
    {
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
            normalizedCandidate.StartsWith($"{normalizedRoot}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }

    private static void AppendCsvRow(StringBuilder builder, IReadOnlyList<string> values)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            var value = values[index] ?? string.Empty;
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

    private sealed record MappingManifest(
        int Version,
        string Kind,
        string Status,
        DateTime CreatedAtUtc,
        MappingSource Source,
        IReadOnlyList<string>? Warnings,
        IReadOnlyList<MappingEntry>? Mappings);

    private sealed record MappingSource(
        string PackagePath,
        string ManifestFileName,
        string ManifestSha256);

    private sealed record MappingEntry(
        string CandidateId,
        string SourceCustomerId,
        string SourceCustomerNumber,
        string SourceVehicleId,
        string CustomerName,
        string VehicleCsvFileName,
        int VehicleCsvRowNumber,
        string PackageImageFileName,
        string ImageSha256,
        string CustomerGroupKey,
        string Decision,
        string Warning);

    private sealed record ImageAttachmentDocument(
        int Version,
        string Kind,
        string Status,
        AttachmentSource Source,
        IReadOnlyList<ImageAttachment> Attachments);

    private sealed record AttachmentSource(
        string MappingPackageName,
        string MappingManifestSha256,
        string SourcePackageName,
        string SourceManifestSha256);

    private sealed record ImageAttachment(
        string CandidateId,
        string CustomerId,
        string VehicleId,
        string CustomerGroupKey,
        string ImagePath,
        string ImageSha256,
        string ContentType,
        string Status,
        string UploadEndpoint);

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
        IReadOnlyList<ManifestCandidate> Candidates);

    private sealed record OutputSource(
        string MappingPackagePath,
        string MappingManifestFileName,
        string MappingManifestSha256,
        string SourcePackagePath,
        string SourceManifestFileName,
        string SourceManifestSha256);

    private sealed record OutputSummary(
        int CandidateCount,
        int CustomerRowCount,
        int VehicleRowCount,
        int ImageCount,
        int MergedVehicleCount,
        string Note);

    private sealed record OutputFile(string RelativePath, long SizeBytes, string Sha256);

    private sealed record ManifestGroup(
        string CustomerId,
        string CustomerNumber,
        string CustomerName,
        string CustomerGroupKey,
        int VehicleCount);

    private sealed record ManifestCandidate(
        string CandidateId,
        string CustomerId,
        string VehicleId,
        string CustomerName,
        string VehicleCsvFileName,
        int VehicleCsvRowNumber,
        string PackageImageFileName,
        string ImageSha256,
        string Decision,
        string CustomerGroupKey);
}
