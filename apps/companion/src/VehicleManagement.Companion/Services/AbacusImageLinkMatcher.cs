using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VehicleManagement.AbacusImport;

namespace VehicleManagement.Companion.Services;

public sealed record AbacusImageLinkMatchRow(
    string ManifestFileName,
    string ImageFileName,
    string Status,
    string StatusLabel,
    string MatchStrategy,
    string Identifier,
    string CustomerName,
    int CandidateCount,
    string Vehicle,
    string CsvLocation,
    string Reason)
{
    /// <summary>
    /// 照合に使用した候補行です。画面表示用の要約だけで承認しないよう、
    /// 手動確認段階で再検証するために保持します。
    /// </summary>
    public IReadOnlyList<AbacusVehicleExportRow> Candidates { get; init; } = [];

    public string ApprovalStatus { get; init; } = "未確認";
}

public sealed record AbacusImageLinkMatchReport(
    string ImageFolderPath,
    string VehicleExportFolderPath,
    int ManifestCount,
    int MatchedCount,
    int ReviewCount,
    int ConflictCount,
    int NotFoundCount,
    int InvalidCount,
    IReadOnlyList<AbacusImageLinkMatchRow> Rows,
    IReadOnlyList<string> Errors,
    DateTime CreatedAtUtc)
{
    public bool IsValid => Errors.Count == 0 && InvalidCount == 0;
}

public sealed class AbacusImageLinkMatcher
{
    private const int MaximumManifestCount = 1_000;
    private const long MaximumManifestBytes = 1L * 1024 * 1024;
    private const long MaximumImageBytes = 256L * 1024 * 1024;
    private const int MaximumIdentifierCharacters = 128;
    private const int MaximumCustomerNameCharacters = 200;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly AbacusVehicleExportReader vehicleExportReader = new();

    public async Task<AbacusImageLinkMatchReport> MatchAsync(
        string imageFolder,
        string vehicleExportFolder,
        CancellationToken cancellationToken = default)
    {
        var imageRoot = ValidateFolder(imageFolder, "画像保存先");
        var vehicleRoot = ValidateFolder(vehicleExportFolder, "車両一覧CSVフォルダー");
        var vehicleExport = await vehicleExportReader.ReadAsync(vehicleRoot, cancellationToken);
        if (!vehicleExport.IsValid)
        {
            var details = string.Join(
                "\n",
                vehicleExport.Errors.Take(20).Select(error =>
                    $"{error.RowNumber?.ToString() ?? "ファイル"}: {error.Message}"));
            throw new InvalidDataException($"車両一覧CSVの診断に失敗しました。{(details.Length == 0 ? string.Empty : $"\n{details}")}");
        }

        var manifestFiles = Directory.EnumerateFiles(
                imageRoot,
                "ABACUS-image-link-*.json",
                SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumManifestCount + 1)
            .ToList();
        if (manifestFiles.Count == 0)
        {
            throw new InvalidDataException("画像保存先にABACUS-image-link-*.jsonがありません。");
        }

        if (manifestFiles.Count > MaximumManifestCount)
        {
            throw new InvalidDataException($"画像紐付けマニフェストは{MaximumManifestCount:N0}件以内にしてください。");
        }

        var rows = new List<AbacusImageLinkMatchRow>(manifestFiles.Count);
        var errors = new List<string>();
        foreach (var manifestPath in manifestFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifestFileName = Path.GetFileName(manifestPath);
            try
            {
                var manifest = await ReadManifestAsync(imageRoot, manifestPath, cancellationToken);
                rows.Add(MatchManifest(manifestFileName, manifest, vehicleExport.Rows));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               InvalidDataException or JsonException or ArgumentException or
                                               NotSupportedException)
            {
                rows.Add(new AbacusImageLinkMatchRow(
                    manifestFileName,
                    string.Empty,
                    "invalid",
                    "マニフェスト不正",
                    "不明",
                    string.Empty,
                    string.Empty,
                    0,
                    string.Empty,
                    string.Empty,
                    exception.Message));
                errors.Add($"{manifestFileName}: {exception.Message}");
            }
        }

        return new AbacusImageLinkMatchReport(
            imageRoot,
            vehicleRoot,
            rows.Count,
            rows.Count(row => row.Status == "matched"),
            rows.Count(row => row.Status == "review"),
            rows.Count(row => row.Status == "conflict"),
            rows.Count(row => row.Status == "not-found"),
            rows.Count(row => row.Status == "invalid"),
            rows,
            errors,
            DateTime.UtcNow);
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

    private static async Task<ManifestDocument> ReadManifestAsync(
        string imageRoot,
        string manifestPath,
        CancellationToken cancellationToken)
    {
        var manifestInfo = new FileInfo(manifestPath);
        if (!manifestInfo.Exists || manifestInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("マニフェストが存在しないか、リンクです。");
        }

        if (manifestInfo.Length <= 0 || manifestInfo.Length > MaximumManifestBytes)
        {
            throw new InvalidDataException($"マニフェストサイズが上限{MaximumManifestBytes:N0} bytes以内ではありません。");
        }

        await using var stream = new FileStream(
            manifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var manifest = await JsonSerializer.DeserializeAsync<ManifestDocument>(
            stream,
            JsonOptions,
            cancellationToken);
        if (manifest is null)
        {
            throw new InvalidDataException("マニフェストを読み取れません。");
        }

        ValidateManifest(manifest);
        var imagePath = Path.GetFullPath(Path.Combine(imageRoot, manifest.ImageFileName));
        if (!IsSameOrSubPath(imagePath, imageRoot) ||
            !string.Equals(Path.GetFileName(imagePath), manifest.ImageFileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("マニフェストの画像ファイル名が保存先フォルダー外を指しています。");
        }

        var imageInfo = new FileInfo(imagePath);
        if (!imageInfo.Exists || imageInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException($"画像ファイルが見つからないか、リンクです: {manifest.ImageFileName}");
        }

        if (imageInfo.Length != manifest.FileSize || imageInfo.Length > MaximumImageBytes)
        {
            throw new InvalidDataException($"画像ファイルのサイズがマニフェストと一致しません: {manifest.ImageFileName}");
        }

        await using var imageStream = new FileStream(
            imagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var imageSha256 = Convert.ToHexString(await SHA256.HashDataAsync(imageStream, cancellationToken));
        if (!string.Equals(imageSha256, manifest.ImageSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"画像のSHA-256がマニフェストと一致しません: {manifest.ImageFileName}");
        }

        return manifest;
    }

    private static void ValidateManifest(ManifestDocument manifest)
    {
        if (manifest.Version != 1 || !string.Equals(manifest.Status, "review-only", StringComparison.Ordinal))
        {
            throw new InvalidDataException("対応していない画像紐付けマニフェストです。");
        }

        if (!string.Equals(manifest.ContentType, "image/png", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(manifest.ImageFileName) ||
            Path.IsPathRooted(manifest.ImageFileName) ||
            manifest.ImageFileName.Contains(Path.DirectorySeparatorChar) ||
            manifest.ImageFileName.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new InvalidDataException("マニフェストの画像情報が不正です。");
        }

        if (manifest.FileSize <= 8 || manifest.PixelWidth <= 0 || manifest.PixelHeight <= 0 ||
            string.IsNullOrWhiteSpace(manifest.ImageSha256) ||
            manifest.ImageSha256.Length != 64 ||
            !manifest.ImageSha256.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("マニフェストの画像サイズ・寸法・SHA-256が不正です。");
        }

        var chassis = NormalizeIdentifier(manifest.ChassisNumber, "車台番号");
        var registration = NormalizeIdentifier(manifest.RegistrationNumber, "登録番号");
        if (string.IsNullOrEmpty(chassis) && string.IsNullOrEmpty(registration))
        {
            throw new InvalidDataException("マニフェストに車台番号または登録番号がありません。");
        }

        var expectedStrategy = string.IsNullOrEmpty(chassis) ? "registration" : "chassis";
        if (!string.Equals(manifest.MatchStrategy, expectedStrategy, StringComparison.Ordinal))
        {
            throw new InvalidDataException("マニフェストの照合方式と識別子が一致しません。");
        }

        _ = NormalizeCustomerName(manifest.CustomerName);
    }

    private static AbacusImageLinkMatchRow MatchManifest(
        string manifestFileName,
        ManifestDocument manifest,
        IReadOnlyList<AbacusVehicleExportRow> vehicleRows)
    {
        var chassis = NormalizeIdentifier(manifest.ChassisNumber, "車台番号");
        var registration = NormalizeIdentifier(manifest.RegistrationNumber, "登録番号");
        var customerName = NormalizeCustomerName(manifest.CustomerName) ?? string.Empty;
        var hasBothIdentifiers = !string.IsNullOrEmpty(chassis) && !string.IsNullOrEmpty(registration);
        var chassisMatches = string.IsNullOrEmpty(chassis)
            ? []
            : vehicleRows.Where(row => NormalizeIdentifier(row.ChassisNumber, "車台番号") == chassis).ToList();
        var registrationMatches = string.IsNullOrEmpty(registration)
            ? []
            : vehicleRows.Where(row => NormalizeIdentifier(row.RegistrationNumber, "登録番号") == registration).ToList();
        var candidates = hasBothIdentifiers
            ? chassisMatches.Count == 0 || registrationMatches.Count == 0
                ? []
                : IntersectByReference(chassisMatches, registrationMatches)
            : IntersectByReference(chassisMatches, registrationMatches);
        var customerMismatch = customerName.Length > 0 &&
                               candidates.Any(candidate =>
                                   NormalizeCustomerName(candidate.CustomerName) != customerName);
        var identifier = string.Join(
            " / ",
            new[]
            {
                string.IsNullOrEmpty(chassis) ? string.Empty : $"車台:{chassis}",
                string.IsNullOrEmpty(registration) ? string.Empty : $"登録:{registration}",
            }.Where(value => value.Length > 0));

        if (hasBothIdentifiers && chassisMatches.Count > 0 && registrationMatches.Count > 0 &&
            candidates.Count == 0)
        {
            return CreateRow(
                manifestFileName,
                manifest,
                "conflict",
                "識別子競合",
                identifier,
                0,
                [],
                "車台番号と登録番号が同じ車両行に一致しません。別車両・入力差異・登録番号再利用を確認してください。");
        }

        if (candidates.Count == 1)
        {
            var candidate = candidates[0];
            if (customerMismatch)
            {
                return CreateRow(
                    manifestFileName,
                    manifest,
                    "conflict",
                    "顧客名不一致",
                    identifier,
                    1,
                    candidates,
                    "識別子は一致しましたが、確認用顧客名が車両一覧の顧客名と一致しません。");
            }

            var weakChassis = !string.IsNullOrEmpty(chassis) &&
                              (chassis.All(char.IsDigit) || chassis.Length < 8);
            if (!hasBothIdentifiers && weakChassis)
            {
                return CreateRow(
                    manifestFileName,
                    manifest,
                    "review",
                    "短い車台番号・要確認",
                    identifier,
                    1,
                    candidates,
                    "車台番号が短い、または数字だけのため、登録番号または車両内容の追加確認が必要です。");
            }

            if (!hasBothIdentifiers && string.IsNullOrEmpty(chassis))
            {
                return CreateRow(
                    manifestFileName,
                    manifest,
                    "review",
                    "登録番号一致・要確認",
                    identifier,
                    1,
                    candidates,
                    "登録番号は一致しましたが、再利用の可能性があるため車両内容を確認してください。");
            }

            return CreateRow(
                manifestFileName,
                manifest,
                "matched",
                "一致",
                identifier,
                1,
                candidates,
                "車両一覧の同一行に識別子が一致しました。");
        }

        if (candidates.Count > 1 || chassisMatches.Count > 1 || registrationMatches.Count > 1)
        {
            var duplicateCandidates = candidates.Count > 0 ? candidates : chassisMatches.Concat(registrationMatches).Distinct().ToList();
            return CreateRow(
                manifestFileName,
                manifest,
                "conflict",
                "複数候補",
                identifier,
                duplicateCandidates.Count,
                duplicateCandidates,
                "同じ識別子に複数の車両行が一致しました。syaryou.csvとsyaryou2.csvの重複を含め、自動登録しません。");
        }

        if (chassisMatches.Count > 0 || registrationMatches.Count > 0)
        {
            var partialMatches = chassisMatches.Concat(registrationMatches).Distinct().ToList();
            return CreateRow(
                manifestFileName,
                manifest,
                "review",
                "識別子一部一致・要確認",
                identifier,
                partialMatches.Count,
                partialMatches,
                "入力された識別子の一部だけが一致しました。車両一覧の同じ行か確認してください。");
        }

        return CreateRow(
            manifestFileName,
            manifest,
            "not-found",
            "未一致",
            identifier,
            0,
            [],
            "車両一覧CSVに一致する識別子がありません。CSVの選択先と入力値を確認してください。");
    }

    private static AbacusImageLinkMatchRow CreateRow(
        string manifestFileName,
        ManifestDocument manifest,
        string status,
        string statusLabel,
        string identifier,
        int candidateCount,
        IReadOnlyList<AbacusVehicleExportRow> candidates,
        string reason)
    {
        var first = candidates.FirstOrDefault();
        return new AbacusImageLinkMatchRow(
            manifestFileName,
            manifest.ImageFileName,
            status,
            statusLabel,
            manifest.MatchStrategy == "chassis" ? "車台番号" : "登録番号",
            identifier,
            manifest.CustomerName ?? string.Empty,
            candidateCount,
            candidates.Count == 0 ? string.Empty : string.Join(" / ", candidates.Take(5).Select(DescribeVehicle)),
            first is null
                ? string.Empty
                : string.Join(" / ", candidates.Take(5).Select(candidate => $"{candidate.FileName} {candidate.RowNumber}行")),
            reason)
        {
            Candidates = candidates.ToArray(),
        };
    }

    private static List<AbacusVehicleExportRow> IntersectByReference(
        IReadOnlyList<AbacusVehicleExportRow> left,
        IReadOnlyList<AbacusVehicleExportRow> right)
    {
        if (left.Count == 0)
        {
            return right.ToList();
        }

        if (right.Count == 0)
        {
            return left.ToList();
        }

        return left.Intersect(right).ToList();
    }

    private static string DescribeVehicle(AbacusVehicleExportRow row)
    {
        var name = string.Join(
            " ",
            new[] { row.Maker, row.VehicleName, row.Model }
                .Select(value => NormalizeText(value))
                .Where(value => value.Length > 0));
        var customer = NormalizeText(row.CustomerName);
        return $"{customer} / {name}".Trim(' ', '/');
    }

    private static string NormalizeIdentifier(string? value, string label)
    {
        var normalized = (value ?? string.Empty).Trim().Normalize(NormalizationForm.FormKC);
        if (normalized.Length > MaximumIdentifierCharacters || normalized.Any(char.IsControl))
        {
            throw new InvalidDataException($"{label}が長すぎるか、制御文字を含んでいます。");
        }

        return string.Concat(
                NormalizeText(normalized)
                    .Where(character => !char.IsWhiteSpace(character) && character != '-'))
            .ToUpperInvariant();
    }

    private static string? NormalizeCustomerName(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().Normalize(NormalizationForm.FormKC);
        if (normalized.Length == 0)
        {
            return null;
        }

        if (normalized.Length > MaximumCustomerNameCharacters || normalized.Any(char.IsControl))
        {
            throw new InvalidDataException("顧客名が長すぎるか、制御文字を含んでいます。");
        }

        return NormalizeText(normalized);
    }

    private static string NormalizeText(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWasSpace = false;
        foreach (var character in value.Trim())
        {
            var normalized = character is '\u0004' or '\u000B' or '\u001D' ? ' ' : character;
            if (char.IsWhiteSpace(normalized) || normalized == '　')
            {
                if (!previousWasSpace)
                {
                    builder.Append(' ');
                    previousWasSpace = true;
                }
            }
            else
            {
                builder.Append(normalized);
                previousWasSpace = false;
            }
        }

        return builder.ToString();
    }

    private static bool IsSameOrSubPath(string candidate, string root) =>
        string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith($"{root}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private sealed record ManifestDocument(
        int Version,
        string Status,
        DateTime CreatedAtUtc,
        string ImageFileName,
        string ContentType,
        long FileSize,
        int PixelWidth,
        int PixelHeight,
        string ImageSha256,
        string MatchStrategy,
        string? ChassisNumber,
        string? RegistrationNumber,
        string? CustomerName,
        string? SourceFolderFingerprint,
        string? WorkspaceFolderFingerprint);
}
