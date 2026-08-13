using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using VehicleManagement.AbacusImport;

namespace VehicleManagement.Companion.Services;

public sealed record AbacusImageLinkApprovalResult(
    string FilePath,
    string Sha256,
    string ManifestFileName,
    string ImageFileName,
    string VehicleCsvFileName,
    int VehicleCsvRowNumber);

/// <summary>
/// 画像と車両一覧の候補を、人が目視確認した事実だけを記録するストアです。
/// このクラスは顧客・車両の登録やAPIへの送信を行いません。
/// </summary>
public sealed class AbacusImageLinkApprovalStore
{
    private const long MaximumManifestBytes = 1L * 1024 * 1024;
    private const long MaximumImageBytes = 256L * 1024 * 1024;
    private const long MaximumApprovalBytes = 1L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly AbacusImageLinkMatcher matcher = new();
    private readonly AbacusVehicleExportReader vehicleExportReader = new();

    public async Task<AbacusImageLinkApprovalResult> CreateAsync(
        string imageFolder,
        string vehicleExportFolder,
        string manifestFileName,
        CancellationToken cancellationToken = default)
    {
        var imageRoot = ValidateFolder(imageFolder, "画像保存先");
        var vehicleRoot = ValidateFolder(vehicleExportFolder, "車両一覧CSVフォルダー");
        if (string.IsNullOrWhiteSpace(manifestFileName) ||
            !string.Equals(Path.GetFileName(manifestFileName), manifestFileName, StringComparison.Ordinal) ||
            !manifestFileName.StartsWith("ABACUS-image-link-", StringComparison.Ordinal) ||
            !manifestFileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("確認対象のマニフェスト名が不正です。");
        }

        // 画面に表示されていた候補を信用せず、承認直前に全検証をやり直します。
        var report = await matcher.MatchAsync(imageRoot, vehicleRoot, cancellationToken);
        var row = report.Rows.SingleOrDefault(candidate =>
            string.Equals(candidate.ManifestFileName, manifestFileName, StringComparison.Ordinal));
        if (row is null)
        {
            throw new InvalidDataException("確認対象のマニフェストが照合結果にありません。もう一度照合してください。");
        }

        if (row.Status is not ("matched" or "review") ||
            row.CandidateCount != 1 ||
            row.Candidates.Count != 1)
        {
            throw new InvalidDataException(
                "確認済みにできるのは、同一の車両一覧行へ一意に対応する「一致」または「要確認」候補だけです。");
        }

        var candidate = row.Candidates[0];
        var manifestPath = Path.Combine(imageRoot, manifestFileName);
        var manifest = await ReadManifestAsync(imageRoot, manifestPath, cancellationToken);
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
        var manifestSha256 = Convert.ToHexString(SHA256.HashData(manifestBytes));
        var imagePath = Path.Combine(imageRoot, manifest.ImageFileName);
        var imageInfo = ValidateRegularFile(imagePath, "画像ファイル", MaximumImageBytes);
        if (imageInfo.Length != manifest.FileSize)
        {
            throw new InvalidDataException("承認前に画像サイズがマニフェストと一致しなくなりました。再照合してください。");
        }

        var imageSha256 = await CalculateSha256Async(imagePath, cancellationToken);
        if (!string.Equals(imageSha256, manifest.ImageSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("承認前に画像のSHA-256がマニフェストと一致しなくなりました。再照合してください。");
        }

        var vehicleExport = await vehicleExportReader.ReadAsync(vehicleRoot, cancellationToken);
        if (!vehicleExport.IsValid)
        {
            throw new InvalidDataException("承認前の車両一覧CSV再検証に失敗しました。CSVを確認して再照合してください。");
        }

        var currentCandidate = vehicleExport.Rows.SingleOrDefault(item =>
            string.Equals(item.FileName, candidate.FileName, StringComparison.Ordinal) &&
            item.RowNumber == candidate.RowNumber);
        if (currentCandidate is null || !Equals(currentCandidate, candidate))
        {
            throw new InvalidDataException("承認前に対象の車両一覧行が変更されました。再照合してください。");
        }

        var vehicleFile = vehicleExport.Files.SingleOrDefault(file =>
            string.Equals(file.FileName, candidate.FileName, StringComparison.Ordinal));
        if (vehicleFile is null || string.IsNullOrWhiteSpace(vehicleFile.Sha256))
        {
            throw new InvalidDataException("承認対象CSVのSHA-256を確認できません。再照合してください。");
        }

        await RejectDuplicateApprovalAsync(imageRoot, manifestFileName, manifestSha256, cancellationToken);

        var approval = new ApprovalDocument(
            Version: 1,
            Status: "human-reviewed",
            CreatedAtUtc: DateTime.UtcNow,
            ReviewedAtUtc: DateTime.UtcNow,
            Confirmation: "利用者がABACUS表示画像と車両内容を目視確認し、同一車両として承認しました。",
            ManifestFileName: manifestFileName,
            ManifestSha256: manifestSha256,
            ImageFileName: manifest.ImageFileName,
            ImageSha256: imageSha256,
            MatchStatus: row.Status,
            MatchLabel: row.StatusLabel,
            MatchStrategy: row.MatchStrategy,
            Identifier: row.Identifier,
            ManifestCustomerName: manifest.CustomerName ?? string.Empty,
            VehicleCsvFileName: candidate.FileName,
            VehicleCsvSha256: vehicleFile.Sha256,
            VehicleCsvRowNumber: candidate.RowNumber,
            CsvCustomerName: candidate.CustomerName,
            Maker: candidate.Maker,
            VehicleName: candidate.VehicleName,
            ModelYear: candidate.ModelYear,
            Displacement: candidate.Displacement,
            Transmission: candidate.Transmission,
            BodyColor: candidate.BodyColor,
            Model: candidate.Model,
            ChassisNumber: candidate.ChassisNumber,
            RegistrationNumber: candidate.RegistrationNumber);

        var json = JsonSerializer.SerializeToUtf8Bytes(approval, new JsonSerializerOptions
        {
            WriteIndented = true,
        });
        var outputPath = CreateUniqueApprovalPath(imageRoot);
        var temporaryPath = outputPath + ".partial";
        try
        {
            await using (var output = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       16 * 1024,
                       FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await output.WriteAsync(json, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            await using (var verification = new FileStream(
                       temporaryPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       16 * 1024,
                       FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var reloaded = await JsonSerializer.DeserializeAsync<ApprovalDocument>(
                    verification,
                    JsonOptions,
                    cancellationToken);
                if (reloaded is null ||
                    reloaded.Version != approval.Version ||
                    !string.Equals(reloaded.Status, approval.Status, StringComparison.Ordinal) ||
                    !string.Equals(reloaded.ManifestFileName, approval.ManifestFileName, StringComparison.Ordinal) ||
                    !string.Equals(reloaded.ManifestSha256, approval.ManifestSha256, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(reloaded.ImageSha256, approval.ImageSha256, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(reloaded.VehicleCsvFileName, approval.VehicleCsvFileName, StringComparison.Ordinal) ||
                    reloaded.VehicleCsvRowNumber != approval.VehicleCsvRowNumber)
                {
                    throw new InvalidDataException("確認済み証跡の再読込検証に失敗しました。");
                }
            }

            File.Move(temporaryPath, outputPath, overwrite: false);
            return new AbacusImageLinkApprovalResult(
                outputPath,
                Convert.ToHexString(SHA256.HashData(json)),
                approval.ManifestFileName,
                approval.ImageFileName,
                approval.VehicleCsvFileName,
                approval.VehicleCsvRowNumber);
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

    private static async Task<ManifestDocument> ReadManifestAsync(
        string imageRoot,
        string manifestPath,
        CancellationToken cancellationToken)
    {
        var manifestInfo = ValidateRegularFile(manifestPath, "画像マニフェスト", MaximumManifestBytes);
        if (!string.Equals(Path.GetDirectoryName(manifestPath), imageRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("画像マニフェストが保存先フォルダー直下にありません。");
        }

        var bytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
        if (bytes.Length != manifestInfo.Length)
        {
            throw new InvalidDataException("画像マニフェストが読み取り中に変更されました。");
        }

        var manifest = JsonSerializer.Deserialize<ManifestDocument>(bytes, JsonOptions);
        if (manifest is null ||
            manifest.Version != 1 ||
            !string.Equals(manifest.Status, "review-only", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(manifest.ImageFileName) ||
            !string.Equals(Path.GetFileName(manifest.ImageFileName), manifest.ImageFileName, StringComparison.Ordinal) ||
            manifest.ImageFileName.Contains(Path.DirectorySeparatorChar) ||
            manifest.ImageFileName.Contains(Path.AltDirectorySeparatorChar) ||
            string.IsNullOrWhiteSpace(manifest.ImageSha256) ||
            manifest.ImageSha256.Length != 64 ||
            !manifest.ImageSha256.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("承認対象の画像マニフェストが不正です。");
        }

        return manifest;
    }

    private static async Task RejectDuplicateApprovalAsync(
        string imageRoot,
        string manifestFileName,
        string manifestSha256,
        CancellationToken cancellationToken)
    {
        foreach (var path in Directory.EnumerateFiles(
                     imageRoot,
                     "ABACUS-image-approval-*.json",
                     SearchOption.TopDirectoryOnly))
        {
            var info = ValidateRegularFile(path, "既存の確認済み証跡", MaximumApprovalBytes);
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            if (bytes.Length != info.Length)
            {
                throw new InvalidDataException("既存の確認済み証跡が読み取り中に変更されました。");
            }

            var existing = JsonSerializer.Deserialize<ApprovalDocument>(bytes, JsonOptions);
            if (existing is null || existing.Version != 1 ||
                !string.Equals(existing.Status, "human-reviewed", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"既存の確認済み証跡が不正です: {Path.GetFileName(path)}");
            }

            if (string.Equals(existing.ManifestFileName, manifestFileName, StringComparison.Ordinal) &&
                string.Equals(existing.ManifestSha256, manifestSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("この画像マニフェストは既に確認済みです。既存の証跡を使用してください。");
            }
        }
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

    private static string CreateUniqueApprovalPath(string directory)
    {
        var baseName = $"ABACUS-image-approval-{DateTime.Now:yyyyMMdd-HHmmss}";
        for (var suffix = 0; suffix < 1_000; suffix++)
        {
            var name = suffix == 0 ? $"{baseName}.json" : $"{baseName}-{suffix}.json";
            var path = Path.Combine(directory, name);
            if (!File.Exists(path) && !Directory.Exists(path) && !File.Exists(path + ".partial"))
            {
                return path;
            }
        }

        throw new IOException("確認済み証跡の一意なファイル名を作成できませんでした。");
    }

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
}
