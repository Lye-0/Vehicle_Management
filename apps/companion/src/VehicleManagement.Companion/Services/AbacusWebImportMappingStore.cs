using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VehicleManagement.Companion.Services;

public sealed record AbacusWebImportMappingCandidate(
    string CandidateId,
    string CustomerId,
    string CustomerNumber,
    string VehicleId,
    string CustomerName,
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
    string VehicleCsvFileName,
    int VehicleCsvRowNumber,
    string PackageImageFileName,
    string ImageSha256,
    string CustomerMappingStatus,
    string Warning,
    string DefaultCustomerGroupKey);

public sealed record AbacusWebImportMappingPackage(
    string PackagePath,
    string ManifestPath,
    string ManifestSha256,
    int CandidateCount,
    int SameNameGroupCount,
    IReadOnlyList<AbacusWebImportMappingCandidate> Candidates);

public sealed record AbacusWebImportMappingSelection(
    string CandidateId,
    string CustomerGroupKey);

public sealed record AbacusWebImportMappingApprovalResult(
    string MappingPackagePath,
    string MappingManifestPath,
    string MappingManifestSha256,
    int CandidateCount,
    int CustomerGroupCount,
    int GroupedVehicleCount,
    int SameNameGroupCount,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Webインポート候補の顧客グループを人が確認した証跡を作成します。
/// この段階ではCSV、DB、API、Object Storage、ABACUSフォルダーを書き換えません。
/// </summary>
public sealed class AbacusWebImportMappingStore
{
    private const string SourceManifestFileName = "manifest.json";
    private const string MappingManifestFileName = "mapping.json";
    private const string CustomersFileName = "customers.csv";
    private const string VehiclesFileName = "vehicles.csv";
    private const long MaximumManifestBytes = 1L * 1024 * 1024;
    private const long MaximumDataFileBytes = 5L * 1024 * 1024;
    private const long MaximumImageBytes = 256L * 1024 * 1024;
    private const long MaximumPackageBytes = 1L * 1024 * 1024 * 1024;
    private const int MaximumCandidateCount = 1_000;
    private const int MaximumFieldCharacters = 64 * 1024;
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

    public async Task<AbacusWebImportMappingPackage> ReadPackageAsync(
        string sourcePackageFolder,
        CancellationToken cancellationToken = default)
    {
        var sourceRoot = ValidateFolder(sourcePackageFolder, "Webインポート候補パッケージ");
        var manifestPath = Path.Combine(sourceRoot, SourceManifestFileName);
        ValidateRegularFile(manifestPath, "Webインポート候補マニフェスト", MaximumManifestBytes);
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
        var manifestSha256 = Convert.ToHexString(SHA256.HashData(manifestBytes));
        var manifest = DeserializePreviewManifest(manifestBytes);
        ValidatePreviewManifest(manifest);

        var files = await VerifyPackageFilesAsync(sourceRoot, manifest, cancellationToken);
        var customersRows = await ReadCsvAsync(
            ResolvePackageRelativePath(sourceRoot, files[CustomersFileName].RelativePath),
            CustomerHeaders,
            CustomersFileName,
            cancellationToken);
        var vehiclesRows = await ReadCsvAsync(
            ResolvePackageRelativePath(sourceRoot, files[VehiclesFileName].RelativePath),
            VehicleHeaders,
            VehiclesFileName,
            cancellationToken);
        var customersById = CreateUniqueRowMap(customersRows, 0, "顧客ID");
        var vehiclesById = CreateUniqueRowMap(vehiclesRows, 0, "車両ID");
        var imageFiles = manifest.ImageFiles.ToDictionary(
            file => file.RelativePath,
            file => file,
            StringComparer.OrdinalIgnoreCase);

        var candidates = new List<AbacusWebImportMappingCandidate>(manifest.Candidates.Count);
        var candidateIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var manifestCandidate in manifest.Candidates)
        {
            var candidateId = RequiredText(manifestCandidate.CandidateId, "候補ID");
            if (!candidateIds.Add(candidateId))
            {
                throw new InvalidDataException($"候補IDが重複しています: {candidateId}");
            }

            var customerId = RequiredText(manifestCandidate.CustomerId, "顧客ID");
            var vehicleId = RequiredText(manifestCandidate.VehicleId, "車両ID");
            if (!customersById.TryGetValue(customerId, out var customerRow))
            {
                throw new InvalidDataException($"候補が参照する顧客行がありません: {customerId}");
            }

            if (!vehiclesById.TryGetValue(vehicleId, out var vehicleRow))
            {
                throw new InvalidDataException($"候補が参照する車両行がありません: {vehicleId}");
            }

            if (!string.Equals(vehicleRow[1], customerId, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"車両と顧客のID対応が不一致です: {vehicleId}");
            }

            var imagePath = RequiredRelativePath(manifestCandidate.PackageImageFileName, "候補画像パス");
            if (!imageFiles.TryGetValue(imagePath, out var imageFile))
            {
                throw new InvalidDataException($"候補が参照する画像がマニフェストにありません: {imagePath}");
            }

            var expectedImageSha256 = RequiredSha256(manifestCandidate.ImageSha256, "候補画像SHA-256");
            if (!string.Equals(imageFile.Sha256, expectedImageSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"候補画像のSHA-256が一致しません: {imagePath}");
            }

            var candidate = new AbacusWebImportMappingCandidate(
                candidateId,
                customerId,
                RequiredText(customerRow[1], "顧客番号"),
                vehicleId,
                RequiredText(customerRow[2], "顧客名"),
                CleanText(vehicleRow[3]),
                RequiredText(vehicleRow[4], "車名"),
                CleanText(vehicleRow[5]),
                CleanText(vehicleRow[7]),
                CleanText(vehicleRow[6]),
                CleanText(vehicleRow[8]),
                CleanText(vehicleRow[9]),
                CleanText(vehicleRow[10]),
                CleanText(vehicleRow[11]),
                CleanText(vehicleRow[12]),
                CleanText(vehicleRow[13]),
                CleanText(vehicleRow[14]),
                RequiredText(manifestCandidate.VehicleCsvFileName, "車両一覧CSVファイル名"),
                manifestCandidate.VehicleCsvRowNumber,
                imagePath,
                expectedImageSha256,
                CleanText(manifestCandidate.CustomerMappingStatus),
                CleanText(manifestCandidate.Warning),
                customerId);
            if (candidate.VehicleCsvRowNumber < 1)
            {
                throw new InvalidDataException($"車両一覧CSV行番号が不正です: {candidateId}");
            }

            candidates.Add(candidate);
        }

        var sameNameGroupCount = candidates
            .GroupBy(candidate => NormalizeNameKey(candidate.CustomerName), StringComparer.OrdinalIgnoreCase)
            .Count(group => group.Count() > 1);
        return new AbacusWebImportMappingPackage(
            sourceRoot,
            manifestPath,
            manifestSha256,
            candidates.Count,
            sameNameGroupCount,
            candidates);
    }

    public async Task<AbacusWebImportMappingApprovalResult> CreateApprovalAsync(
        string sourcePackageFolder,
        string destinationParent,
        IReadOnlyList<AbacusWebImportMappingSelection> selections,
        CancellationToken cancellationToken = default)
    {
        var package = await ReadPackageAsync(sourcePackageFolder, cancellationToken);
        var destinationRoot = ValidateFolder(destinationParent, "対応付け証跡保存先");
        ValidateDestination(package.PackagePath, destinationRoot);
        if (selections is null || selections.Count != package.CandidateCount)
        {
            throw new InvalidDataException("候補全件の顧客グループを指定してください。");
        }

        var candidateById = package.Candidates.ToDictionary(
            candidate => candidate.CandidateId,
            candidate => candidate,
            StringComparer.OrdinalIgnoreCase);
        var selectionById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var selection in selections)
        {
            var candidateId = RequiredText(selection.CandidateId, "候補ID");
            if (!candidateById.ContainsKey(candidateId) ||
                !selectionById.TryAdd(candidateId, NormalizeGroupKey(selection.CustomerGroupKey)))
            {
                throw new InvalidDataException($"顧客グループ指定に重複または不明な候補IDがあります: {candidateId}");
            }
        }

        if (selectionById.Count != package.CandidateCount)
        {
            throw new InvalidDataException("候補全件の顧客グループを指定してください。");
        }

        var grouped = package.Candidates.GroupBy(
            candidate => selectionById[candidate.CandidateId],
            StringComparer.OrdinalIgnoreCase);
        var mappingEntries = new List<MappingEntry>(package.CandidateCount);
        var warnings = new List<string>();
        foreach (var group in grouped)
        {
            var groupCandidates = group.ToList();
            var distinctNames = groupCandidates
                .Select(candidate => NormalizeNameKey(candidate.CustomerName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (distinctNames.Count != 1)
            {
                throw new InvalidDataException(
                    $"異なる顧客名を同じグループへ設定できません: {string.Join("、", groupCandidates.Select(candidate => candidate.CustomerName).Distinct())}");
            }

            if (groupCandidates.Count > 1)
            {
                warnings.Add($"顧客グループ「{groupCandidates[0].CustomerName}」に{groupCandidates.Count:N0}台の車両をまとめました。複数車両であることを確認してください。");
            }

            foreach (var candidate in groupCandidates)
            {
                mappingEntries.Add(new MappingEntry(
                    candidate.CandidateId,
                    candidate.CustomerId,
                    candidate.CustomerNumber,
                    candidate.VehicleId,
                    candidate.CustomerName,
                    candidate.VehicleCsvFileName,
                    candidate.VehicleCsvRowNumber,
                    candidate.PackageImageFileName,
                    candidate.ImageSha256,
                    group.Key,
                    groupCandidates.Count > 1 ? "same-customer-multiple-vehicles" : "new-customer",
                    candidate.Warning));
            }
        }

        if (package.SameNameGroupCount > 0)
        {
            warnings.Add($"入力候補には同じ顧客名のグループが{package.SameNameGroupCount:N0}件あります。名前だけで自動統合していません。");
        }

        var mappingManifest = new MappingManifest(
            Version: 1,
            Kind: "abacus-web-import-mapping",
            Status: "human-reviewed",
            CreatedAtUtc: DateTime.UtcNow,
            Source: new MappingSource(
                package.PackagePath,
                SourceManifestFileName,
                package.ManifestSha256),
            Summary: new MappingSummary(
                package.CandidateCount,
                grouped.Count(),
                mappingEntries.Count(entry => entry.Decision == "same-customer-multiple-vehicles"),
                package.SameNameGroupCount,
                "この証跡は顧客グループの人手確認だけを記録します。顧客・車両・画像の登録やAPI送信は行っていません。"),
            Warnings: warnings,
            Mappings: mappingEntries.OrderBy(entry => entry.CandidateId, StringComparer.OrdinalIgnoreCase).ToArray());

        var mappingPackagePath = CreateUniqueMappingDirectory(destinationRoot);
        var mappingManifestPath = Path.Combine(mappingPackagePath, MappingManifestFileName);
        var mappingBytes = JsonSerializer.SerializeToUtf8Bytes(mappingManifest, OutputJsonOptions);
        try
        {
            await WriteAndVerifyAsync(mappingManifestPath, mappingBytes, mappingManifest, cancellationToken);
            return new AbacusWebImportMappingApprovalResult(
                mappingPackagePath,
                mappingManifestPath,
                Convert.ToHexString(SHA256.HashData(mappingBytes)),
                package.CandidateCount,
                grouped.Count(),
                mappingEntries.Count(entry => entry.Decision == "same-customer-multiple-vehicles"),
                package.SameNameGroupCount,
                warnings);
        }
        catch
        {
            if (Directory.Exists(mappingPackagePath) && IsSameOrSubPath(mappingPackagePath, destinationRoot))
            {
                Directory.Delete(mappingPackagePath, recursive: true);
            }

            throw;
        }
    }

    private static PreviewManifest DeserializePreviewManifest(byte[] bytes)
    {
        try
        {
            return JsonSerializer.Deserialize<PreviewManifest>(bytes, JsonOptions)
                ?? throw new InvalidDataException("Webインポート候補マニフェストが空です。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Webインポート候補マニフェストのJSONが不正です。", exception);
        }
    }

    private static void ValidatePreviewManifest(PreviewManifest manifest)
    {
        if (manifest.Version != 1 ||
            !string.Equals(manifest.Kind, "abacus-web-import-preview", StringComparison.Ordinal) ||
            !string.Equals(manifest.Status, "preview-only", StringComparison.Ordinal))
        {
            throw new InvalidDataException("入力はGate5Kのpreview-onlyパッケージではありません。");
        }

        if (manifest.Candidates is null || manifest.Candidates.Count == 0 || manifest.Candidates.Count > MaximumCandidateCount)
        {
            throw new InvalidDataException($"Webインポート候補は1〜{MaximumCandidateCount:N0}件で指定してください。");
        }

        if (manifest.DataFiles is null || manifest.ImageFiles is null)
        {
            throw new InvalidDataException("Webインポート候補マニフェストのファイル一覧がありません。");
        }
    }

    private static async Task<Dictionary<string, PreviewFile>> VerifyPackageFilesAsync(
        string packageRoot,
        PreviewManifest manifest,
        CancellationToken cancellationToken)
    {
        var files = manifest.DataFiles
            .Concat(manifest.ImageFiles)
            .ToList();
        var map = new Dictionary<string, PreviewFile>(StringComparer.OrdinalIgnoreCase);
        var totalBytes = 0L;
        foreach (var file in files)
        {
            var relativePath = RequiredRelativePath(file.RelativePath, "候補パッケージファイルパス");
            if (!map.TryAdd(relativePath, file))
            {
                throw new InvalidDataException($"候補パッケージのファイルパスが重複しています: {relativePath}");
            }

            var fullPath = ResolvePackageRelativePath(packageRoot, relativePath);
            var maximumBytes = relativePath.StartsWith("images/", StringComparison.OrdinalIgnoreCase)
                ? MaximumImageBytes
                : MaximumDataFileBytes;
            var info = ValidateRegularFile(fullPath, "候補パッケージファイル", maximumBytes);
            if (info.Length != file.SizeBytes)
            {
                throw new InvalidDataException($"候補パッケージファイルのサイズが一致しません: {relativePath}");
            }

            var actualSha256 = await CalculateSha256Async(fullPath, cancellationToken);
            if (!string.Equals(actualSha256, RequiredSha256(file.Sha256, "候補パッケージファイルSHA-256"), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"候補パッケージファイルが変更されています: {relativePath}");
            }

            totalBytes = checked(totalBytes + info.Length);
            if (totalBytes > MaximumPackageBytes)
            {
                throw new InvalidDataException($"候補パッケージの合計サイズが上限{MaximumPackageBytes:N0} bytesを超えています。");
            }
        }

        foreach (var required in new[] { CustomersFileName, VehiclesFileName, "image-attachments.json" })
        {
            if (!map.ContainsKey(required))
            {
                throw new InvalidDataException($"候補パッケージに必要なファイルがありません: {required}");
            }
        }

        return map;
    }

    private static async Task<IReadOnlyList<string[]>> ReadCsvAsync(
        string path,
        IReadOnlyList<string> expectedHeaders,
        string label,
        CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes).TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException($"{label}はUTF-8として読めません。", exception);
        }

        var rows = ParseCsv(text, label);
        if (rows.Count < 2 || !rows[0].SequenceEqual(expectedHeaders, StringComparer.Ordinal))
        {
            throw new InvalidDataException($"{label}の見出し行が既存Webインポート形式と一致しません。");
        }

        var result = rows.Skip(1).Where(row => row.Any(value => value.Length > 0)).ToList();
        if (result.Count == 0 || result.Count > MaximumCandidateCount)
        {
            throw new InvalidDataException($"{label}のデータ行数が不正です。");
        }

        return result;
    }

    private static IReadOnlyList<string[]> ParseCsv(string text, string label)
    {
        var table = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '"')
            {
                if (quoted && index + 1 < text.Length && text[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == ',' && !quoted)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if ((character == '\r' || character == '\n') && !quoted)
            {
                if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }

                row.Add(field.ToString());
                table.Add(row.ToArray());
                row.Clear();
                field.Clear();
            }
            else
            {
                field.Append(character);
            }

            if (field.Length > MaximumFieldCharacters)
            {
                throw new InvalidDataException($"{label}のフィールドが長すぎます。");
            }
        }

        if (quoted)
        {
            throw new InvalidDataException($"{label}の引用符が閉じられていません。");
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            table.Add(row.ToArray());
        }

        if (table.Any(values => values.Length != table[0].Length))
        {
            throw new InvalidDataException($"{label}の列数が一致しません。");
        }

        return table;
    }

    private static Dictionary<string, string[]> CreateUniqueRowMap(
        IReadOnlyList<string[]> rows,
        int keyIndex,
        string label)
    {
        var map = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var key = RequiredText(row[keyIndex], label);
            if (!map.TryAdd(key, row))
            {
                throw new InvalidDataException($"{label}が重複しています: {key}");
            }
        }

        return map;
    }

    private static string NormalizeGroupKey(string? value)
    {
        var key = CleanText(value).Normalize(NormalizationForm.FormKC);
        if (key.Length == 0 || key.Length > 200)
        {
            throw new InvalidDataException("顧客グループキーは1〜200文字で入力してください。");
        }

        return key;
    }

    private static string NormalizeNameKey(string value) =>
        string.Concat(CleanText(value).Normalize(NormalizationForm.FormKC).Where(character => !char.IsWhiteSpace(character)))
            .ToUpperInvariant();

    private static string CleanText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim())
        {
            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        return builder.ToString().Trim();
    }

    private static string RequiredText(string? value, string label)
    {
        var text = CleanText(value);
        if (text.Length == 0)
        {
            throw new InvalidDataException($"{label}がありません。");
        }

        return text;
    }

    private static string RequiredRelativePath(string? value, string label)
    {
        var normalized = RequiredText(value, label).Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized) ||
            normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                .Any(part => part is "." or ".."))
        {
            throw new InvalidDataException($"{label}がパッケージ外を指しています。");
        }

        return normalized.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string RequiredSha256(string? value, string label)
    {
        var sha256 = RequiredText(value, label).ToUpperInvariant();
        if (sha256.Length != 64 || sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException($"{label}が不正です。");
        }

        return sha256;
    }

    private static string ValidateFolder(string folderPath, string label)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
        var directory = new DirectoryInfo(root);
        if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException($"{label}が存在しないか、リンクまたは再解析ポイントです。");
        }

        return root;
    }

    private static void ValidateDestination(string sourceRoot, string destinationRoot)
    {
        if (IsSameOrSubPath(destinationRoot, sourceRoot))
        {
            throw new InvalidDataException("対応付け証跡を入力候補パッケージの内部へ保存できません。");
        }
    }

    private static string ResolvePackageRelativePath(string packageRoot, string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(packageRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsSameOrSubPath(fullPath, packageRoot))
        {
            throw new InvalidDataException("パッケージ外のファイルは使用できません。");
        }

        return fullPath;
    }

    private static FileInfo ValidateRegularFile(string path, string label, long maximumBytes)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length <= 0 || info.Length > maximumBytes)
        {
            throw new InvalidDataException($"{label}が存在しないか、サイズ上限を超えています。");
        }

        return info;
    }

    private static async Task<string> CalculateSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static async Task WriteAndVerifyAsync(
        string path,
        byte[] bytes,
        MappingManifest expected,
        CancellationToken cancellationToken)
    {
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        var reloadedBytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (!reloadedBytes.AsSpan().SequenceEqual(bytes))
        {
            throw new InvalidDataException("対応付け証跡の書込後検証に失敗しました。");
        }

        try
        {
            var reloaded = JsonSerializer.Deserialize<MappingManifest>(reloadedBytes, JsonOptions);
            if (reloaded is null ||
                reloaded.Version != expected.Version ||
                !string.Equals(reloaded.Kind, expected.Kind, StringComparison.Ordinal) ||
                !string.Equals(reloaded.Status, expected.Status, StringComparison.Ordinal) ||
                reloaded.Mappings.Count != expected.Mappings.Count ||
                reloaded.Summary.CustomerGroupCount != expected.Summary.CustomerGroupCount)
            {
                throw new InvalidDataException("対応付け証跡の再読込検証に失敗しました。");
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("対応付け証跡JSONの再読込に失敗しました。", exception);
        }
    }

    private static string CreateUniqueMappingDirectory(string destinationRoot)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var suffix = attempt == 0 ? string.Empty : $"-{attempt:D2}";
            var path = Path.Combine(destinationRoot, $"ABACUS-Web-Import-Mapping-{DateTime.Now:yyyyMMdd-HHmmss}{suffix}");
            if (Directory.Exists(path) || File.Exists(path))
            {
                continue;
            }

            Directory.CreateDirectory(path);
            return path;
        }

        throw new IOException("対応付け証跡フォルダーを一意に作成できませんでした。");
    }

    private static bool IsSameOrSubPath(string candidate, string root)
    {
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               normalizedCandidate.StartsWith($"{normalizedRoot}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record PreviewManifest(
        int Version,
        string Kind,
        string Status,
        IReadOnlyList<PreviewFile> DataFiles,
        IReadOnlyList<PreviewFile> ImageFiles,
        IReadOnlyList<PreviewCandidate> Candidates);

    private sealed record PreviewFile(string RelativePath, long SizeBytes, string Sha256);

    private sealed record PreviewCandidate(
        string CandidateId,
        string CustomerId,
        string CustomerNumber,
        string VehicleId,
        string CustomerMappingStatus,
        string Warning,
        string VehicleCsvFileName,
        int VehicleCsvRowNumber,
        string PackageImageFileName,
        string ImageSha256);

    private sealed record MappingManifest(
        int Version,
        string Kind,
        string Status,
        DateTime CreatedAtUtc,
        MappingSource Source,
        MappingSummary Summary,
        IReadOnlyList<string> Warnings,
        IReadOnlyList<MappingEntry> Mappings);

    private sealed record MappingSource(string PackagePath, string ManifestFileName, string ManifestSha256);

    private sealed record MappingSummary(
        int CandidateCount,
        int CustomerGroupCount,
        int GroupedVehicleCount,
        int SameNameGroupCount,
        string Note);

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
}
