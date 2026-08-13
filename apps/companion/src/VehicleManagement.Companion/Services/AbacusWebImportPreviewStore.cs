using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VehicleManagement.Companion.Services;

public sealed record AbacusWebImportPreviewCandidate(
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
    string Warning);

public sealed record AbacusWebImportPreviewResult(
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
    int SameNameGroupCount,
    IReadOnlyList<AbacusWebImportPreviewCandidate> Candidates);

/// <summary>
/// 確認済み画像の登録前候補を、既存Web CSVインポート形式へ変換します。
/// この段階ではDB/API/Object Storageへ接続せず、顧客名による自動統合も行いません。
/// </summary>
public sealed class AbacusWebImportPreviewStore
{
    private const string ManifestFileName = "manifest.json";
    private const string CustomersFileName = "customers.csv";
    private const string VehiclesFileName = "vehicles.csv";
    private const string ImageAttachmentsFileName = "image-attachments.json";
    private const long MaximumManifestBytes = 1L * 1024 * 1024;
    private const long MaximumImageBytes = 256L * 1024 * 1024;
    private const long MaximumPackageBytes = 1L * 1024 * 1024 * 1024;
    private const int MaximumCandidateCount = 1_000;
    private const int MaximumOutputTextCharacters = 500;
    private static readonly Regex EraDatePattern = new(
        "^(?<era>明治|大正|昭和|平成|令和|[MTSHRmts hr])\\s*(?<year>\\d{1,2})[./年月-](?<month>\\d{1,2})[./月-](?<day>\\d{1,2})日?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace);
    private static readonly Regex EraYearPattern = new(
        "^(?<era>明治|大正|昭和|平成|令和|[MTSHRmts hr])\\s*(?<year>\\d{1,2})(?:年|/\\d{1,2})?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private static readonly JsonSerializerOptions OutputJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
    private static readonly Encoding Utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    public async Task<AbacusWebImportPreviewResult> CreateAsync(
        string sourcePackageFolder,
        string destinationParent,
        CancellationToken cancellationToken = default)
    {
        var sourceRoot = ValidateFolder(sourcePackageFolder, "登録前候補パッケージ");
        var destinationRoot = ValidateFolder(destinationParent, "Webインポート候補保存先");
        ValidateDestination(sourceRoot, destinationRoot);

        var sourceManifestPath = Path.Combine(sourceRoot, ManifestFileName);
        ValidateRegularFile(sourceManifestPath, "登録前候補マニフェスト", MaximumManifestBytes);
        var sourceManifestBytes = await File.ReadAllBytesAsync(sourceManifestPath, cancellationToken);
        var sourceManifestSha256 = Convert.ToHexString(SHA256.HashData(sourceManifestBytes));
        var sourceManifest = DeserializeSourceManifest(sourceManifestBytes);
        var sourceCandidates = ValidateSourceManifest(sourceRoot, sourceManifest);
        var candidates = BuildCandidates(sourceCandidates, sourceRoot, sourceManifestSha256);
        var sameNameGroups = CountSameNameGroups(candidates);

        var packagePath = CreateUniquePackageDirectory(destinationRoot);
        var imageDirectory = Path.Combine(packagePath, "images");
        Directory.CreateDirectory(imageDirectory);
        try
        {
            var customersCsvBytes = BuildCustomersCsv(candidates);
            var vehiclesCsvBytes = BuildVehiclesCsv(candidates);
            var attachmentDocument = BuildImageAttachmentDocument(
                sourceRoot,
                sourceManifestSha256,
                candidates);
            var attachmentBytes = JsonSerializer.SerializeToUtf8Bytes(attachmentDocument, OutputJsonOptions);

            var customersCsvPath = Path.Combine(packagePath, CustomersFileName);
            var vehiclesCsvPath = Path.Combine(packagePath, VehiclesFileName);
            var attachmentsPath = Path.Combine(packagePath, ImageAttachmentsFileName);
            await WriteAndVerifyAsync(customersCsvPath, customersCsvBytes, cancellationToken);
            await WriteAndVerifyAsync(vehiclesCsvPath, vehiclesCsvBytes, cancellationToken);
            await WriteAndVerifyAsync(attachmentsPath, attachmentBytes, cancellationToken);

            var copiedImages = new List<PreviewFile>(candidates.Count);
            var totalImageBytes = 0L;
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceImagePath = ResolvePackageRelativePath(sourceRoot, candidate.PackageImageFileName);
                var sourceImageInfo = ValidateRegularFile(sourceImagePath, "登録前候補画像", MaximumImageBytes);
                totalImageBytes = checked(totalImageBytes + sourceImageInfo.Length);
                if (totalImageBytes > MaximumPackageBytes)
                {
                    throw new InvalidDataException($"画像合計サイズが上限{MaximumPackageBytes:N0} bytesを超えています。");
                }

                var packageImagePath = ResolvePackageRelativePath(packagePath, candidate.PackageImageFileName);
                await CopyAndVerifyAsync(
                    sourceImagePath,
                    packageImagePath,
                    candidate.ImageSha256,
                    cancellationToken);
                copiedImages.Add(await DescribeFileAsync(
                    packageImagePath,
                    candidate.PackageImageFileName,
                    cancellationToken));
            }

            var dataFiles = new[]
            {
                await DescribeFileAsync(customersCsvPath, CustomersFileName, cancellationToken),
                await DescribeFileAsync(vehiclesCsvPath, VehiclesFileName, cancellationToken),
                await DescribeFileAsync(attachmentsPath, ImageAttachmentsFileName, cancellationToken),
            };
            var warnings = BuildWarnings(candidates, sameNameGroups);
            var manifest = new OutputManifest(
                Version: 1,
                Kind: "abacus-web-import-preview",
                Status: "preview-only",
                CreatedAtUtc: DateTime.UtcNow,
                Source: new OutputSource(
                    sourceRoot,
                    ManifestFileName,
                    sourceManifestSha256,
                    sourceManifest.Summary?.CandidateCount ?? candidates.Count),
                Summary: new OutputSummary(
                    candidates.Count,
                    candidates.Count,
                    candidates.Count,
                    candidates.Count,
                    sameNameGroups,
                    candidates.Count,
                    "顧客ID・車両IDは候補ごとの仮IDです。顧客名による自動統合、DB登録、API送信、画像アップロードは行っていません。"),
                DataFiles: dataFiles,
                ImageFiles: copiedImages,
                Warnings: warnings,
                Candidates: candidates.Select(candidate => new ManifestCandidate(
                    candidate.CandidateId,
                    candidate.CustomerId,
                    candidate.CustomerNumber,
                    candidate.VehicleId,
                    candidate.CustomerMappingStatus,
                    candidate.Warning,
                    candidate.VehicleCsvFileName,
                    candidate.VehicleCsvRowNumber,
                    candidate.PackageImageFileName,
                    candidate.ImageSha256)).ToArray());
            var manifestPath = Path.Combine(packagePath, ManifestFileName);
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, OutputJsonOptions);
            await WriteAndVerifyManifestAsync(manifestPath, manifestBytes, manifest, cancellationToken);

            return new AbacusWebImportPreviewResult(
                packagePath,
                manifestPath,
                Convert.ToHexString(SHA256.HashData(manifestBytes)),
                customersCsvPath,
                vehiclesCsvPath,
                attachmentsPath,
                candidates.Count,
                candidates.Count,
                candidates.Count,
                copiedImages.Count,
                sameNameGroups,
                candidates);
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

    private static SourceManifest DeserializeSourceManifest(byte[] bytes)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<SourceManifest>(bytes, JsonOptions);
            if (manifest is null)
            {
                throw new InvalidDataException("登録前候補マニフェストが空です。");
            }

            return manifest;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("登録前候補マニフェストのJSONが不正です。", exception);
        }
    }

    private static IReadOnlyList<SourceCandidate> ValidateSourceManifest(
        string sourceRoot,
        SourceManifest manifest)
    {
        if (manifest.Version != 1 ||
            !string.Equals(manifest.Kind, "abacus-image-registration-preview", StringComparison.Ordinal) ||
            !string.Equals(manifest.Status, "preview-only", StringComparison.Ordinal))
        {
            throw new InvalidDataException("入力はGate5Jのpreview-onlyパッケージではありません。");
        }

        if (manifest.Candidates is null ||
            manifest.Candidates.Count == 0 ||
            manifest.Candidates.Count > MaximumCandidateCount)
        {
            throw new InvalidDataException($"登録前候補は1〜{MaximumCandidateCount:N0}件で指定してください。");
        }

        var candidateIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var imagePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<SourceCandidate>(manifest.Candidates.Count);
        var totalImageBytes = 0L;
        foreach (var candidate in manifest.Candidates)
        {
            var candidateId = RequiredText(candidate.CandidateId, "候補ID");
            if (!candidateIds.Add(candidateId))
            {
                throw new InvalidDataException($"候補IDが重複しています: {candidateId}");
            }

            var packageImageFileName = RequiredRelativePath(candidate.PackageImageFileName, "候補画像パス");
            if (!string.Equals(Path.GetExtension(packageImageFileName), ".png", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"候補画像はPNGである必要があります: {packageImageFileName}");
            }
            if (!imagePaths.Add(packageImageFileName))
            {
                throw new InvalidDataException($"候補画像が重複しています: {packageImageFileName}");
            }

            var imagePath = ResolvePackageRelativePath(sourceRoot, packageImageFileName);
            var imageInfo = ValidateRegularFile(imagePath, "登録前候補画像", MaximumImageBytes);
            totalImageBytes = checked(totalImageBytes + imageInfo.Length);
            if (totalImageBytes > MaximumPackageBytes)
            {
                throw new InvalidDataException($"画像合計サイズが上限{MaximumPackageBytes:N0} bytesを超えています。");
            }

            var expectedSha256 = RequiredSha256(candidate.ImageSha256, "候補画像SHA-256");
            var actualSha256 = CalculateSha256(imagePath);
            if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"登録前候補画像が変更されています: {packageImageFileName}");
            }

            if (candidate.VehicleCsvRowNumber < 1)
            {
                throw new InvalidDataException($"車両一覧CSV行番号が不正です: {candidateId}");
            }

            result.Add(new SourceCandidate(
                candidateId,
                RequiredText(candidate.CustomerName, "顧客名"),
                CleanText(candidate.Maker),
                RequiredText(candidate.VehicleName, "車名"),
                CleanText(candidate.Model),
                CleanText(candidate.ChassisNumber),
                CleanText(candidate.RegistrationNumber),
                CleanText(candidate.ModelYear),
                CleanText(candidate.InspectionDate),
                CleanText(candidate.Mileage),
                CleanText(candidate.BodyColor),
                CleanText(candidate.Displacement),
                CleanText(candidate.Transmission),
                CleanText(candidate.InspectionRecord),
                RequiredText(candidate.VehicleCsvFileName, "車両一覧CSVファイル名"),
                candidate.VehicleCsvRowNumber,
                packageImageFileName,
                expectedSha256));
        }

        return result;
    }

    private static IReadOnlyList<AbacusWebImportPreviewCandidate> BuildCandidates(
        IReadOnlyList<SourceCandidate> sourceCandidates,
        string sourceRoot,
        string sourceManifestSha256)
    {
        var nameCounts = sourceCandidates
            .GroupBy(candidate => NormalizeCustomerKey(candidate.CustomerName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var packageName = Path.GetFileName(sourceRoot);
        return sourceCandidates.Select(source =>
        {
            var customerId = CreateStableId("abacus-customer", source.CandidateId);
            var customerNumber = CreateStableId("ABACUS", source.CandidateId)[..24].ToUpperInvariant();
            var vehicleId = CreateStableId("abacus-vehicle", $"{source.CandidateId}:vehicle");
            var duplicateName = nameCounts[NormalizeCustomerKey(source.CustomerName)] > 1;
            var mappingStatus = duplicateName ? "same-name-manual-review" : "manual-new-master-candidate";
            var warning = duplicateName
                ? "同じ顧客名の候補が複数あります。顧客IDは自動統合していないため、同姓同名と複数車両を手動確認してください。"
                : "候補ごとの仮顧客IDです。既存顧客との自動照合は行っていません。";
            var modelYear = NormalizeModelYear(source.ModelYear);
            var inspectionDate = NormalizeCalendarDate(source.InspectionDate);
            var mileage = NormalizeNonNegativeInteger(source.Mileage);
            var displacement = NormalizeNonNegativeInteger(source.Displacement);
            var inspectionRecord = NormalizeInspectionRecord(source.InspectionRecord);
            var provenance = BuildProvenance(
                source,
                packageName,
                sourceManifestSha256,
                modelYear,
                inspectionDate,
                inspectionRecord);
            return new AbacusWebImportPreviewCandidate(
                source.CandidateId,
                customerId,
                customerNumber,
                vehicleId,
                source.CustomerName,
                source.Maker,
                source.VehicleName,
                source.Model,
                source.ChassisNumber,
                source.RegistrationNumber,
                modelYear,
                inspectionDate,
                mileage,
                source.BodyColor,
                displacement,
                source.Transmission,
                inspectionRecord,
                source.VehicleCsvFileName,
                source.VehicleCsvRowNumber,
                source.PackageImageFileName,
                source.ImageSha256,
                mappingStatus,
                warning + $" 出典: {provenance}");
        }).ToArray();
    }

    private static byte[] BuildCustomersCsv(IReadOnlyList<AbacusWebImportPreviewCandidate> candidates)
    {
        var builder = new StringBuilder();
        AppendCsvRow(builder, ["顧客ID", "顧客番号", "顧客名", "ふりがな", "電話番号", "メールアドレス", "郵便番号", "住所", "メモ", "車両台数"]);
        foreach (var candidate in candidates)
        {
            var memo = Truncate($"ABACUS候補ID={candidate.CandidateId}; {candidate.CustomerMappingStatus}; 顧客名による自動統合なし。{candidate.Warning}", MaximumOutputTextCharacters);
            AppendCsvRow(builder, [candidate.CustomerId, candidate.CustomerNumber, candidate.CustomerName, "", "", "", "", "", memo, "1"]);
        }

        return Utf8WithBom.GetBytes(builder.ToString());
    }

    private static byte[] BuildVehiclesCsv(IReadOnlyList<AbacusWebImportPreviewCandidate> candidates)
    {
        var builder = new StringBuilder();
        AppendCsvRow(builder, ["車両ID", "顧客ID", "顧客名", "メーカー", "車名", "型式", "登録番号", "車台番号", "年式", "車検満了日", "走行距離", "車体色", "排気量", "ミッション", "記録簿", "備考"]);
        foreach (var candidate in candidates)
        {
            var memo = Truncate(
                $"ABACUS候補ID={candidate.CandidateId}; CSV={candidate.VehicleCsvFileName}#{candidate.VehicleCsvRowNumber}; " +
                $"画像={candidate.PackageImageFileName}; {candidate.Warning}",
                MaximumOutputTextCharacters);
            AppendCsvRow(builder, [
                candidate.VehicleId,
                candidate.CustomerId,
                candidate.CustomerName,
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
                memo,
            ]);
        }

        return Utf8WithBom.GetBytes(builder.ToString());
    }

    private static ImageAttachmentDocument BuildImageAttachmentDocument(
        string sourceRoot,
        string sourceManifestSha256,
        IReadOnlyList<AbacusWebImportPreviewCandidate> candidates) =>
        new(
            Version: 1,
            Kind: "abacus-web-import-image-attachments",
            Status: "manual-upload-required",
            Source: new AttachmentSource(Path.GetFileName(sourceRoot), sourceManifestSha256),
            Attachments: candidates.Select(candidate => new ImageAttachment(
                candidate.CandidateId,
                candidate.CustomerId,
                candidate.VehicleId,
                candidate.PackageImageFileName,
                candidate.ImageSha256,
                "image/png",
                "manual-upload-required",
                "POST /api/vehicles/{vehicleId}/files")).ToArray());

    private static IReadOnlyList<string> BuildWarnings(
        IReadOnlyList<AbacusWebImportPreviewCandidate> candidates,
        int sameNameGroups)
    {
        var warnings = new List<string>
        {
            "このパッケージはpreview-onlyです。CSVを確認するまでWebインポートを実行しないでください。",
            "顧客ID・車両IDは候補ごとの仮IDです。既存顧客との自動照合は行っていません。",
            "画像はimage-attachments.jsonを参照して手動アップロードする段階であり、自動アップロードは行っていません。",
        };
        if (sameNameGroups > 0)
        {
            warnings.Add($"同じ顧客名の候補グループが{sameNameGroups:N0}件あります。同姓同名か複数車両かを手動確認してください。");
        }

        return warnings;
    }

    private static int CountSameNameGroups(IReadOnlyList<AbacusWebImportPreviewCandidate> candidates) =>
        candidates
            .GroupBy(candidate => NormalizeCustomerKey(candidate.CustomerName), StringComparer.OrdinalIgnoreCase)
            .Count(group => group.Count() > 1);

    private static string BuildProvenance(
        SourceCandidate source,
        string packageName,
        string sourceManifestSha256,
        string modelYear,
        string inspectionDate,
        string inspectionRecord) =>
        Truncate(
            $"候補パッケージ={packageName}; マニフェストSHA-256={sourceManifestSha256}; " +
            $"CSV={source.VehicleCsvFileName}#{source.VehicleCsvRowNumber}; " +
            $"年式原文={source.ModelYear}; 車検日原文={source.InspectionDate}; 記録簿原文={source.InspectionRecord}; " +
            $"正規化={modelYear}/{inspectionDate}/{inspectionRecord}",
            MaximumOutputTextCharacters);

    private static string NormalizeModelYear(string value)
    {
        var normalized = NormalizeForParsing(value);
        if (int.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out var year) &&
            year is >= 1 and <= 9999)
        {
            return year.ToString(CultureInfo.InvariantCulture);
        }

        var match = EraYearPattern.Match(normalized);
        if (!match.Success || !int.TryParse(match.Groups["year"].Value, out var eraYear))
        {
            return string.Empty;
        }

        return TryConvertEraYear(match.Groups["era"].Value, eraYear, out var converted)
            ? converted.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
    }

    private static string NormalizeCalendarDate(string value)
    {
        var normalized = NormalizeForParsing(value);
        if (DateTime.TryParseExact(
                normalized,
                ["yyyy/M/d", "yyyy/M/dd", "yyyy/MM/d", "yyyy/MM/dd", "yyyy-M-d", "yyyy-M-dd", "yyyy.MM.dd"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var gregorian))
        {
            return gregorian.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        var match = EraDatePattern.Match(normalized);
        if (!match.Success ||
            !int.TryParse(match.Groups["year"].Value, out var eraYear) ||
            !int.TryParse(match.Groups["month"].Value, out var month) ||
            !int.TryParse(match.Groups["day"].Value, out var day) ||
            !TryConvertEraYear(match.Groups["era"].Value, eraYear, out var year))
        {
            return string.Empty;
        }

        try
        {
            return new DateTime(year, month, day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return string.Empty;
        }
    }

    private static bool TryConvertEraYear(string era, int year, out int converted)
    {
        converted = 0;
        if (year is < 1 or > 99)
        {
            return false;
        }

        var normalized = era.Trim().ToUpperInvariant();
        var baseYear = normalized switch
        {
            "M" or "明治" => 1867,
            "T" or "大正" => 1911,
            "S" or "昭和" => 1925,
            "H" or "平成" => 1988,
            "R" or "令和" => 2018,
            _ => 0,
        };
        if (baseYear == 0)
        {
            return false;
        }

        converted = baseYear + year;
        return converted is >= 1 and <= 9999;
    }

    private static string NormalizeNonNegativeInteger(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC).Trim().Replace(",", string.Empty, StringComparison.Ordinal);
        if (normalized.Length == 0 || !normalized.All(char.IsDigit))
        {
            return string.Empty;
        }

        return long.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out var number) &&
               number <= int.MaxValue
            ? number.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
    }

    private static string NormalizeInspectionRecord(string value)
    {
        var normalized = NormalizeForParsing(value).ToLowerInvariant();
        return normalized switch
        {
            "有" or "あり" or "○" or "〇" or "true" or "yes" => "あり",
            "無" or "なし" or "×" or "false" or "no" => "なし",
            _ => string.Empty,
        };
    }

    private static string NormalizeForParsing(string value) =>
        CleanText(value)
            .Replace('．', '.')
            .Replace('／', '/')
            .Replace('－', '-')
            .Trim();

    private static string NormalizeCustomerKey(string value) =>
        string.Concat(
                CleanText(value)
                    .Normalize(NormalizationForm.FormKC)
                    .Where(character => !char.IsWhiteSpace(character)))
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
            if (character is '\u0004' or '\u000B' or '\u001D' || char.IsControl(character))
            {
                builder.Append(' ');
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Trim();
    }

    private static string RequiredText(string? value, string label)
    {
        var normalized = CleanText(value);
        if (normalized.Length == 0)
        {
            throw new InvalidDataException($"{label}がありません。");
        }

        return normalized;
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
        var normalized = RequiredText(value, label).ToUpperInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException($"{label}が不正です。");
        }

        return normalized;
    }

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

    private static string CreateStableId(string prefix, string value)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        return $"{prefix}-{hash[..32]}";
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
            throw new InvalidDataException("Webインポート候補は入力パッケージの内部へ保存できません。");
        }
    }

    private static string ResolvePackageRelativePath(string packageRoot, string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(packageRoot, normalized));
        if (!IsSameOrSubPath(fullPath, packageRoot))
        {
            throw new InvalidDataException("パッケージ外のファイルは使用できません。");
        }

        return fullPath;
    }

    private static FileInfo ValidateRegularFile(string path, string label, long maximumBytes)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException($"{label}が存在しない、またはリンクです。");
        }

        if (info.Length <= 0 || info.Length > maximumBytes)
        {
            throw new InvalidDataException($"{label}が上限{maximumBytes:N0} bytesを超えています。");
        }

        return info;
    }

    private static string CalculateSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static async Task<string> CalculateSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static async Task CopyAndVerifyAsync(
        string sourcePath,
        string destinationPath,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            throw new InvalidDataException("画像の出力先を確認できません。");
        }

        Directory.CreateDirectory(destinationDirectory);
        if (new DirectoryInfo(destinationDirectory).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("リンクまたは再解析ポイントへ画像を書き込めません。");
        }

        var temporaryPath = destinationPath + ".partial";
        try
        {
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }

            var actualSha256 = await CalculateSha256Async(temporaryPath, cancellationToken);
            if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"画像のコピー後SHA-256が一致しません: {Path.GetFileName(sourcePath)}");
            }

            File.Move(temporaryPath, destinationPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task WriteAndVerifyAsync(
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
            throw new InvalidDataException($"生成ファイルの再読込検証に失敗しました: {Path.GetFileName(path)}");
        }
    }

    private static async Task WriteAndVerifyManifestAsync(
        string path,
        byte[] bytes,
        OutputManifest expected,
        CancellationToken cancellationToken)
    {
        await WriteAndVerifyAsync(path, bytes, cancellationToken);
        try
        {
            var reloaded = JsonSerializer.Deserialize<OutputManifest>(bytes, JsonOptions);
            if (reloaded is null ||
                reloaded.Version != expected.Version ||
                !string.Equals(reloaded.Kind, expected.Kind, StringComparison.Ordinal) ||
                !string.Equals(reloaded.Status, expected.Status, StringComparison.Ordinal) ||
                reloaded.Summary.CandidateCount != expected.Summary.CandidateCount ||
                reloaded.DataFiles.Count != expected.DataFiles.Count ||
                reloaded.ImageFiles.Count != expected.ImageFiles.Count)
            {
                throw new InvalidDataException("作成したWebインポート候補マニフェストの再読込検証に失敗しました。");
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("作成したWebインポート候補マニフェストの形式が不正です。", exception);
        }
    }

    private static async Task<PreviewFile> DescribeFileAsync(
        string path,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var info = ValidateRegularFile(path, "生成ファイル", MaximumPackageBytes);
        return new PreviewFile(relativePath, info.Length, await CalculateSha256Async(path, cancellationToken));
    }

    private static string CreateUniquePackageDirectory(string destinationRoot)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var suffix = attempt == 0 ? string.Empty : $"-{attempt:D2}";
            var path = Path.Combine(destinationRoot, $"ABACUS-Web-Import-Preview-{DateTime.Now:yyyyMMdd-HHmmss}{suffix}");
            if (Directory.Exists(path) || File.Exists(path))
            {
                continue;
            }

            Directory.CreateDirectory(path);
            return path;
        }

        throw new IOException("Webインポート候補パッケージを一意に作成できませんでした。");
    }

    private static bool IsSameOrSubPath(string candidate, string root)
    {
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               normalizedCandidate.StartsWith($"{normalizedRoot}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record SourceManifest(
        int Version,
        string Kind,
        string Status,
        SourceSummary? Summary,
        IReadOnlyList<AbacusImageRegistrationPreviewCandidate> Candidates);

    private sealed record SourceSummary(int CandidateCount);

    private sealed record SourceCandidate(
        string CandidateId,
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
        string ImageSha256);

    private sealed record OutputManifest(
        int Version,
        string Kind,
        string Status,
        DateTime CreatedAtUtc,
        OutputSource Source,
        OutputSummary Summary,
        IReadOnlyList<PreviewFile> DataFiles,
        IReadOnlyList<PreviewFile> ImageFiles,
        IReadOnlyList<string> Warnings,
        IReadOnlyList<ManifestCandidate> Candidates);

    private sealed record OutputSource(
        string PackagePath,
        string ManifestFileName,
        string ManifestSha256,
        int CandidateCount);

    private sealed record OutputSummary(
        int CandidateCount,
        int CustomerRows,
        int VehicleRows,
        int ImageCount,
        int SameNameGroupCount,
        int ManualMappingRequiredCount,
        string Note);

    private sealed record PreviewFile(string RelativePath, long SizeBytes, string Sha256);

    private sealed record ManifestCandidate(
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

    private sealed record ImageAttachmentDocument(
        int Version,
        string Kind,
        string Status,
        AttachmentSource Source,
        IReadOnlyList<ImageAttachment> Attachments);

    private sealed record AttachmentSource(string PackageName, string ManifestSha256);

    private sealed record ImageAttachment(
        string CandidateId,
        string CustomerId,
        string VehicleId,
        string ImageFileName,
        string ImageSha256,
        string ContentType,
        string Status,
        string UploadEndpoint);
}
