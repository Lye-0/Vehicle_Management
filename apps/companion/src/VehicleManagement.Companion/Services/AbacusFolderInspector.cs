using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VehicleManagement.Companion.Services;

public static class AbacusConstants
{
    public const string ExecutableFileName = "ABACUS カーショップPRO④.EXE";

    public static readonly IReadOnlyList<string> RequiredFiles =
    [
        ExecutableFileName,
        "abx-cs-mn.ucs",
        "abx-cs-sk.ucs",
        "BackUp-2.tab",
        "BackUp-3.tab",
        "BackUp-5.fp5",
    ];
}

public sealed record AbacusFileFingerprint(
    string RelativePath,
    long Length,
    DateTime LastWriteTimeUtc,
    string Sha256);

public sealed record AbacusInspectionProgress(int CompletedFiles, int TotalFiles, string CurrentFile);

public sealed record AbacusFolderReport(
    string SourcePath,
    int FileCount,
    long TotalBytes,
    string FolderFingerprint,
    string ExecutableArchitecture,
    IReadOnlyList<AbacusFileFingerprint> Files,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public bool IsValid => Errors.Count == 0;
}

public sealed class AbacusFolderInspector
{
    public async Task<AbacusFolderReport> InspectAsync(
        string sourcePath,
        IProgress<AbacusInspectionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourcePath));
        if (!Directory.Exists(root))
        {
            return Invalid(root, "指定されたフォルダーが見つかりません。");
        }

        var errors = new List<string>();
        var warnings = new List<string>();
        var rootInfo = new DirectoryInfo(root);
        if (rootInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            errors.Add("リンクまたは再解析ポイントのフォルダーは使用できません。");
        }

        List<FileInfo> files;
        try
        {
            files = EnumerateFilesWithoutFollowingLinks(rootInfo, errors)
                .OrderBy(file => Path.GetRelativePath(root, file.FullName), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Invalid(root, $"フォルダーを読み取れません: {exception.Message}");
        }

        if (files.Any(file => file.Attributes.HasFlag(FileAttributes.ReparsePoint)))
        {
            errors.Add("フォルダー内にリンクまたは再解析ポイントのファイルが含まれています。");
        }

        foreach (var requiredFile in AbacusConstants.RequiredFiles)
        {
            if (!files.Any(file => string.Equals(file.Name, requiredFile, StringComparison.OrdinalIgnoreCase) &&
                                   string.Equals(file.DirectoryName, root, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"必須ファイルがありません: {requiredFile}");
            }
        }

        var fingerprints = new List<AbacusFileFingerprint>(files.Count);
        for (var index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[index];
            var relativePath = Path.GetRelativePath(root, file.FullName);
            if (relativePath.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
            {
                errors.Add($"フォルダー外を参照するファイルがあります: {file.Name}");
                continue;
            }

            progress?.Report(new AbacusInspectionProgress(index, files.Count, relativePath));
            await using var stream = new FileStream(
                file.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            fingerprints.Add(new AbacusFileFingerprint(
                relativePath,
                file.Length,
                file.LastWriteTimeUtc,
                Convert.ToHexString(hash)));
        }

        progress?.Report(new AbacusInspectionProgress(files.Count, files.Count, "完了"));
        var executablePath = Path.Combine(root, AbacusConstants.ExecutableFileName);
        var architecture = File.Exists(executablePath) ? ReadPeArchitecture(executablePath) : "不明";
        if (File.Exists(executablePath) && architecture != "x86")
        {
            warnings.Add($"ABACUS実行ファイルの想定アーキテクチャはx86ですが、{architecture}と判定されました。");
        }

        if (files.Count != 51)
        {
            warnings.Add($"確認済み原本は51ファイルですが、このフォルダーには{files.Count}ファイルあります。");
        }

        return new AbacusFolderReport(
            root,
            fingerprints.Count,
            fingerprints.Sum(file => file.Length),
            CalculateFolderFingerprint(fingerprints),
            architecture,
            fingerprints,
            errors,
            warnings);
    }

    private static string CalculateFolderFingerprint(IEnumerable<AbacusFileFingerprint> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var line = $"{file.RelativePath}\0{file.Length}\0{file.Sha256}\n";
            hash.AppendData(Encoding.UTF8.GetBytes(line));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static IEnumerable<FileInfo> EnumerateFilesWithoutFollowingLinks(
        DirectoryInfo root,
        ICollection<string> errors)
    {
        var pending = new Queue<DirectoryInfo>();
        pending.Enqueue(root);
        while (pending.Count > 0)
        {
            var directory = pending.Dequeue();
            foreach (var child in directory.EnumerateDirectories("*", SearchOption.TopDirectoryOnly))
            {
                if (child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    errors.Add($"リンクまたは再解析ポイントのフォルダーがあります: {child.Name}");
                    continue;
                }

                pending.Enqueue(child);
            }

            foreach (var file in directory.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
            {
                if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    errors.Add($"リンクまたは再解析ポイントのファイルがあります: {file.Name}");
                    continue;
                }

                yield return file;
            }
        }
    }

    private static string ReadPeArchitecture(string executablePath)
    {
        try
        {
            using var stream = File.Open(executablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> header = stackalloc byte[64];
            if (stream.Read(header) != header.Length || header[0] != 'M' || header[1] != 'Z')
            {
                return "不明";
            }

            var peOffset = BinaryPrimitives.ReadInt32LittleEndian(header[0x3c..]);
            stream.Position = peOffset;
            Span<byte> peHeader = stackalloc byte[6];
            if (stream.Read(peHeader) != peHeader.Length ||
                peHeader[0] != 'P' || peHeader[1] != 'E' || peHeader[2] != 0 || peHeader[3] != 0)
            {
                return "不明";
            }

            return BinaryPrimitives.ReadUInt16LittleEndian(peHeader[4..]) switch
            {
                0x014c => "x86",
                0x8664 => "x64",
                0xaa64 => "arm64",
                _ => "不明",
            };
        }
        catch (IOException)
        {
            return "不明";
        }
    }

    private static AbacusFolderReport Invalid(string sourcePath, string error) =>
        new(sourcePath, 0, 0, string.Empty, "不明", [], [error], []);
}
