using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using VehicleManagement.AbacusImport;

namespace VehicleManagement.Companion.Services;

public sealed record AbacusBulkImageCandidate(
    string CandidateId,
    string SourceRelativePath,
    string PackageImageFileName,
    string Status,
    string StatusLabel,
    string MatchStrategy,
    string Identifier,
    string CustomerName,
    string VehicleName,
    string ChassisNumber,
    string RegistrationNumber,
    long FileSize,
    int PixelWidth,
    int PixelHeight,
    string ImageSha256,
    string Reason);

public sealed record AbacusBulkImagePreparationResult(
    string SourceFolder,
    string VehicleExportFolder,
    string PackagePath,
    string ManifestPath,
    string ReportPath,
    string ManifestSha256,
    int SourceImageCount,
    int MatchedCount,
    int ReviewCount,
    int NotFoundCount,
    int RejectedCount,
    int ImageCount,
    IReadOnlyList<AbacusBulkImageCandidate> Candidates,
    IReadOnlyList<string> Warnings);

/// <summary>
/// ABACUSフォルダー内に既に存在する標準画像を一括で検査し、ファイル名に含まれる
/// 車台番号・登録番号を車両一覧CSVへ読み取り専用で照合します。
/// FileMakerのUCSコンテナを推測して書き換えることはせず、照合が一意な画像だけを
/// 既存の画像登録前パッケージ形式へまとめます。
/// </summary>
public sealed class AbacusBulkImagePreparationStore
{
    private const long MaximumImageBytes = 256L * 1024 * 1024;
    private const long MaximumTotalImageBytes = 1L * 1024 * 1024 * 1024;
    private const long MaximumPixels = 50_000_000;
    private const int MaximumSourceImages = 5_000;
    private const int MaximumManifestBytes = 4 * 1024 * 1024;
    private const int MaximumReportBytes = 16 * 1024 * 1024;
    private static readonly string[] SupportedExtensions = [".png", ".jpg", ".jpeg"];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    private readonly AbacusVehicleExportReader vehicleExportReader = new();

    public async Task<AbacusBulkImagePreparationResult> CreateAsync(
        string sourceFolder,
        string vehicleExportFolder,
        string destinationParent,
        CancellationToken cancellationToken = default)
    {
        var sourceRoot = ValidateFolder(sourceFolder, "ABACUSフォルダー");
        var vehicleRoot = ValidateFolder(vehicleExportFolder, "車両一覧CSVフォルダー");
        var destinationRoot = ValidateFolder(destinationParent, "一括画像パッケージ保存先");
        ValidateDestination(sourceRoot, vehicleRoot, destinationRoot);

        var vehicleExport = await vehicleExportReader.ReadAsync(vehicleRoot, cancellationToken);
        if (!vehicleExport.IsValid)
        {
            var details = string.Join(
                "\n",
                vehicleExport.Errors.Take(20).Select(error =>
                    $"{error.RowNumber?.ToString() ?? "ファイル"}: {error.Message}"));
            throw new InvalidDataException(
                $"車両一覧CSVの再検証に失敗しました。{(details.Length == 0 ? string.Empty : $"\n{details}")}");
        }

        var sourceFiles = EnumerateImageFiles(sourceRoot)
            .Take(MaximumSourceImages + 1)
            .ToList();
        if (sourceFiles.Count > MaximumSourceImages)
        {
            throw new InvalidDataException($"一括画像の件数が上限{MaximumSourceImages:N0}件を超えています。");
        }

        var candidates = new List<AbacusBulkImageCandidate>(sourceFiles.Count);
        var warnings = new List<string>();
        var accepted = new List<PreparedImage>();
        var totalBytes = 0L;
        for (var index = 0; index < sourceFiles.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePath = sourceFiles[index];
            var relativePath = Path.GetRelativePath(sourceRoot, sourcePath).Replace('\\', '/');
            try
            {
                var validated = await ValidateImageAsync(sourcePath, cancellationToken);
                totalBytes = checked(totalBytes + validated.FileSize);
                if (totalBytes > MaximumTotalImageBytes)
                {
                    throw new InvalidDataException(
                        $"一括画像の合計サイズが上限{MaximumTotalImageBytes:N0} bytesを超えています。");
                }

                var matches = FindVehicleMatches(relativePath, vehicleExport.Rows);
                var status = matches.Count switch
                {
                    1 => "matched",
                    0 => "not-found",
                    _ => "review",
                };
                var statusLabel = status switch
                {
                    "matched" => "一意に照合",
                    "review" => "要確認（複数候補）",
                    _ => "未照合",
                };
                var match = matches.Count == 1 ? matches[0] : null;
                var candidateId = CreateCandidateId(relativePath, validated.ImageSha256);
                var packageImagePath = match is null
                    ? string.Empty
                    : $"images/{candidateId}{Path.GetExtension(sourcePath).ToLowerInvariant()}";
                var reason = status switch
                {
                    "matched" => $"ファイル名から{(match!.UsedChassis ? "車台番号" : "登録番号")}を検出し、車両一覧の1行へ一致しました。",
                    "review" => $"ファイル名から{matches.Count:N0}件の車両候補が見つかりました。自動登録せず要確認として残しました。",
                    _ => "ファイル名に車台番号または登録番号が含まれていないため、自動照合しませんでした。",
                };
                var candidate = new AbacusBulkImageCandidate(
                    candidateId,
                    relativePath,
                    packageImagePath,
                    status,
                    statusLabel,
                    match is null ? "none" : match.UsedChassis ? "filename-chassis" : "filename-registration",
                    match?.Identifier ?? string.Empty,
                    match?.Row.CustomerName ?? string.Empty,
                    match?.Row.VehicleName ?? string.Empty,
                    match?.Row.ChassisNumber ?? string.Empty,
                    match?.Row.RegistrationNumber ?? string.Empty,
                    validated.FileSize,
                    validated.PixelWidth,
                    validated.PixelHeight,
                    validated.ImageSha256,
                    reason);
                candidates.Add(candidate);
                if (match is not null)
                {
                    accepted.Add(new PreparedImage(sourcePath, candidate));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               InvalidDataException or NotSupportedException)
            {
                var candidateId = CreateCandidateId(relativePath, index.ToString());
                candidates.Add(new AbacusBulkImageCandidate(
                    candidateId,
                    relativePath,
                    string.Empty,
                    "rejected",
                    "抽出不可",
                    "none",
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    0,
                    0,
                    0,
                    string.Empty,
                    exception.Message));
            }
        }

        AddContainerWarning(sourceRoot, sourceFiles.Count, warnings);
        var packagePath = CreateUniquePackageDirectory(destinationRoot);
        var imagesPath = Path.Combine(packagePath, "images");
        Directory.CreateDirectory(imagesPath);
        try
        {
            foreach (var prepared in accepted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destinationPath = Path.Combine(
                    packagePath,
                    prepared.Candidate.PackageImageFileName.Replace('/', Path.DirectorySeparatorChar));
                await CopyAndVerifyAsync(prepared.SourcePath, destinationPath, prepared.Candidate.ImageSha256, cancellationToken);
            }

            var manifest = new BulkPreviewManifest(
                1,
                "abacus-image-registration-preview",
                "preview-only",
                DateTime.UtcNow,
                new BulkPreviewSource(sourceRoot, vehicleRoot, "filename-identifier-bulk-match"),
                new BulkPreviewSummary(
                    accepted.Count,
                    candidates.Count(item => item.Status == "review"),
                    candidates.Count(item => item.Status == "not-found"),
                    candidates.Count(item => item.Status == "rejected"),
                    "ファイル名に含まれる識別子が車両一覧CSVの1行へ一意に一致した画像だけを登録前候補に含めています。"),
                accepted.Select(prepared => new BulkPreviewCandidate(
                    prepared.Candidate.CandidateId,
                    prepared.Candidate.PackageImageFileName,
                    prepared.Candidate.ImageSha256,
                    prepared.Candidate.CustomerName,
                    prepared.Candidate.VehicleName,
                    prepared.Candidate.ChassisNumber,
                    prepared.Candidate.RegistrationNumber)).ToArray());
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
            if (manifestBytes.Length > MaximumManifestBytes)
            {
                throw new InvalidDataException("一括画像マニフェストのサイズが上限を超えています。");
            }

            var manifestPath = Path.Combine(packagePath, "manifest.json");
            await WriteAtomicallyAsync(manifestPath, manifestBytes, cancellationToken);

            var report = new BulkReport(
                1,
                "abacus-bulk-image-preparation-report",
                "review-only",
                DateTime.UtcNow,
                sourceRoot,
                vehicleRoot,
                candidates);
            var reportBytes = JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions);
            if (reportBytes.Length > MaximumReportBytes)
            {
                throw new InvalidDataException("一括画像の照合レポートが大きすぎます。");
            }

            var reportPath = Path.Combine(packagePath, "image-batch-report.json");
            await WriteAtomicallyAsync(reportPath, reportBytes, cancellationToken);
            if (sourceFiles.Count == 0)
            {
                warnings.Add("ABACUSフォルダー内にPNG/JPEGファイルがありません。UCSコンテナの推測抽出は行わず、原本を変更しません。画像表示キャプチャまたは専用形式解析が必要です。");
            }

            if (candidates.Any(item => item.Status is "review" or "not-found" or "rejected"))
            {
                warnings.Add("要確認・未照合・抽出不可の画像は登録前候補へ含めていません。image-batch-report.jsonで一覧を確認し、必要な画像だけ既存の確認済み経路へ回してください。");
            }

            return new AbacusBulkImagePreparationResult(
                sourceRoot,
                vehicleRoot,
                packagePath,
                manifestPath,
                reportPath,
                Convert.ToHexString(SHA256.HashData(manifestBytes)),
                sourceFiles.Count,
                candidates.Count(item => item.Status == "matched"),
                candidates.Count(item => item.Status == "review"),
                candidates.Count(item => item.Status == "not-found"),
                candidates.Count(item => item.Status == "rejected"),
                accepted.Count,
                candidates,
                warnings);
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

    private static IEnumerable<string> EnumerateImageFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> directories;
            IEnumerable<string> files;
            try
            {
                directories = Directory.EnumerateDirectories(current);
                files = Directory.EnumerateFiles(current);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var directory in directories)
            {
                try
                {
                    if (!new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        pending.Push(directory);
                    }
                }
                catch (IOException)
                {
                    // 読めない枝は安全側に無視します。
                }
                catch (UnauthorizedAccessException)
                {
                    // 読めない枝は安全側に無視します。
                }
            }

            foreach (var file in files)
            {
                var extension = Path.GetExtension(file);
                if (!SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var include = false;
                try
                {
                    var info = new FileInfo(file);
                    include = info.Exists && !info.Attributes.HasFlag(FileAttributes.ReparsePoint);
                }
                catch (IOException)
                {
                    // 読めないファイルは候補にしません。
                }
                catch (UnauthorizedAccessException)
                {
                    // 読めないファイルは候補にしません。
                }

                if (include)
                {
                    yield return file;
                }
            }
        }
    }

    private static async Task<ValidatedImage> ValidateImageAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            info.Length <= 0 || info.Length > MaximumImageBytes)
        {
            throw new InvalidDataException($"画像サイズが許容範囲外です: {Path.GetFileName(path)}");
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count != 1)
        {
            throw new InvalidDataException($"画像を1枚としてデコードできません: {Path.GetFileName(path)}");
        }

        var frame = decoder.Frames[0];
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 ||
            (long)frame.PixelWidth * frame.PixelHeight > MaximumPixels)
        {
            throw new InvalidDataException($"画像の画素数が許容範囲を超えています: {Path.GetFileName(path)}");
        }

        stream.Position = 0;
        var imageSha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        var afterLength = new FileInfo(path).Length;
        if (afterLength != info.Length)
        {
            throw new InvalidDataException($"画像が読み取り中に変更されました: {Path.GetFileName(path)}");
        }

        return new ValidatedImage(info.Length, frame.PixelWidth, frame.PixelHeight, imageSha256);
    }

    private static List<VehicleMatch> FindVehicleMatches(
        string relativePath,
        IReadOnlyList<AbacusVehicleExportRow> rows)
    {
        var searchable = Normalize(relativePath);
        var matches = new List<VehicleMatch>();
        foreach (var row in rows)
        {
            var chassis = Normalize(row.ChassisNumber);
            var registration = Normalize(row.RegistrationNumber);
            var chassisHit = IsStrongChassis(chassis) && searchable.Contains(chassis, StringComparison.Ordinal);
            var registrationHit = IsStrongIdentifier(registration) && searchable.Contains(registration, StringComparison.Ordinal);
            if (!chassisHit && !registrationHit)
            {
                continue;
            }

            var identifier = chassisHit ? row.ChassisNumber.Trim() : row.RegistrationNumber.Trim();
            matches.Add(new VehicleMatch(row, identifier, chassisHit));
        }

        return matches
            .GroupBy(match => $"{match.Row.FileName}:{match.Row.RowNumber}", StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
    }

    private static bool IsStrongChassis(string normalized) =>
        normalized.Length >= 5 && normalized.Any(char.IsLetter);

    private static bool IsStrongIdentifier(string normalized) => normalized.Length >= 4;

    private static string Normalize(string value) =>
        string.Concat((value ?? string.Empty).Normalize(NormalizationForm.FormKC)
            .Where(char.IsLetterOrDigit))
        .ToUpperInvariant();

    private static string CreateCandidateId(string relativePath, string value) =>
        $"bulk-image-{Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes($"{relativePath}\n{value}"))).ToLowerInvariant()[..24]}";

    private static void AddContainerWarning(string sourceRoot, int sourceImageCount, ICollection<string> warnings)
    {
        var containerFiles = Directory.EnumerateFiles(sourceRoot, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetExtension(path).Equals(".ucs", StringComparison.OrdinalIgnoreCase) ||
                           Path.GetExtension(path).Equals(".fp5", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (containerFiles.Length > 0 && sourceImageCount == 0)
        {
            warnings.Add("ABACUSのUCS/FP5コンテナはFileMaker内部形式のため、一括処理では標準画像ファイルだけを対象にしました。原本を推測分割・書換えせず、画像がない場合は手動表示キャプチャまたは専用形式解析へ分離しています。");
        }
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

    private static void ValidateDestination(string sourceRoot, string vehicleRoot, string destinationRoot)
    {
        if (IsSameOrSubPath(destinationRoot, sourceRoot) || IsSameOrSubPath(destinationRoot, vehicleRoot))
        {
            throw new InvalidDataException("一括画像パッケージはABACUSフォルダーまたは車両一覧CSVフォルダーの外側へ保存してください。");
        }
    }

    private static string CreateUniquePackageDirectory(string destinationRoot)
    {
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            var suffix = attempt == 0 ? string.Empty : $"-{attempt}";
            var name = $"ABACUS-Bulk-Image-Preparation-{DateTime.Now:yyyyMMdd-HHmmss}{suffix}";
            var path = Path.Combine(destinationRoot, name);
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
                return path;
            }
        }

        throw new IOException("一括画像パッケージの保存先を作成できませんでした。");
    }

    private static async Task CopyAndVerifyAsync(
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
            await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1024 * 1024];
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            await destination.FlushAsync(cancellationToken);
            var actual = Convert.ToHexString(hash.GetHashAndReset());
            if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"画像のSHA-256が一致しません: {Path.GetFileName(sourcePath)}");
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

    private static async Task WriteAtomicallyAsync(
        string path,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var temporaryPath = path + ".partial";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken);
            File.Move(temporaryPath, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static bool IsSameOrSubPath(string candidatePath, string rootPath) =>
        candidatePath.Equals(rootPath, StringComparison.OrdinalIgnoreCase) ||
        candidatePath.StartsWith($"{rootPath}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private sealed record ValidatedImage(long FileSize, int PixelWidth, int PixelHeight, string ImageSha256);

    private sealed record VehicleMatch(AbacusVehicleExportRow Row, string Identifier, bool UsedChassis);

    private sealed record PreparedImage(string SourcePath, AbacusBulkImageCandidate Candidate);

    private sealed record BulkPreviewManifest(
        int Version,
        string Kind,
        string Status,
        DateTime CreatedAtUtc,
        BulkPreviewSource Source,
        BulkPreviewSummary Summary,
        IReadOnlyList<BulkPreviewCandidate> Candidates);

    private sealed record BulkPreviewSource(string SourceFolder, string VehicleExportFolder, string Strategy);

    private sealed record BulkPreviewSummary(
        int ImageCount,
        int ReviewCount,
        int NotFoundCount,
        int RejectedCount,
        string Note);

    private sealed record BulkPreviewCandidate(
        string CandidateId,
        string PackageImageFileName,
        string ImageSha256,
        string CustomerName,
        string VehicleName,
        string ChassisNumber,
        string RegistrationNumber);

    private sealed record BulkReport(
        int Version,
        string Kind,
        string Status,
        DateTime CreatedAtUtc,
        string SourceFolder,
        string VehicleExportFolder,
        IReadOnlyList<AbacusBulkImageCandidate> Candidates);
}
