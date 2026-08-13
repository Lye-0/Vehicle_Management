using System.Collections.ObjectModel;

namespace VehicleManagement.AbacusImport;

/// <summary>
/// FP5の論理sector順に並べたデータtokenを、値を変更せずに読み出します。
/// Gate 19では、この低レベル結果をもとにABACUSの明細レコードを再構成します。
/// </summary>
public sealed record AbacusFp5RawToken(
    IReadOnlyList<byte[]> Path,
    byte[] Reference,
    byte[] Data,
    ulong? SegmentCounter);

public sealed record AbacusFp5RawTokenStream(
    string SourceFilePath,
    int SectorCount,
    int DataSectorCount,
    IReadOnlyList<AbacusFp5RawToken> Tokens);

public sealed class AbacusFp5RawTokenReader
{
    private const int SectorBytes = 1024;
    private const long MaximumSourceFileBytes = 2L * 1024 * 1024 * 1024;

    public Task<AbacusFp5RawTokenStream> ReadAsync(
        string sourceFilePath,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Read(sourceFilePath, cancellationToken), cancellationToken);

    private static AbacusFp5RawTokenStream Read(string sourceFilePath, CancellationToken cancellationToken)
    {
        var sourcePath = Path.GetFullPath(sourceFilePath);
        var source = new FileInfo(sourcePath);
        if (!source.Exists) throw new FileNotFoundException("FP5/UCSファイルが見つかりません。", sourcePath);
        if (source.Attributes.HasFlag(FileAttributes.ReparsePoint) || source.Directory?.Attributes.HasFlag(FileAttributes.ReparsePoint) == true)
        {
            throw new InvalidOperationException("リンクまたは再解析ポイントのFP5/UCSファイルは読み取れません。");
        }
        if (source.Length < 5L * SectorBytes || source.Length > MaximumSourceFileBytes || source.Length % SectorBytes != 0)
        {
            throw new InvalidDataException("FP5/UCSファイルのサイズまたは1024-byte sector境界が不正です。");
        }
        if (!source.Extension.Equals(".ucs", StringComparison.OrdinalIgnoreCase) &&
            !source.Extension.Equals(".fp5", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("入力ファイルの拡張子は.ucsまたは.fp5である必要があります。");
        }

        using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.RandomAccess);
        var catalog = AbacusFp5ImageRestorer.ReadSectorCatalog(stream, cancellationToken);
        var dataSectors = AbacusFp5ImageRestorer.OrderDataSectors(stream, catalog, cancellationToken);
        var tokens = new List<AbacusFp5RawToken>();
        AbacusFp5ImageRestorer.ParseImageNodes(
            stream,
            dataSectors,
            static (_, _, _) => { },
            new Dictionary<string, AbacusFp5ImageRestorer.InternalVehicleRecordBuilder>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<byte, byte[]>(),
            false,
            cancellationToken,
            token => tokens.Add(token));
        return new AbacusFp5RawTokenStream(sourcePath, checked((int)(source.Length / SectorBytes)), dataSectors.Count, new ReadOnlyCollection<AbacusFp5RawToken>(tokens));
    }
}
