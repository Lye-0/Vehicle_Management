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

            var fingerprintBeforeCopy = string.IsNullOrWhiteSpace(sourceFingerprint)
                ? await CalculateMetadataFingerprintAsync(sourceRoot, cancellationToken)
                : sourceFingerprint.Trim();
            await CopySourceSnapshotAsync(sourceRoot, workAbacusCopyPath, cancellationToken);
            var fingerprint = await CalculateMetadataFingerprintAsync(sourceRoot, cancellationToken);
            if (!string.Equals(fingerprintBeforeCopy, fingerprint, StringComparison.Ordinal))
            {
                throw new InvalidDataException("ABACUSフォルダーのコピー中に原本の指紋が変化しました。処理を中止しました。");
            }
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
                    status = "copied",
                    packageId,
                    updatedAtUtc = DateTime.UtcNow,
                    sourceFingerprint = fingerprint,
                    imageAcquisitionMethod = method,
                    abacusCopyPath = "../abacus-copy",
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
        string? imageAcquisitionReportPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(finalPackage);
        ValidateSession(session);
        var stagingPath = Path.Combine(session.WorkIntermediatePath, $"ready-staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingPath);
        try
        {
            await WriteCheckpointAsync(session, "finalizing", cancellationToken);
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
                    status = imageAcquisitionReportPath is not null
                        ? "gate14-verified"
                        : finalPackage.ImageCount > 0 ? "completed" : "not-selected",
                    imageCount = finalPackage.ImageCount,
                }, JsonOptions),
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(imageAcquisitionReportPath))
            {
                if (!IsSameOrSubPath(imageAcquisitionReportPath, session.WorkPath))
                {
                    throw new InvalidDataException("画像対応付けレポートが作業用フォルダー外を指しています。");
                }

                await CopyFileAsync(
                    imageAcquisitionReportPath,
                    Path.Combine(reportsPath, "fp5-vehicle-image-mapping-report.json"),
                    cancellationToken);
            }

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

            // 同じセッションを再実行した場合も、前回の画像やレポートを残さない。
            // 古いファイルが残るとready/manifest.jsonに列挙されないファイルをWeb側へ
            // 渡すことになるため、検証済みstagingの内容と完全に置き換える。
            await WriteRootManifestAsync(session, "in-progress", cancellationToken);
            ReplaceDirectoryContents(stagingPath, session.ReadyPath);
            await WriteRootManifestAsync(session, "ready", cancellationToken);
            await WriteCheckpointAsync(session, "ready", cancellationToken);
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
            await TryWriteCheckpointAsync(session, "failed");
            throw;
        }
    }

    /// <summary>
    /// アプリ再起動後に、既存のABACUS-Importルートを安全に再開対象として開きます。
    /// ルートマニフェスト、作業領域、保存用原本の指紋を再検証するため、途中状態を
    /// 別の原本や別パッケージへ誤って紐付けません。
    /// </summary>
    public async Task<AbacusImportOutputPackageSession> OpenAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        var root = ValidateExistingDirectory(rootPath, "生成物ルート");
        var rootManifestPath = Path.Combine(root, "abacus-import.json");
        var manifest = await ReadJsonObjectAsync(rootManifestPath, cancellationToken);
        if (!string.Equals(ReadString(manifest, "kind"), "abacus-import", StringComparison.Ordinal) ||
            ReadInt32(manifest, "version") != RootManifestVersion)
        {
            throw new InvalidDataException("ABACUS登録パッケージのルートマニフェストが不正です。");
        }

        var packageId = ReadString(manifest, "packageId");
        var status = ReadString(manifest, "status");
        if (packageId.Length == 0 || status is not ("in-progress" or "ready"))
        {
            throw new InvalidDataException("ABACUS登録パッケージの状態が再開対象ではありません。");
        }

        var source = ReadObject(manifest, "source");
        var sourcePath = ValidateExistingDirectory(ReadString(source, "path"), "ABACUSフォルダー");
        var sourceFingerprint = ReadString(source, "fingerprint");
        if (sourceFingerprint.Length != 64)
        {
            throw new InvalidDataException("ABACUS原本の指紋が不正です。");
        }
        if (IsSameOrSubPath(root, sourcePath) || IsSameOrSubPath(sourcePath, root))
        {
            throw new InvalidDataException("生成物と保存用ABACUS原本が分離されていません。");
        }

        var workPath = ResolveManifestPath(root, manifest, "workPath", "work");
        var workAbacusCopyPath = ResolveManifestPath(root, manifest, "workAbacusCopy", "work/abacus-copy");
        var workIntermediatePath = ResolveManifestPath(root, manifest, "workIntermediate", "work/intermediate");
        var workCheckpointsPath = ResolveManifestPath(root, manifest, "workCheckpoints", "work/checkpoints");
        var workLogsPath = ResolveManifestPath(root, manifest, "workLogs", "work/logs");
        var readyPath = ResolveManifestPath(root, manifest, "readyPath", "ready");
        var readyManifestPath = ResolveManifestPath(root, manifest, "readyManifest", "ready/manifest.json");
        foreach (var path in new[] { workPath, workAbacusCopyPath, workIntermediatePath, workCheckpointsPath, workLogsPath, readyPath })
        {
            _ = ValidateExistingDirectory(path, "生成物領域");
        }
        if (!string.Equals(Path.GetFullPath(readyManifestPath), Path.Combine(readyPath, "manifest.json"), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("完成品マニフェストの配置が不正です。");
        }
        var readyManifest = await ReadJsonObjectAsync(readyManifestPath, cancellationToken);
        if (!string.Equals(ReadString(readyManifest, "kind"), "abacus-import-ready", StringComparison.Ordinal) ||
            ReadInt32(readyManifest, "version") != ReadyManifestVersion ||
            !string.Equals(ReadString(readyManifest, "packageId"), packageId, StringComparison.Ordinal) ||
            ReadString(readyManifest, "status") is not ("pending" or "ready"))
        {
            throw new InvalidDataException("完成品マニフェストがルートマニフェストと一致しません。");
        }
        var checkpointPath = Path.Combine(workCheckpointsPath, "state.json");
        if (File.Exists(checkpointPath))
        {
            var checkpoint = await ReadJsonObjectAsync(checkpointPath, cancellationToken);
            if (!string.Equals(ReadString(checkpoint, "packageId"), packageId, StringComparison.Ordinal) ||
                !string.Equals(ReadString(checkpoint, "sourceFingerprint"), sourceFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("再開チェックポイントがパッケージと一致しません。");
            }
        }

        var actualFingerprint = await CalculateMetadataFingerprintAsync(sourcePath, cancellationToken);
        if (!string.Equals(actualFingerprint, sourceFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("保存用ABACUS原本が作成時の指紋と一致しません。再開を中止しました。");
        }

        return new AbacusImportOutputPackageSession(
            packageId,
            root,
            rootManifestPath,
            workPath,
            workAbacusCopyPath,
            workIntermediatePath,
            workCheckpointsPath,
            workLogsPath,
            readyPath,
            readyManifestPath,
            sourcePath,
            sourceFingerprint,
            NormalizeImageMethod(ReadString(manifest, "imageAcquisitionMethod")));
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
            workAbacusCopy = "work/abacus-copy",
            workIntermediate = "work/intermediate",
            workCheckpoints = "work/checkpoints",
            workLogs = "work/logs",
            readyPath = "ready",
            readyManifest = "ready/manifest.json",
            imageAcquisitionMethod = session.ImageAcquisitionMethod,
        };
        await WriteTextFileAsync(session.RootManifestPath, JsonSerializer.Serialize(manifest, JsonOptions), cancellationToken);
    }

    private static Task WriteCheckpointAsync(
        AbacusImportOutputPackageSession session,
        string status,
        CancellationToken cancellationToken) =>
        WriteTextFileAsync(
            Path.Combine(session.WorkCheckpointsPath, "state.json"),
            JsonSerializer.Serialize(new
            {
                version = 1,
                status,
                packageId = session.PackageId,
                updatedAtUtc = DateTime.UtcNow,
                sourceFingerprint = session.SourceFingerprint,
                imageAcquisitionMethod = session.ImageAcquisitionMethod,
                abacusCopyPath = "../abacus-copy",
            }, JsonOptions),
            cancellationToken);

    private static async Task TryWriteCheckpointAsync(AbacusImportOutputPackageSession session, string status)
    {
        try
        {
            await WriteCheckpointAsync(session, status, CancellationToken.None);
        }
        catch
        {
            // 元の例外を失敗理由として返す。診断用チェックポイントの書き込み失敗で
            // エラー内容を隠さない。
        }
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

    private static async Task CopySourceSnapshotAsync(string source, string destination, CancellationToken cancellationToken)
    {
        var sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        var destinationRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceInfo = new FileInfo(file);
            if (sourceInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(sourceRoot, file);
            var destinationPath = Path.Combine(destinationRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using (var input = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            File.SetLastWriteTimeUtc(destinationPath, sourceInfo.LastWriteTimeUtc);
        }
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

    private static void ReplaceDirectoryContents(string source, string destination)
    {
        var sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        var destinationRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        if (!Directory.Exists(sourceRoot) || !Directory.Exists(destinationRoot) ||
            IsSameOrSubPath(sourceRoot, destinationRoot) || IsSameOrSubPath(destinationRoot, sourceRoot))
        {
            throw new InvalidDataException("完成品フォルダーの置換範囲が不正です。");
        }

        foreach (var destinationFile in Directory.EnumerateFiles(destinationRoot, "*", SearchOption.AllDirectories).ToArray())
        {
            var relative = Path.GetRelativePath(destinationRoot, destinationFile);
            if (!File.Exists(Path.Combine(sourceRoot, relative)))
            {
                File.Delete(destinationFile);
            }
        }

        foreach (var destinationDirectory in Directory.EnumerateDirectories(destinationRoot, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length)
                     .ToArray())
        {
            var relative = Path.GetRelativePath(destinationRoot, destinationDirectory);
            if (!Directory.Exists(Path.Combine(sourceRoot, relative)))
            {
                Directory.Delete(destinationDirectory, recursive: true);
            }
        }

        CopyDirectory(sourceRoot, destinationRoot, overwrite: true);
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

    private static string ResolveManifestPath(
        string root,
        JsonElement manifest,
        string propertyName,
        string fallbackRelativePath)
    {
        var relative = manifest.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : fallbackRelativePath;
        if (relative.Length == 0 || Path.IsPathRooted(relative) || relative.Contains(':', StringComparison.Ordinal))
        {
            throw new InvalidDataException($"ルートマニフェストのパスが不正です: {propertyName}");
        }

        var resolved = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsSameOrSubPath(resolved, root))
        {
            throw new InvalidDataException($"ルートマニフェストがフォルダー外を参照しています: {propertyName}");
        }

        return Path.TrimEndingDirectorySeparator(resolved);
    }

    private static JsonElement ReadObject(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"マニフェストの必須オブジェクトがありません: {propertyName}");
        }

        return value;
    }

    private static string ReadString(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"マニフェストの必須文字列がありません: {propertyName}");
        }

        return value.GetString()?.Trim() ?? string.Empty;
    }

    private static int ReadInt32(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
        {
            throw new InvalidDataException($"マニフェストの必須数値がありません: {propertyName}");
        }

        return number;
    }

    private static string NormalizeImageMethod(string value) => value switch
    {
        "fp5-vehicle-record" => value,
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
