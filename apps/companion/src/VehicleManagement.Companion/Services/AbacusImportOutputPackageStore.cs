using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VehicleManagement.Companion.Services;

/// <summary>
/// Gate 11 の成果物境界を管理します。作業途中のファイルと Web へ渡す完成品を
/// 同じルート内で分離し、完成品側のマニフェストを最後に書き込むことで、途中状態を
/// 完成品として扱わないようにします。
/// </summary>
public sealed record AbacusImportOutputPackageSession(
    string PackageId,
    string RootPath,
    string RootManifestPath,
    string WorkPath,
    string WorkAbacusCopyPath,
    string WorkIntermediatePath,
    string WorkCheckpointsPath,
    string WorkLogsPath,
    string ReadyPath,
    string ReadyManifestPath,
    string SourcePath,
    string SourceFingerprint,
    string ImageAcquisitionMethod);

public sealed record AbacusImportOutputPackageReadyResult(
    string RootPath,
    string ReadyPath,
    string ReadyManifestPath,
    string RootManifestPath,
    IReadOnlyList<string> ReadyFiles);

public sealed class AbacusImportOutputPackageStore
{
    private const int RootManifestVersion = 1;
    private const int ReadyManifestVersion = 1;
    private const long MaximumManifestBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public async Task<AbacusImportOutputPackageSession> CreateAsync(
        string destinationParent,
        string sourcePath,
        string? sourceFingerprint,
        string imageAcquisitionMethod,
        CancellationToken cancellationToken = default)
    {
        var destinationRoot = ValidateExistingDirectory(destinationParent, "生成物の保存先");
        var sourceRoot = ValidateExistingDirectory(sourcePath, "ABACUSフォルダー");
        if (IsSameOrSubPath(destinationRoot, sourceRoot))
        {
            throw new InvalidDataException("生成物の保存先はABACUSフォルダーの外側を選択してください。");
        }

        var packageId = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        var rootPath = Path.Combine(destinationRoot, $"ABACUS-Import-{packageId}");
        Directory.CreateDirectory(rootPath);
        try
        {
            var workPath = Path.Combine(rootPath, "work");
            var workAbacusCopyPath = Path.Combine(workPath, "abacus-copy");
            var workIntermediatePath = Path.Combine(workPath, "intermediate");
            var workCheckpointsPath = Path.Combine(workPath, "checkpoints");
            var workLogsPath = Path.Combine(workPath, "logs");
            var readyPath = Path.Combine(rootPath, "ready");
            Directory.CreateDirectory(workAbacusCopyPath);
            Directory.CreateDirectory(workIntermediatePath);
            Directory.CreateDirectory(workCheckpointsPath);
            Directory.CreateDirectory(workLogsPath);
            Directory.CreateDirectory(readyPath);
            Directory.CreateDirectory(Path.Combine(readyPath, "data"));
            Directory.CreateDirectory(Path.Combine(readyPath, "mappings"));
            Directory.CreateDirectory(Path.Combine(readyPath, "images"));
            Directory.CreateDirectory(Path.Combine(readyPath, "reports"));

            var fingerprint = string.IsNullOrWhiteSpace(sourceFingerprint)
                ? await CalculateMetadataFingerprintAsync(sourceRoot, cancellationToken)
                : sourceFingerprint.Trim();
            var method = NormalizeImageMethod(imageAcquisitionMethod);
            var session = new AbacusImportOutputPackageSession(
                packageId,
                rootPath,
                Path.Combine(rootPath, "abacus-import.json"),
                workPath,
                workAbacusCopyPath,
                workIntermediatePath,
                workCheckpointsPath,
                workLogsPath,
                readyPath,
                Path.Combine(readyPath, "manifest.json"),
                sourceRoot,
                fingerprint,
                method);

            await WriteRootManifestAsync(session, "in-progress", cancellationToken);
            await WriteTextFileAsync(
                Path.Combine(workCheckpointsPath, "state.json"),
                JsonSerializer.Serialize(new
                {
                    version = 1,
                    status = "created",
                    packageId,
                    updatedAtUtc = DateTime.UtcNow,
                    sourceFingerprint = fingerprint,
                    imageAcquisitionMethod = method,
                }, JsonOptions),
                cancellationToken);
            await WriteTextFileAsync(
                session.ReadyManifestPath,
                JsonSerializer.Serialize(new
                {
                    version = ReadyManifestVersion,
                    kind = "abacus-import-ready",
                    status = "pending",
                    packageId,
                    createdAtUtc = DateTime.UtcNow,
                    sourceFingerprint = fingerprint,
                    imageAcquisitionMethod = method,
                    files = Array.Empty<object>(),
                }, JsonOptions),
                cancellationToken);
            return session;
        }
        catch
        {
            if (Directory.Exists(rootPath) && IsSameOrSubPath(rootPath, destinationRoot))
            {
                Directory.Delete(rootPath, recursive: true);
            }

            throw;
        }
    }

    public async Task<AbacusImportOutputPackageReadyResult> CompleteAsync(
        AbacusImportOutputPackageSession session,
        AbacusLegacyGraphFinalPackageResult finalPackage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(finalPackage);
        ValidateSession(session);
        var stagingPath = Path.Combine(session.WorkIntermediatePath, $"ready-staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingPath);
        try
        {
            var dataPath = Path.Combine(stagingPath, "data");
            var mappingsPath = Path.Combine(stagingPath, "mappings");
            var imagesPath = Path.Combine(stagingPath, "images");
            var reportsPath = Path.Combine(stagingPath, "reports");
            Directory.CreateDirectory(dataPath);
            Directory.CreateDirectory(mappingsPath);
            Directory.CreateDirectory(imagesPath);
            Directory.CreateDirectory(reportsPath);

            await CopyFileAsync(finalPackage.CustomersCsvPath, Path.Combine(dataPath, "customers.csv"), cancellationToken);
            await CopyFileAsync(finalPackage.VehiclesCsvPath, Path.Combine(dataPath, "vehicles.csv"), cancellationToken);
            await CopyFileAsync(finalPackage.SalesCsvPath, Path.Combine(dataPath, "sales-documents.csv"), cancellationToken);
            await CopyFileAsync(finalPackage.MaintenanceCsvPath, Path.Combine(dataPath, "maintenance-documents.csv"), cancellationToken);
            await CopyFileAsync(finalPackage.DocumentLinksPath, Path.Combine(mappingsPath, "document-links.json"), cancellationToken);
            if (finalPackage.ImageAttachmentsPath is { } imageAttachmentsPath && File.Exists(imageAttachmentsPath))
            {
                await CopyFileAsync(imageAttachmentsPath, Path.Combine(mappingsPath, "image-attachments.json"), cancellationToken);
            }

            var sourceImageRoot = Path.Combine(finalPackage.PackagePath, "images");
            if (Directory.Exists(sourceImageRoot))
            {
                CopyDirectory(sourceImageRoot, imagesPath);
            }

            var finalManifest = await ReadJsonObjectAsync(Path.Combine(finalPackage.PackagePath, "manifest.json"), cancellationToken);
            await WriteTextFileAsync(
                Path.Combine(mappingsPath, "customer-merges.json"),
                finalManifest.TryGetProperty("groups", out var groups) ? groups.GetRawText() : "[]",
                cancellationToken);
            await WriteTextFileAsync(
                Path.Combine(reportsPath, "excluded-documents.json"),
                finalManifest.TryGetProperty("excludedDocumentKeys", out var excluded) ? excluded.GetRawText() : "[]",
                cancellationToken);
            await WriteTextFileAsync(Path.Combine(reportsPath, "unresolved-items.json"), "[]", cancellationToken);
            await WriteTextFileAsync(
                Path.Combine(reportsPath, "image-acquisition-report.json"),
                JsonSerializer.Serialize(new
                {
                    method = session.ImageAcquisitionMethod,
                    status = finalPackage.ImageCount > 0 ? "completed" : "not-selected",
                    imageCount = finalPackage.ImageCount,
                }, JsonOptions),
                cancellationToken);

            var readyFiles = await DescribeFilesAsync(stagingPath, cancellationToken);
            var readyManifest = new
            {
                version = ReadyManifestVersion,
                kind = "abacus-import-ready",
                status = "ready",
                packageId = session.PackageId,
                createdAtUtc = DateTime.UtcNow,
                source = new { path = session.SourcePath, fingerprint = session.SourceFingerprint },
                imageAcquisitionMethod = session.ImageAcquisitionMethod,
                summary = new
                {
                    customerCount = finalPackage.CustomerRowCount,
                    vehicleCount = finalPackage.VehicleRowCount,
                    salesDocumentCount = finalPackage.SalesRowCount,
                    maintenanceDocumentCount = finalPackage.MaintenanceRowCount,
                    vehiclelessDocumentCount = finalPackage.VehiclelessDocumentCount,
                    excludedDocumentCount = finalPackage.ExcludedDocumentCount,
                    imageCount = finalPackage.ImageCount,
                },
                files = readyFiles,
            };
            await WriteTextFileAsync(Path.Combine(stagingPath, "manifest.json"), JsonSerializer.Serialize(readyManifest, JsonOptions), cancellationToken);

            // ready配下の既存のpending内容を残したまま、検証済みの各ファイルを上書きします。
            // 完成状態のmanifestは最後に置くため、途中で停止してもpendingとして扱えます。
            CopyDirectory(stagingPath, session.ReadyPath, overwrite: true);
            await WriteRootManifestAsync(session, "ready", cancellationToken);
            return new AbacusImportOutputPackageReadyResult(
                session.RootPath,
                session.ReadyPath,
                session.ReadyManifestPath,
                session.RootManifestPath,
                readyFiles.Select(file => file.Path).ToArray());
        }
        catch
        {
            if (Directory.Exists(stagingPath)) Directory.Delete(stagingPath, recursive: true);
            throw;
        }
    }

    private async Task WriteRootManifestAsync(AbacusImportOutputPackageSession session, string status, CancellationToken cancellationToken)
    {
        var manifest = new
        {
            version = RootManifestVersion,
            kind = "abacus-import",
            status,
            packageId = session.PackageId,
            createdAtUtc = Directory.GetCreationTimeUtc(session.RootPath),
            updatedAtUtc = DateTime.UtcNow,
            source = new { path = session.SourcePath, fingerprint = session.SourceFingerprint },
            workPath = "work",
            readyPath = "ready",
            readyManifest = "ready/manifest.json",
            imageAcquisitionMethod = session.ImageAcquisitionMethod,
        };
        await WriteTextFileAsync(session.RootManifestPath, JsonSerializer.Serialize(manifest, JsonOptions), cancellationToken);
    }

    private void ValidateSession(AbacusImportOutputPackageSession session)
    {
        var root = ValidateExistingDirectory(session.RootPath, "生成物ルート");
        if (!IsSameOrSubPath(session.WorkPath, root) || !IsSameOrSubPath(session.ReadyPath, root) ||
            !string.Equals(Path.GetFullPath(session.RootManifestPath), Path.Combine(root, "abacus-import.json"), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("生成物ルートの構成が不正です。");
        }
        _ = ValidateExistingDirectory(session.ReadyPath, "完成品フォルダー");
    }

    private static async Task<JsonElement> ReadJsonObjectAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length > MaximumManifestBytes)
        {
            throw new InvalidDataException($"マニフェストを読み取れません: {path}");
        }

        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path, cancellationToken));
        return document.RootElement.Clone();
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        var sourceInfo = new FileInfo(source);
        if (!sourceInfo.Exists || sourceInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException($"出力元ファイルが見つかりません: {source}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    private static async Task WriteTextFileAsync(string path, string contents, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidDataException($"出力先フォルダーを解決できません: {path}");
        }

        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, contents, Encoding.UTF8, cancellationToken);
    }

    private static void CopyDirectory(string source, string destination, bool overwrite = false)
    {
        var sourceInfo = new DirectoryInfo(source);
        if (!sourceInfo.Exists || sourceInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException($"出力元フォルダーが見つかりません: {source}");
        }

        Directory.CreateDirectory(destination);
        foreach (var directory in sourceInfo.EnumerateDirectories())
        {
            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            CopyDirectory(directory.FullName, Path.Combine(destination, directory.Name), overwrite);
        }
        foreach (var file in sourceInfo.EnumerateFiles())
        {
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            File.Copy(file.FullName, Path.Combine(destination, file.Name), overwrite);
        }
    }

    private static async Task<IReadOnlyList<ReadyFile>> DescribeFilesAsync(string root, CancellationToken cancellationToken)
    {
        var files = new List<ReadyFile>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (string.Equals(relative, "manifest.json", StringComparison.OrdinalIgnoreCase)) continue;
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            files.Add(new ReadyFile(relative, info.Length, Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))));
        }
        return files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
    }

    private static async Task<string> CalculateMetadataFingerprintAsync(string root, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .Where(info => !info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            .OrderBy(info => Path.GetRelativePath(root, info.FullName), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var info in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = $"{Path.GetRelativePath(root, info.FullName)}\0{info.Length}\0{info.LastWriteTimeUtc.Ticks}\n";
            hash.AppendData(Encoding.UTF8.GetBytes(line));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string NormalizeImageMethod(string value) => value switch
    {
        "screen-navigation" => value,
        "fast-experimental" => value,
        _ => throw new ArgumentException("画像取得方式が不正です。", nameof(value)),
    };

    private static string ValidateExistingDirectory(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException($"{label}を指定してください。", nameof(path));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
        var info = new DirectoryInfo(full);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException($"{label}を読み取れません: {full}");
        return full;
    }

    private static bool IsSameOrSubPath(string candidate, string root)
    {
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               normalizedCandidate.StartsWith($"{normalizedRoot}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ReadyFile(string Path, long SizeBytes, string Sha256);
}
