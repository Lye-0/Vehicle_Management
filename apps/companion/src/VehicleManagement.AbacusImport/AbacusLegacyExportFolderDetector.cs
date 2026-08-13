namespace VehicleManagement.AbacusImport;

public sealed record AbacusLegacyExportFolderDetection(
    string RootPath,
    IReadOnlyList<string> CandidateFolders,
    bool ScanLimitReached)
{
    public bool HasUniqueCandidate => CandidateFolders.Count == 1 && !ScanLimitReached;

    public string? UniqueFolderPath => HasUniqueCandidate ? CandidateFolders[0] : null;
}

/// <summary>
/// 保存用ABACUSフォルダー内に既に出力されている固定列CSVのフォルダーを、読み取り専用で探します。
/// 再解析ポイントやシンボリックリンクは辿らず、深さと走査ディレクトリ数に上限を設けます。
/// </summary>
public sealed class AbacusLegacyExportFolderDetector
{
    private const int MaximumDepth = 3;
    private const int MaximumDirectories = 2_048;

    private static readonly HashSet<string> KnownFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "hanbai.csv",
        "seibi.csv",
        "syaryou.csv",
        "syaryou2.csv",
    };

    private static readonly string[] KnownAliasSuffixes = ["_hanbai", "-hanbai", "_seibi", "-seibi", "_syaryou", "-syaryou"];

    public Task<AbacusLegacyExportFolderDetection> DetectAsync(
        string rootPath,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Detect(rootPath, cancellationToken), cancellationToken);

    private static AbacusLegacyExportFolderDetection Detect(
        string rootPath,
        CancellationToken cancellationToken)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var rootDirectory = new DirectoryInfo(root);
        if (!rootDirectory.Exists || rootDirectory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("CSV自動検出の対象フォルダーを読み取れません。");
        }

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<(DirectoryInfo Directory, int Depth)>();
        pending.Enqueue((rootDirectory, 0));
        var scannedDirectories = 0;
        var scanLimitReached = false;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, depth) = pending.Dequeue();
            scannedDirectories++;
            if (scannedDirectories > MaximumDirectories)
            {
                scanLimitReached = true;
                break;
            }

            bool containsKnownCsv;
            try
            {
                var csvFiles = directory.EnumerateFiles()
                    .Where(file => !file.Attributes.HasFlag(FileAttributes.ReparsePoint) &&
                                   string.Equals(file.Extension, ".csv", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                containsKnownCsv = csvFiles.Any(file => KnownFileNames.Contains(file.Name) || IsKnownAlias(file.Name)) ||
                                   csvFiles.Length >= 3;
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            if (containsKnownCsv)
            {
                candidates.Add(directory.FullName);
            }

            if (depth >= MaximumDepth)
            {
                continue;
            }

            IEnumerable<DirectoryInfo> childDirectories;
            try
            {
                childDirectories = directory.EnumerateDirectories()
                    .Where(child => !child.Attributes.HasFlag(FileAttributes.ReparsePoint));
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in childDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pending.Enqueue((child, depth + 1));
            }
        }

        return new AbacusLegacyExportFolderDetection(
            root,
            candidates.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            scanLimitReached);
    }

    private static bool IsKnownAlias(string fileName)
    {
        if (!string.Equals(Path.GetExtension(fileName), ".csv", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        return KnownAliasSuffixes.Any(suffix => stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }
}
