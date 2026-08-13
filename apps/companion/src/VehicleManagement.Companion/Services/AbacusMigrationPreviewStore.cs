using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using VehicleManagement.AbacusImport;

namespace VehicleManagement.Companion.Services;

public sealed record AbacusMigrationPreviewResult(
    string PackagePath,
    string ManifestPath,
    string ManifestSha256,
    int CustomerCandidates,
    int VehicleCandidates,
    int DocumentCandidates,
    int SkippedBlankCustomerDocuments,
    int ConflictGroups);

public interface IAbacusMigrationPreviewStore
{
    Task<AbacusMigrationPreviewResult> CreateAsync(
        string sourceFolder,
        string destinationParent,
        CancellationToken cancellationToken = default);
}

public sealed class AbacusMigrationPreviewStore(
    AbacusDataAnalyzer dataAnalyzer,
    AbacusLinkagePlanner linkagePlanner) : IAbacusMigrationPreviewStore
{
    private const string ManifestFileName = "manifest.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public async Task<AbacusMigrationPreviewResult> CreateAsync(
        string sourceFolder,
        string destinationParent,
        CancellationToken cancellationToken = default)
    {
        var sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceFolder));
        var destinationRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationParent));
        ValidateDestination(sourceRoot, destinationRoot);

        var before = await FingerprintSourceFilesAsync(sourceRoot, cancellationToken);
        var analysisTask = dataAnalyzer.AnalyzeAsync(sourceRoot, cancellationToken);
        var linkageTask = linkagePlanner.PlanAsync(sourceRoot, cancellationToken);
        await Task.WhenAll(analysisTask, linkageTask);
        var analysis = analysisTask.Result;
        var linkage = linkageTask.Result;
        if (!analysis.IsStructurallyValid || !linkage.IsValid)
        {
            throw new InvalidDataException("構造エラーがあるため移行準備情報を作成できません。");
        }

        var after = await FingerprintSourceFilesAsync(sourceRoot, cancellationToken);
        if (!before.SequenceEqual(after))
        {
            throw new InvalidDataException("分析中にABACUSバックアップファイルが変更されました。もう一度実行してください。");
        }

        var manifest = new MigrationPreviewManifest(
            Version: 1,
            Kind: "abacus-migration-preview",
            Status: "preview-only",
            CreatedAtUtc: DateTime.UtcNow,
            SourceFiles: after,
            Summary: new MigrationPreviewSummary(
                linkage.CustomerCandidates,
                linkage.VehicleCandidates,
                linkage.ImportCandidateDocuments,
                analysis.Sales.ImportCandidateRows,
                analysis.Maintenance.ImportCandidateRows,
                linkage.SkippedBlankCustomerDocuments,
                linkage.SameNameConflictGroups,
                linkage.VehicleIdentifierConflictGroups + linkage.VehiclesLinkedToMultipleCustomers),
            DataFiles: [],
            ImageFiles: []);

        var packagePath = CreateUniquePackageDirectory(destinationRoot);
        var manifestPath = Path.Combine(packagePath, ManifestFileName);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        await using (var stream = new FileStream(
                         manifestPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         64 * 1024,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        var verifiedBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
        VerifyManifest(verifiedBytes, manifest);

        return new AbacusMigrationPreviewResult(
            packagePath,
            manifestPath,
            Convert.ToHexString(SHA256.HashData(verifiedBytes)),
            linkage.CustomerCandidates,
            linkage.VehicleCandidates,
            linkage.ImportCandidateDocuments,
            linkage.SkippedBlankCustomerDocuments,
            linkage.SameNameConflictGroups + linkage.VehicleIdentifierConflictGroups +
            linkage.VehiclesLinkedToMultipleCustomers);
    }

    private static void VerifyManifest(byte[] bytes, MigrationPreviewManifest expected)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            var sourceFiles = root.GetProperty("sourceFiles");
            var summary = root.GetProperty("summary");
            var isValid =
                root.GetProperty("version").GetInt32() == expected.Version &&
                root.GetProperty("kind").GetString() == expected.Kind &&
                root.GetProperty("status").GetString() == expected.Status &&
                sourceFiles.GetArrayLength() == expected.SourceFiles.Count &&
                root.GetProperty("dataFiles").GetArrayLength() == 0 &&
                root.GetProperty("imageFiles").GetArrayLength() == 0 &&
                summary.GetProperty("customerCandidates").GetInt32() == expected.Summary.CustomerCandidates &&
                summary.GetProperty("vehicleCandidates").GetInt32() == expected.Summary.VehicleCandidates &&
                summary.GetProperty("documentCandidates").GetInt32() == expected.Summary.DocumentCandidates &&
                summary.GetProperty("salesDocumentCandidates").GetInt32() == expected.Summary.SalesDocumentCandidates &&
                summary.GetProperty("maintenanceDocumentCandidates").GetInt32() == expected.Summary.MaintenanceDocumentCandidates &&
                summary.GetProperty("skippedBlankCustomerDocuments").GetInt32() == expected.Summary.SkippedBlankCustomerDocuments &&
                summary.GetProperty("sameNameConflictGroups").GetInt32() == expected.Summary.SameNameConflictGroups &&
                summary.GetProperty("vehicleConflictGroups").GetInt32() == expected.Summary.VehicleConflictGroups;

            for (var index = 0; isValid && index < expected.SourceFiles.Count; index++)
            {
                var actual = sourceFiles[index];
                var expectedFile = expected.SourceFiles[index];
                isValid = actual.GetProperty("fileName").GetString() == expectedFile.FileName &&
                          actual.GetProperty("sizeBytes").GetInt64() == expectedFile.SizeBytes &&
                          actual.GetProperty("sha256").GetString() == expectedFile.Sha256;
            }

            if (!isValid)
            {
                throw new InvalidDataException("作成した移行準備マニフェストの内容が書込前と一致しません。");
            }
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidDataException("作成した移行準備マニフェストの形式が不正です。", exception);
        }
    }

    private static void ValidateDestination(string sourceRoot, string destinationRoot)
    {
        var destination = new DirectoryInfo(destinationRoot);
        if (!destination.Exists)
        {
            throw new DirectoryNotFoundException("移行準備情報の保存先フォルダーが見つかりません。");
        }

        if (destination.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("リンクまたは再解析ポイントには保存できません。");
        }

        if (destinationRoot.Equals(sourceRoot, StringComparison.OrdinalIgnoreCase) ||
            destinationRoot.StartsWith($"{sourceRoot}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("ABACUSフォルダーの内部には移行準備情報を保存できません。");
        }
    }

    private static string CreateUniquePackageDirectory(string destinationRoot)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var suffix = attempt == 0 ? string.Empty : $"-{attempt:D2}";
            var path = Path.Combine(destinationRoot, $"ABACUS-Migration-Preview-{DateTime.Now:yyyyMMdd-HHmmss}{suffix}");
            if (Directory.Exists(path) || File.Exists(path))
            {
                continue;
            }

            Directory.CreateDirectory(path);
            return path;
        }

        throw new IOException("一意な移行準備フォルダーを作成できませんでした。");
    }

    private static async Task<IReadOnlyList<MigrationSourceFile>> FingerprintSourceFilesAsync(
        string sourceRoot,
        CancellationToken cancellationToken)
    {
        var results = new List<MigrationSourceFile>();
        foreach (var specification in new[] { AbacusTabSpecifications.Sales, AbacusTabSpecifications.Maintenance })
        {
            var path = Path.GetFullPath(Path.Combine(sourceRoot, specification.FileName));
            if (!path.StartsWith($"{sourceRoot}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("ABACUSフォルダー外のファイルは使用できません。");
            }

            var info = new FileInfo(path);
            if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException($"安全に読み取れない必須ファイルがあります: {specification.FileName}");
            }

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            results.Add(new MigrationSourceFile(specification.FileName, info.Length, Convert.ToHexString(hash)));
        }

        return results;
    }

    private sealed record MigrationPreviewManifest(
        int Version,
        string Kind,
        string Status,
        DateTime CreatedAtUtc,
        IReadOnlyList<MigrationSourceFile> SourceFiles,
        MigrationPreviewSummary Summary,
        IReadOnlyList<string> DataFiles,
        IReadOnlyList<string> ImageFiles);

    private sealed record MigrationSourceFile(string FileName, long SizeBytes, string Sha256);

    private sealed record MigrationPreviewSummary(
        int CustomerCandidates,
        int VehicleCandidates,
        int DocumentCandidates,
        int SalesDocumentCandidates,
        int MaintenanceDocumentCandidates,
        int SkippedBlankCustomerDocuments,
        int SameNameConflictGroups,
        int VehicleConflictGroups);
}
