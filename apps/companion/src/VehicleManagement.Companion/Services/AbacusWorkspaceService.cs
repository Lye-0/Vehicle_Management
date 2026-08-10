using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VehicleManagement.Companion.Services;

public sealed record AbacusWorkspaceProgress(
    string Phase,
    int CompletedFiles,
    int TotalFiles,
    string CurrentFile);

public sealed record AbacusWorkspaceResult(
    string WorkspacePath,
    string ManifestPath,
    AbacusFolderReport WorkspaceReport,
    AbacusFolderReport SourceAfterCopyReport);

public sealed record AbacusWorkspaceVerificationResult(
    string WorkspacePath,
    string ManifestPath,
    AbacusFolderReport WorkspaceReport,
    string OriginalFingerprint,
    IReadOnlyList<string> AllowedRuntimeChanges);

public sealed class AbacusWorkspaceService(AbacusFolderInspector inspector)
{
    private const int MaximumRuntimeChangedBytesPerFile = 4096;
    private const int MaximumRuntimeChangedBytesTotal = 16 * 1024;
    private static readonly Regex RuntimeMutableFilePattern = new(
        "^abx-cs-[a-z0-9]{2}\\.ucs$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private sealed record WorkspaceManifest(
        int Version,
        DateTime CreatedAtUtc,
        string SourcePath,
        string WorkspacePath,
        int FileCount,
        long TotalBytes,
        string FolderFingerprint);

    public async Task<AbacusWorkspaceResult> CreateAsync(
        AbacusFolderReport sourceReport,
        string destinationParent,
        IProgress<AbacusWorkspaceProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!sourceReport.IsValid)
        {
            throw new InvalidOperationException("検査に合格していないABACUSフォルダーはコピーできません。");
        }

        var sourceRoot = Path.GetFullPath(sourceReport.SourcePath);
        var destinationRoot = Path.GetFullPath(destinationParent);
        if (!Directory.Exists(destinationRoot))
        {
            throw new DirectoryNotFoundException("コピー先の親フォルダーが見つかりません。");
        }

        if (IsSameOrSubPath(destinationRoot, sourceRoot))
        {
            throw new InvalidOperationException("保存用原本の内部をコピー先には指定できません。");
        }

        var workspacePath = CreateUniqueWorkspacePath(destinationRoot);
        if (IsSameOrSubPath(workspacePath, sourceRoot) || IsSameOrSubPath(sourceRoot, workspacePath))
        {
            throw new InvalidOperationException("保存用原本とは別の場所に作業用コピーを作成してください。");
        }

        Directory.CreateDirectory(workspacePath);

        for (var index = 0; index < sourceReport.Files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceFile = sourceReport.Files[index];
            progress?.Report(new AbacusWorkspaceProgress("コピー中", index, sourceReport.FileCount, sourceFile.RelativePath));

            var sourcePath = SafeCombine(sourceRoot, sourceFile.RelativePath);
            var destinationPath = SafeCombine(workspacePath, sourceFile.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

            await using (var input = new FileStream(
                sourcePath,
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
            }

            File.SetLastWriteTimeUtc(destinationPath, sourceFile.LastWriteTimeUtc);
            await VerifyFileHashAsync(destinationPath, sourceFile.Sha256, cancellationToken);
        }

        progress?.Report(new AbacusWorkspaceProgress("コピー検証中", sourceReport.FileCount, sourceReport.FileCount, "作業用コピー"));
        var workspaceReport = await inspector.InspectAsync(
            workspacePath,
            new Progress<AbacusInspectionProgress>(item =>
                progress?.Report(new AbacusWorkspaceProgress("コピー検証中", item.CompletedFiles, item.TotalFiles, item.CurrentFile))),
            cancellationToken);

        progress?.Report(new AbacusWorkspaceProgress("原本再検証中", 0, sourceReport.FileCount, "保存用原本"));
        var sourceAfterCopyReport = await inspector.InspectAsync(
            sourceRoot,
            new Progress<AbacusInspectionProgress>(item =>
                progress?.Report(new AbacusWorkspaceProgress("原本再検証中", item.CompletedFiles, item.TotalFiles, item.CurrentFile))),
            cancellationToken);

        if (workspaceReport.FolderFingerprint != sourceReport.FolderFingerprint)
        {
            throw new InvalidDataException("作業用コピーが保存用原本と一致しません。コピーは使用しないでください。");
        }

        if (sourceAfterCopyReport.FolderFingerprint != sourceReport.FolderFingerprint)
        {
            throw new InvalidDataException("コピー処理中に保存用原本の内容が変化しました。処理を中止しました。");
        }

        if (!HasSameSourceMetadata(sourceReport, sourceAfterCopyReport))
        {
            throw new InvalidDataException("コピー処理中に保存用原本のファイル情報が変化しました。処理を中止しました。");
        }

        var manifestPath = $"{workspacePath}.manifest.json";
        var manifest = new
        {
            Version = 1,
            CreatedAtUtc = DateTime.UtcNow,
            SourcePath = sourceRoot,
            WorkspacePath = workspacePath,
            sourceReport.FileCount,
            sourceReport.TotalBytes,
            sourceReport.FolderFingerprint,
        };
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);

        progress?.Report(new AbacusWorkspaceProgress("完了", sourceReport.FileCount, sourceReport.FileCount, "完了"));
        return new AbacusWorkspaceResult(workspacePath, manifestPath, workspaceReport, sourceAfterCopyReport);
    }

    public async Task<AbacusWorkspaceVerificationResult> VerifyExistingAsync(
        string workspacePath,
        IProgress<AbacusWorkspaceProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
        var manifestPath = $"{root}.manifest.json";
        if (!Directory.Exists(root) || !File.Exists(manifestPath))
        {
            throw new InvalidDataException("作業用コピーまたは隣接する検証マニフェストが見つかりません。");
        }

        var manifestInfo = new FileInfo(manifestPath);
        if (manifestInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) || manifestInfo.Length > 64 * 1024)
        {
            throw new InvalidDataException("検証マニフェストの形式が不正です。");
        }

        await using var manifestStream = new FileStream(
            manifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var manifest = await JsonSerializer.DeserializeAsync<WorkspaceManifest>(
            manifestStream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
            cancellationToken) ?? throw new InvalidDataException("検証マニフェストを読み取れません。");

        if (manifest.Version != 1 || string.IsNullOrWhiteSpace(manifest.SourcePath) ||
            string.IsNullOrWhiteSpace(manifest.WorkspacePath) ||
            !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(manifest.WorkspacePath)), root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("検証マニフェストが選択した作業用コピーと一致しません。");
        }

        var sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(manifest.SourcePath));
        if (IsSameOrSubPath(root, sourceRoot) || IsSameOrSubPath(sourceRoot, root))
        {
            throw new InvalidDataException("保存用原本と作業用コピーが分離されていません。");
        }

        var sourceReport = await inspector.InspectAsync(
            sourceRoot,
            new Progress<AbacusInspectionProgress>(item =>
                progress?.Report(new AbacusWorkspaceProgress("保存用原本確認中", item.CompletedFiles, item.TotalFiles, item.CurrentFile))),
            cancellationToken);
        if (!sourceReport.IsValid || sourceReport.FileCount != manifest.FileCount ||
            sourceReport.TotalBytes != manifest.TotalBytes ||
            !string.Equals(sourceReport.FolderFingerprint, manifest.FolderFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidDataException("保存用原本が作業用コピー作成時の検証結果と一致しません。");
        }

        var report = await inspector.InspectAsync(
            root,
            new Progress<AbacusInspectionProgress>(item =>
                progress?.Report(new AbacusWorkspaceProgress("作業用コピー再検証中", item.CompletedFiles, item.TotalFiles, item.CurrentFile))),
            cancellationToken);
        if (!report.IsValid || report.FileCount != manifest.FileCount || report.TotalBytes != manifest.TotalBytes)
        {
            throw new InvalidDataException("作業用コピーが作成時の検証結果と一致しません。起動対象にできません。");
        }

        var sourceFiles = sourceReport.Files.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        var workspaceFiles = report.Files.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        if (sourceFiles.Count != workspaceFiles.Count || sourceFiles.Keys.Any(path => !workspaceFiles.ContainsKey(path)))
        {
            throw new InvalidDataException("作業用コピーのファイル構成が保存用原本と一致しません。");
        }

        var allowedRuntimeChanges = new List<string>();
        var totalRuntimeChangedBytes = 0;
        foreach (var (relativePath, sourceFile) in sourceFiles)
        {
            var workspaceFile = workspaceFiles[relativePath];
            if (string.Equals(sourceFile.Sha256, workspaceFile.Sha256, StringComparison.Ordinal))
            {
                continue;
            }

            if (!RuntimeMutableFilePattern.IsMatch(relativePath) ||
                sourceFile.Length != workspaceFile.Length)
            {
                throw new InvalidDataException($"許可されていない変更があります: {relativePath}");
            }

            var changedBytes = await CountChangedBytesAsync(
                SafeCombine(sourceRoot, relativePath),
                SafeCombine(root, relativePath),
                MaximumRuntimeChangedBytesPerFile,
                cancellationToken);
            if (changedBytes > MaximumRuntimeChangedBytesPerFile)
            {
                throw new InvalidDataException($"{relativePath}の変更量が起動時管理情報の上限を超えています。");
            }

            totalRuntimeChangedBytes += changedBytes;
            if (totalRuntimeChangedBytes > MaximumRuntimeChangedBytesTotal)
            {
                throw new InvalidDataException("FileMakerデータファイルの合計変更量が起動時管理情報の上限を超えています。");
            }

            allowedRuntimeChanges.Add($"{relativePath}（{changedBytes:N0}バイト差分）");
        }

        return new AbacusWorkspaceVerificationResult(
            root,
            manifestPath,
            report,
            manifest.FolderFingerprint,
            allowedRuntimeChanges);
    }

    private static async Task<int> CountChangedBytesAsync(
        string sourcePath,
        string workspacePath,
        int stopAfter,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        await using var workspace = new FileStream(workspacePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        var sourceBuffer = new byte[64 * 1024];
        var workspaceBuffer = new byte[64 * 1024];
        var changedBytes = 0;
        while (true)
        {
            var sourceRead = await source.ReadAsync(sourceBuffer, cancellationToken);
            var workspaceRead = await workspace.ReadAsync(workspaceBuffer, cancellationToken);
            if (sourceRead != workspaceRead)
            {
                return stopAfter + 1;
            }

            if (sourceRead == 0)
            {
                return changedBytes;
            }

            for (var index = 0; index < sourceRead; index++)
            {
                if (sourceBuffer[index] != workspaceBuffer[index] && ++changedBytes > stopAfter)
                {
                    return changedBytes;
                }
            }
        }
    }

    private static async Task VerifyFileHashAsync(string path, string expectedHash, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"コピーしたファイルのハッシュが一致しません: {Path.GetFileName(path)}");
        }
    }

    private static string CreateUniqueWorkspacePath(string destinationRoot)
    {
        var baseName = $"ABACUS-Work-{DateTime.Now:yyyyMMdd-HHmmss}";
        for (var suffix = 0; suffix < 1000; suffix++)
        {
            var name = suffix == 0 ? baseName : $"{baseName}-{suffix}";
            var candidate = Path.Combine(destinationRoot, name);
            if (!Directory.Exists(candidate) && !File.Exists(candidate) && !File.Exists($"{candidate}.manifest.json"))
            {
                return candidate;
            }
        }

        throw new IOException("一意な作業用フォルダー名を作成できませんでした。");
    }

    private static string SafeCombine(string root, string relativePath)
    {
        var combined = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!IsSameOrSubPath(combined, root))
        {
            throw new InvalidDataException("フォルダー外を参照するパスが含まれています。");
        }

        return combined;
    }

    private static bool IsSameOrSubPath(string candidate, string root)
    {
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
            normalizedCandidate.StartsWith($"{normalizedRoot}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasSameSourceMetadata(AbacusFolderReport before, AbacusFolderReport after)
    {
        if (before.Files.Count != after.Files.Count)
        {
            return false;
        }

        var afterFiles = after.Files.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        return before.Files.All(file =>
            afterFiles.TryGetValue(file.RelativePath, out var candidate) &&
            candidate.Length == file.Length &&
            candidate.LastWriteTimeUtc == file.LastWriteTimeUtc);
    }
}
