using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VehicleManagement.Companion.Services;

public sealed record AbacusImageLinkManifestResult(
    string FilePath,
    string Sha256,
    string MatchStrategy,
    string? ChassisNumber,
    string? RegistrationNumber);

public sealed class AbacusImageLinkManifestStore
{
    private const int MaximumIdentifierCharacters = 128;
    private const int MaximumCustomerNameCharacters = 200;

    public async Task<AbacusImageLinkManifestResult> CreateAsync(
        AbacusImageExportResult image,
        string sourceFolder,
        string workspaceFolder,
        string? sourceFolderFingerprint,
        string? workspaceFolderFingerprint,
        string chassisNumber,
        string registrationNumber,
        string customerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        var sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceFolder));
        var workspaceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceFolder));
        var imagePath = Path.GetFullPath(image.FilePath);
        var imageDirectory = Path.GetDirectoryName(imagePath);
        if (string.IsNullOrWhiteSpace(imageDirectory))
        {
            throw new InvalidDataException("保存済み画像のフォルダーを確認できません。");
        }

        if (!Directory.Exists(sourceRoot) || !Directory.Exists(workspaceRoot) ||
            !Directory.Exists(imageDirectory))
        {
            throw new DirectoryNotFoundException("原本・作業用コピー・画像保存先のいずれかが見つかりません。");
        }

        if (new DirectoryInfo(imageDirectory).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("リンクまたは再解析ポイントのフォルダーにはマニフェストを保存できません。");
        }

        if (IsSameOrSubPath(imageDirectory, sourceRoot) ||
            IsSameOrSubPath(imageDirectory, workspaceRoot))
        {
            throw new InvalidOperationException("画像またはマニフェストは原本・作業用コピーの内部に保存できません。");
        }

        var imageInfo = new FileInfo(imagePath);
        if (!imageInfo.Exists || imageInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("保存済み画像が存在しない、またはリンクです。画像を保存し直してください。");
        }

        if (imageInfo.Length != image.FileSize ||
            !string.Equals(await CalculateSha256Async(imagePath, cancellationToken), image.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("保存済み画像のサイズまたはSHA-256が保存時の結果と一致しません。画像を保存し直してください。");
        }

        var normalizedChassis = NormalizeIdentifier(chassisNumber, "車台番号");
        var normalizedRegistration = NormalizeIdentifier(registrationNumber, "登録番号");
        if (string.IsNullOrEmpty(normalizedChassis) && string.IsNullOrEmpty(normalizedRegistration))
        {
            throw new InvalidDataException("車台番号または登録番号を少なくとも1つ入力してください。");
        }

        var normalizedCustomer = NormalizeCustomerName(customerName);
        var matchStrategy = string.IsNullOrEmpty(normalizedChassis) ? "registration" : "chassis";
        var manifest = new ManifestDocument(
            Version: 1,
            Status: "review-only",
            CreatedAtUtc: DateTime.UtcNow,
            ImageFileName: Path.GetFileName(imagePath),
            ContentType: "image/png",
            FileSize: image.FileSize,
            PixelWidth: image.PixelWidth,
            PixelHeight: image.PixelHeight,
            ImageSha256: image.Sha256.ToUpperInvariant(),
            MatchStrategy: matchStrategy,
            ChassisNumber: normalizedChassis,
            RegistrationNumber: normalizedRegistration,
            CustomerName: normalizedCustomer,
            SourceFolderFingerprint: sourceFolderFingerprint,
            WorkspaceFolderFingerprint: workspaceFolderFingerprint);

        var json = JsonSerializer.SerializeToUtf8Bytes(
            manifest,
            new JsonSerializerOptions { WriteIndented = true });
        var outputPath = CreateUniqueManifestPath(imageDirectory);
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
                var reloaded = await JsonSerializer.DeserializeAsync<ManifestDocument>(
                    verification,
                    cancellationToken: cancellationToken);
                if (reloaded is null ||
                    !string.Equals(reloaded.ImageFileName, manifest.ImageFileName, StringComparison.Ordinal) ||
                    !string.Equals(reloaded.ImageSha256, manifest.ImageSha256, StringComparison.Ordinal) ||
                    !string.Equals(reloaded.MatchStrategy, manifest.MatchStrategy, StringComparison.Ordinal) ||
                    !string.Equals(reloaded.ChassisNumber, manifest.ChassisNumber, StringComparison.Ordinal) ||
                    !string.Equals(reloaded.RegistrationNumber, manifest.RegistrationNumber, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("画像紐付けマニフェストの再読込検証に失敗しました。");
                }
            }

            File.Move(temporaryPath, outputPath, overwrite: false);
            var manifestSha256 = Convert.ToHexString(SHA256.HashData(json));
            return new AbacusImageLinkManifestResult(
                outputPath,
                manifestSha256,
                matchStrategy,
                normalizedChassis,
                normalizedRegistration);
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

    private static string NormalizeIdentifier(string value, string label)
    {
        var normalized = (value ?? string.Empty).Trim().Normalize(NormalizationForm.FormKC);
        if (normalized.Length > MaximumIdentifierCharacters)
        {
            throw new InvalidDataException($"{label}が長すぎます。");
        }

        if (normalized.Any(char.IsControl))
        {
            throw new InvalidDataException($"{label}に制御文字は入力できません。");
        }

        return string.Concat(normalized.Where(character => !char.IsWhiteSpace(character) && character != '-'))
            .ToUpperInvariant();
    }

    private static string? NormalizeCustomerName(string value)
    {
        var normalized = (value ?? string.Empty).Trim().Normalize(NormalizationForm.FormKC);
        if (normalized.Length == 0)
        {
            return null;
        }

        if (normalized.Length > MaximumCustomerNameCharacters || normalized.Any(char.IsControl))
        {
            throw new InvalidDataException("確認用の顧客名が長すぎるか、制御文字を含んでいます。");
        }

        return normalized;
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

    private static string CreateUniqueManifestPath(string directory)
    {
        var baseName = $"ABACUS-image-link-{DateTime.Now:yyyyMMdd-HHmmss}";
        for (var suffix = 0; suffix < 1000; suffix++)
        {
            var name = suffix == 0 ? $"{baseName}.json" : $"{baseName}-{suffix}.json";
            var path = Path.Combine(directory, name);
            if (!File.Exists(path) && !Directory.Exists(path) && !File.Exists(path + ".partial"))
            {
                return path;
            }
        }

        throw new IOException("一意な画像紐付けマニフェスト名を作成できませんでした。");
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
