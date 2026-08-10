using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

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

public sealed class AbacusWorkspaceService(AbacusFolderInspector inspector)
{
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
