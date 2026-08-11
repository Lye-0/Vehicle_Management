using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VehicleManagement.AbacusImport;

namespace VehicleManagement.Companion.Services;

public sealed record AbacusImageRegistrationPreviewCandidate(
    string CandidateId,
    string ApprovalFileName,
    string ApprovalSha256,
    string ImageFileName,
    string ImageSha256,
    string PackageImageFileName,
    string CustomerName,
    string VehicleCsvFileName,
    int VehicleCsvRowNumber,
    string Maker,
    string VehicleName,
    string Model,
    string ModelYear,
    string ChassisNumber,
    string RegistrationNumber,
    string Displacement,
    string Transmission,
    string BodyColor,
    string Mileage,
    string InspectionDate,
    string InspectionRecord);

public sealed record AbacusImageRegistrationPreviewResult(
    string PackagePath,
    string ManifestPath,
    string ManifestSha256,
    int CandidateCount,
    int ImageCount,
    IReadOnlyList<AbacusImageRegistrationPreviewCandidate> Candidates);

/// <summary>
/// 人が確認済みとした画像・車両行を、登録前の永続パッケージへまとめます。
/// この段階ではDB/APIへの登録、ObjectStorageへのアップロード、ABACUSフォルダーへの書き込みを行いません。
/// </summary>
public sealed class AbacusImageRegistrationPreviewStore
{
    private const long MaximumApprovalBytes = 1L * 1024 * 1024;
    private const long MaximumManifestBytes = 1L * 1024 * 1024;
    private const long MaximumImageBytes = 256L * 1024 * 1024;
    private const long MaximumPackageBytes = 1L * 1024 * 1024 * 1024;
    private const int MaximumApprovalCount = 1_000;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private static readonly JsonSerializerOptions PreviewJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly AbacusVehicleExportReader vehicleExportReader = new();

    public async Task<AbacusImageRegistrationPreviewResult> CreateAsync(
        string approvalFolder,
        string vehicleExportFolder,
        string destinationParent,
        CancellationToken cancellationToken = default)
    {
        var approvalRoot = ValidateFolder(approvalFolder, "確認済み証跡保存先");
        var vehicleRoot = ValidateFolder(vehicleExportFolder, "車両一覧CSVフォルダー");
        var destinationRoot = ValidateFolder(destinationParent, "登録前パッケージ保存先");
        ValidateDestination(approvalRoot, vehicleRoot, destinationRoot);

        var approvalPaths = Directory.EnumerateFiles(
                approvalRoot,
                "ABACUS-image-approval-*.json",
                SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumApprovalCount + 1)
            .ToList();
        if (approvalPaths.Count == 0)
        {
            throw new InvalidDataException("確認済み証跡フォルダーにABACUS-image-approval-*.jsonがありません。");
        }

        if (approvalPaths.Count > MaximumApprovalCount)
        {
            throw new InvalidDataException($"確認済み証跡は{MaximumApprovalCount:N0}件以内にしてください。");
        }

        var vehicleExport = await vehicleExportReader.ReadAsync(vehicleRoot, cancellationToken);
        if (!vehicleExport.IsValid)
        {
            var details = string.Join(
                "\n",
                vehicleExport.Errors.Take(20).Select(error =>
                    $"{error.RowNumber?.ToString() ?? "ファイル"}: {error.Message}"));
            throw new InvalidDataException($"車両一覧CSVの再検証に失敗しました。{(details.Length == 0 ? string.Empty : $"\n{details}")}");
        }

        var candidates = new List<AbacusImageRegistrationPreviewCandidate>(approvalPaths.Count);
        var usedManifestKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalImageBytes = 0L;
        foreach (var approvalPath in approvalPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var approvalFileName = Path.GetFileName(approvalPath);
            var approvalRead = await ReadApprovalAsync(approvalPath, cancellationToken);
            var approval = approvalRead.Document;
            var approvalSha256 = approvalRead.Sha256;
            var manifestKey = $"{approval.ManifestFileName}:{approval.ManifestSha256}";
            if (!usedManifestKeys.Add(manifestKey))
            {
                throw new InvalidDataException($"同じ画像マニフェストを参照する証跡が複数あります: {approval.ManifestFileName}");
            }

            var manifestPath = Path.Combine(approvalRoot, approval.ManifestFileName);
            var manifest = await ReadManifestAsync(approvalRoot, manifestPath, cancellationToken);
            var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
            var actualManifestSha256 = Convert.ToHexString(SHA256.HashData(manifestBytes));
            if (!string.Equals(actualManifestSha256, approval.ManifestSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"マニフェストのSHA-256が確認済み証跡と一致しません: {approval.ManifestFileName}");
            }

            if (!string.Equals(manifest.ImageFileName, approval.ImageFileName, StringComparison.Ordinal) ||
                !string.Equals(manifest.ImageSha256, approval.ImageSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"画像情報が確認済み証跡と一致しません: {approvalFileName}");
            }

            var imagePath = Path.Combine(approvalRoot, approval.ImageFileName);
            var imageInfo = ValidateRegularFile(imagePath, "承認済み画像", MaximumImageBytes);
            var imageSha256 = await CalculateSha256Async(imagePath, cancellationToken);
            if (imageInfo.Length != manifest.FileSize ||
                !string.Equals(imageSha256, approval.ImageSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"承認済み画像が変更されています: {approval.ImageFileName}");
            }

            totalImageBytes = checked(totalImageBytes + imageInfo.Length);
            if (totalImageBytes > MaximumPackageBytes)
            {
                throw new InvalidDataException($"画像合計サイズが上限{MaximumPackageBytes:N0} bytesを超えています。");
            }

            var currentCandidate = vehicleExport.Rows.SingleOrDefault(row =>
                string.Equals(row.FileName, approval.VehicleCsvFileName, StringComparison.Ordinal) &&
                row.RowNumber == approval.VehicleCsvRowNumber);
            if (currentCandidate is null)
            {
                throw new InvalidDataException(
                    $"確認済み証跡が参照する車両一覧行がありません: {approval.VehicleCsvFileName} {approval.VehicleCsvRowNumber}行");
            }

            var vehicleFile = vehicleExport.Files.SingleOrDefault(file =>
                string.Equals(file.FileName, approval.VehicleCsvFileName, StringComparison.Ordinal));
            if (vehicleFile is null ||
                !string.Equals(vehicleFile.Sha256, approval.VehicleCsvSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"車両一覧CSVが確認済み証跡作成後に変更されています: {approval.VehicleCsvFileName}");
            }

            ValidateApprovalCandidate(approval, currentCandidate);
            var candidateId = CreateCandidateId(approvalFileName, approval.ManifestSha256, currentCandidate);
            candidates.Add(new AbacusImageRegistrationPreviewCandidate(
                candidateId,
                approvalFileName,
                approvalSha256,
                approval.ImageFileName,
                imageSha256,
                $"images/{candidateId}.png",
                currentCandidate.CustomerName,
                currentCandidate.FileName,
                currentCandidate.RowNumber,
                currentCandidate.Maker,
                currentCandidate.VehicleName,
                currentCandidate.Model,
                currentCandidate.ModelYear,
                currentCandidate.ChassisNumber,
                currentCandidate.RegistrationNumber,
                currentCandidate.Displacement,
                currentCandidate.Transmission,
                currentCandidate.BodyColor,
                currentCandidate.Mileage,
                currentCandidate.InspectionDate,
                currentCandidate.InspectionRecord));
        }

        var packagePath = CreateUniquePackageDirectory(destinationRoot);
        var imagesPath = Path.Combine(packagePath, "images");
        Directory.CreateDirectory(imagesPath);
        try
        {
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourcePath = Path.Combine(approvalRoot, candidate.ImageFileName);
                var destinationPath = Path.Combine(packagePath, candidate.PackageImageFileName.Replace('/', Path.DirectorySeparatorChar));
                var destinationDirectory = Path.GetDirectoryName(destinationPath);
                if (string.IsNullOrWhiteSpace(destinationDirectory) ||
                    !IsSameOrSubPath(destinationDirectory, packagePath))
                {
                    throw new InvalidDataException("登録前パッケージの画像パスがパッケージ外を指しています。");
                }

                await CopyAndVerifyImageAsync(sourcePath, destinationPath, candidate.ImageSha256, cancellationToken);
            }

            var previewManifest = new PreviewManifest(
                Version: 1,
                Kind: "abacus-image-registration-preview",
                Status: "preview-only",
                CreatedAtUtc: DateTime.UtcNow,
                Source: new PreviewSource(
                    approvalRoot,
                    vehicleRoot,
                    candidates.Select(candidate => candidate.ApprovalFileName).ToArray()),
                Summary: new PreviewSummary(
                    candidates.Count,
                    candidates.Count,
                    "顧客・車両・画像は確認済み証跡に基づく登録前候補です。顧客ID・車両IDの解決と登録は次段階で行います。"),
                Candidates: candidates);
            var manifestPath = Path.Combine(packagePath, "manifest.json");
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(previewManifest, PreviewJsonOptions);
            await WriteAtomicallyAndVerifyAsync(manifestPath, manifestBytes, previewManifest, cancellationToken);
            return new AbacusImageRegistrationPreviewResult(
                packagePath,
                manifestPath,
                Convert.ToHexString(SHA256.HashData(manifestBytes)),
                candidates.Count,
                candidates.Count,
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

    private static void ValidateDestination(string approvalRoot, string vehicleRoot, string destinationRoot)
    {
        if ((IsSameOrSubPath(destinationRoot, approvalRoot) &&
             !string.Equals(destinationRoot, approvalRoot, StringComparison.OrdinalIgnoreCase)) ||
            IsSameOrSubPath(destinationRoot, vehicleRoot))
        {
            throw new InvalidDataException("登録前パッケージは確認済み証跡のサブフォルダーまたは車両一覧CSVの内部には作成できません。");
        }
    }

    private static async Task<ApprovalRead> ReadApprovalAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var info = ValidateRegularFile(path, "確認済み証跡", MaximumApprovalBytes);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (bytes.Length != info.Length)
        {
            throw new InvalidDataException($"確認済み証跡が読み取り中に変更されました: {Path.GetFileName(path)}");
        }

        var approval = JsonSerializer.Deserialize<ApprovalDocument>(bytes, JsonOptions);
        if (approval is null ||
            approval.Version != 1 ||
            !string.Equals(approval.Status, "human-reviewed", StringComparison.Ordinal) ||
            !IsSafeFileName(approval.ManifestFileName) ||
            string.IsNullOrWhiteSpace(approval.ManifestSha256) ||
            approval.ManifestSha256.Length != 64 ||
            !approval.ManifestSha256.All(Uri.IsHexDigit) ||
            string.IsNullOrWhiteSpace(approval.ImageFileName) ||
            !IsSafeFileName(approval.ImageFileName) ||
            string.IsNullOrWhiteSpace(approval.ImageSha256) ||
            approval.ImageSha256.Length != 64 ||
            !approval.ImageSha256.All(Uri.IsHexDigit) ||
            approval.VehicleCsvFileName is not ("syaryou.csv" or "syaryou2.csv") ||
            approval.VehicleCsvRowNumber <= 0 ||
            string.IsNullOrWhiteSpace(approval.VehicleCsvSha256) ||
            approval.VehicleCsvSha256.Length != 64 ||
            !approval.VehicleCsvSha256.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException($"確認済み証跡の形式が不正です: {Path.GetFileName(path)}");
        }

        return new ApprovalRead(
            approval,
            Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static async Task<ManifestDocument> ReadManifestAsync(
        string imageRoot,
        string path,
        CancellationToken cancellationToken)
    {
        var info = ValidateRegularFile(path, "画像マニフェスト", MaximumManifestBytes);
        if (!string.Equals(Path.GetDirectoryName(path), imageRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("画像マニフェストが保存先フォルダー直下にありません。");
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (bytes.Length != info.Length)
        {
            throw new InvalidDataException("画像マニフェストが読み取り中に変更されました。");
        }

        var manifest = JsonSerializer.Deserialize<ManifestDocument>(bytes, JsonOptions);
        if (manifest is null ||
            manifest.Version != 1 ||
            !string.Equals(manifest.Status, "review-only", StringComparison.Ordinal) ||
            !IsSafeFileName(manifest.ImageFileName) ||
            manifest.FileSize <= 0 ||
            string.IsNullOrWhiteSpace(manifest.ImageSha256) ||
            manifest.ImageSha256.Length != 64 ||
            !manifest.ImageSha256.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("画像マニフェストの形式が不正です。");
        }

        return manifest;
    }

    private static void ValidateApprovalCandidate(
        ApprovalDocument approval,
        AbacusVehicleExportRow candidate)
    {
        var mismatches = new List<string>();
        AddMismatch("顧客名", approval.CsvCustomerName, candidate.CustomerName, mismatches);
        AddMismatch("メーカー", approval.Maker, candidate.Maker, mismatches);
        AddMismatch("車名", approval.VehicleName, candidate.VehicleName, mismatches);
        AddMismatch("型式", approval.Model, candidate.Model, mismatches);
        AddMismatch("年式", approval.ModelYear, candidate.ModelYear, mismatches);
        AddMismatch("車台番号", approval.ChassisNumber, candidate.ChassisNumber, mismatches);
        AddMismatch("登録番号", approval.RegistrationNumber, candidate.RegistrationNumber, mismatches);
        AddMismatch("排気量", approval.Displacement, candidate.Displacement, mismatches);
        AddMismatch("ミッション", approval.Transmission, candidate.Transmission, mismatches);
        AddMismatch("車体色", approval.BodyColor, candidate.BodyColor, mismatches);
        if (mismatches.Count > 0)
        {
            throw new InvalidDataException(
                $"確認済み証跡の車両内容と現在のCSV行が一致しません: {string.Join("、", mismatches)}");
        }

        static void AddMismatch(string label, string expected, string actual, ICollection<string> mismatches)
        {
            if (!string.Equals(expected ?? string.Empty, actual ?? string.Empty, StringComparison.Ordinal))
            {
                mismatches.Add(label);
            }
        }
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
            await using (var source = new FileStream(
                       sourcePath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       1024 * 1024,
                       FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       1024 * 1024,
                       FileOptions.Asynchronous | FileOptions.SequentialScan))
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
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }
    }

    private static async Task WriteAtomicallyAndVerifyAsync(
        string path,
        byte[] bytes,
        PreviewManifest expected,
        CancellationToken cancellationToken)
    {
        var temporaryPath = path + ".partial";
        try
        {
            await using (var output = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(bytes, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            await using (var verification = new FileStream(
                       temporaryPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       64 * 1024,
                       FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var reloaded = await JsonSerializer.DeserializeAsync<PreviewManifest>(
                    verification,
                    PreviewJsonOptions,
                    cancellationToken);
                if (reloaded is null ||
                    reloaded.Version != expected.Version ||
                    !string.Equals(reloaded.Kind, expected.Kind, StringComparison.Ordinal) ||
                    !string.Equals(reloaded.Status, expected.Status, StringComparison.Ordinal) ||
                    reloaded.Candidates.Count != expected.Candidates.Count ||
                    reloaded.Summary.CandidateCount != expected.Summary.CandidateCount)
                {
                    throw new InvalidDataException("登録前パッケージのマニフェスト再読込検証に失敗しました。");
                }
            }

            File.Move(temporaryPath, path, overwrite: false);
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }
    }

    private static FileInfo ValidateRegularFile(string path, string label, long maximumBytes)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException($"{label}が存在しないか、リンクです。");
        }

        if (file.Length <= 0 || file.Length > maximumBytes)
        {
            throw new InvalidDataException($"{label}のサイズが上限以内ではありません。");
        }

        return file;
    }

    private static async Task<string> CalculateSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static string CreateCandidateId(
        string approvalFileName,
        string manifestSha256,
        AbacusVehicleExportRow candidate)
    {
        var identity = Encoding.UTF8.GetBytes(
            $"{approvalFileName}\n{manifestSha256}\n{candidate.FileName}\n{candidate.RowNumber}");
        var hash = Convert.ToHexString(SHA256.HashData(identity));
        return $"candidate-{hash[..16]}".ToLowerInvariant();
    }

    private static string CreateUniquePackageDirectory(string destinationRoot)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var suffix = attempt == 0 ? string.Empty : $"-{attempt:D2}";
            var path = Path.Combine(
                destinationRoot,
                $"ABACUS-Image-Registration-Preview-{DateTime.Now:yyyyMMdd-HHmmss}{suffix}");
            if (Directory.Exists(path) || File.Exists(path))
            {
                continue;
            }

            Directory.CreateDirectory(path);
            return path;
        }

        throw new IOException("登録前パッケージの一意なフォルダー名を作成できませんでした。");
    }

    private static bool IsSafeFileName(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal) &&
        !Path.IsPathRooted(value) &&
        !value.Contains(Path.DirectorySeparatorChar) &&
        !value.Contains(Path.AltDirectorySeparatorChar) &&
        !value.Any(char.IsControl);

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

    private sealed record ApprovalDocument(
        int Version,
        string Status,
        DateTime CreatedAtUtc,
        DateTime ReviewedAtUtc,
        string Confirmation,
        string ManifestFileName,
        string ManifestSha256,
        string ImageFileName,
        string ImageSha256,
        string MatchStatus,
        string MatchLabel,
        string MatchStrategy,
        string Identifier,
        string ManifestCustomerName,
        string VehicleCsvFileName,
        string VehicleCsvSha256,
        int VehicleCsvRowNumber,
        string CsvCustomerName,
        string Maker,
        string VehicleName,
        string ModelYear,
        string Displacement,
        string Transmission,
        string BodyColor,
        string Model,
        string ChassisNumber,
        string RegistrationNumber);

    private sealed record ApprovalRead(
        ApprovalDocument Document,
        string Sha256);

    private sealed record PreviewManifest(
        int Version,
        string Kind,
        string Status,
        DateTime CreatedAtUtc,
        PreviewSource Source,
        PreviewSummary Summary,
        IReadOnlyList<AbacusImageRegistrationPreviewCandidate> Candidates);

    private sealed record PreviewSource(
        string ApprovalFolder,
        string VehicleExportFolder,
        IReadOnlyList<string> ApprovalFiles);

    private sealed record PreviewSummary(
        int CandidateCount,
        int ImageCount,
        string Note);
}
