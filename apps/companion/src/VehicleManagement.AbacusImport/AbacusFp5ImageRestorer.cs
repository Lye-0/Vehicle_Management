using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;

namespace VehicleManagement.AbacusImport;

public sealed record AbacusFp5RestoredImage(
    int Index,
    string ImageIdHex,
    ulong? LogicalImageId,
    string RelativePath,
    long FileSize,
    long ExpectedFileSize,
    int PixelWidth,
    int PixelHeight,
    string Sha256);

public sealed record AbacusFp5ImageRestoration(
    string SourceFilePath,
    string OutputFolderPath,
    string ReportPath,
    long SourceFileSize,
    string SourceSha256,
    int SectorCount,
    int BlockChainLevels,
    int DataBlockCount,
    int ImageNodeCount,
    int RestoredImageCount,
    int LengthCheckMatchCount,
    int DecodeSuccessCount,
    int UniqueImageSha256Count,
    IReadOnlyList<AbacusFp5RestoredImage> Images)
{
    public bool IsValid =>
        ImageNodeCount > 0 &&
        ImageNodeCount == RestoredImageCount &&
        RestoredImageCount == LengthCheckMatchCount &&
        LengthCheckMatchCount == DecodeSuccessCount;
}

internal sealed record AbacusFp5InternalVehicleRecord(
    string RecordIdHex,
    byte[] RegistrationNumberBytes,
    byte[] ChassisNumberBytes,
    byte[] ImageReferenceBytes);

internal sealed record AbacusFp5VehicleImageSource(
    AbacusFp5ImageRestoration Restoration,
    IReadOnlyList<AbacusFp5InternalVehicleRecord> VehicleRecords,
    IReadOnlySet<string> GifImageIds,
    IReadOnlyDictionary<byte, byte[]> VehicleFieldNames);

/// <summary>
/// FileMaker Pro 5の1024-byte sector、リンク順、token path、分割data tokenを
/// 読み取り専用で復元し、1F/05/&lt;image id&gt;/JPEGノードを書き出します。
/// セクターとtokenの解釈は、公開されているFP5 grammarおよび実装
/// https://github.com/qwesda/fp5dump と実ファイルの観測結果を照合しています。
/// </summary>
public sealed class AbacusFp5ImageRestorer
{
    private const int SectorBytes = 1024;
    private const int SectorHeaderBytes = 14;
    private const int MaximumSectorPayloadBytes = SectorBytes - SectorHeaderBytes;
    private const long MaximumSourceFileBytes = 2L * 1024 * 1024 * 1024;
    private const long MaximumImageBytes = 256L * 1024 * 1024;
    private const long MaximumPixels = 50_000_000;
    private const int MaximumImages = 20_000;
    private const int MaximumPathDepth = 256;
    private static readonly byte[] Magic =
        [0x00, 0x01, 0x00, 0x00, 0x00, 0x02, 0x00, 0x01, 0x00, 0x05, 0x00, 0x02, 0x00, 0x02, 0xC0];
    private static readonly byte[] Version = Encoding.ASCII.GetBytes("Pro 5.0");
    private static readonly byte[] JpegNodeName = Encoding.ASCII.GetBytes("JPEG");

    public Task<AbacusFp5ImageRestoration> RestoreAsync(
        string sourceFilePath,
        string outputParentFolder,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () => Restore(sourceFilePath, outputParentFolder, collectVehicleMapping: false, cancellationToken).Restoration,
            cancellationToken);

    internal Task<AbacusFp5VehicleImageSource> RestoreForVehicleMappingAsync(
        string sourceFilePath,
        string outputParentFolder,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () => Restore(sourceFilePath, outputParentFolder, collectVehicleMapping: true, cancellationToken),
            cancellationToken);

    private static AbacusFp5VehicleImageSource Restore(
        string sourceFilePath,
        string outputParentFolder,
        bool collectVehicleMapping,
        CancellationToken cancellationToken)
    {
        var sourcePath = Path.GetFullPath(sourceFilePath);
        var outputParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputParentFolder));
        var sourceFile = new FileInfo(sourcePath);
        ValidatePaths(sourceFile, outputParent);
        var sourceLastWriteTimeUtc = sourceFile.LastWriteTimeUtc;

        var outputRoot = CreateOutputRoot(outputParent);
        var sourceDirectory = Path.TrimEndingDirectorySeparator(sourceFile.DirectoryName!);
        if (IsInside(outputRoot, sourceDirectory))
        {
            throw new InvalidOperationException("復元先は入力FP5/UCSファイルのフォルダー外を指定してください。");
        }

        Directory.CreateDirectory(outputRoot);
        var imagesPath = Path.Combine(outputRoot, "images");
        Directory.CreateDirectory(imagesPath);

        using var stream = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.RandomAccess);

        cancellationToken.ThrowIfCancellationRequested();
        var sourceSha256 = Convert.ToHexString(SHA256.HashData(stream));
        stream.Position = 0;
        var catalog = ReadSectorCatalog(stream, cancellationToken);
        var dataSectorPositions = OrderDataSectors(stream, catalog, cancellationToken);
        var restoredImages = new List<AbacusFp5RestoredImage>();
        var vehicleRecordBuilders = new Dictionary<string, InternalVehicleRecordBuilder>(StringComparer.Ordinal);
        var gifImageIds = new HashSet<string>(StringComparer.Ordinal);
        var vehicleFieldNames = new Dictionary<byte, byte[]>();
        var uniqueHashes = new HashSet<string>(StringComparer.Ordinal);

        ParseImageNodes(
            stream,
            dataSectorPositions,
            (imageId, expectedLength, jpegBytes) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (restoredImages.Count >= MaximumImages)
                {
                    throw new InvalidDataException($"JPEG画像ノード数が上限{MaximumImages:N0}件を超えています。");
                }

                var imageIndex = restoredImages.Count + 1;
                var idHex = Convert.ToHexString(imageId);
                var logicalId = DecodeVariableLengthInteger(imageId, subtract64: false);
                var fileName = logicalId is null
                    ? $"fp5-image-{imageIndex:D4}-{idHex}.jpg"
                    : $"fp5-image-{imageIndex:D4}-{logicalId.Value}.jpg";
                var outputPath = Path.Combine(imagesPath, fileName);
                var relativePath = Path.GetRelativePath(outputRoot, outputPath).Replace('\\', '/');
                var dimensions = ValidateDecodedJpeg(jpegBytes);
                var sha256 = Convert.ToHexString(SHA256.HashData(jpegBytes.Span));

                using (var output = new FileStream(
                    outputPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 1024 * 1024,
                    FileOptions.SequentialScan))
                {
                    output.Write(jpegBytes.Span);
                    output.Flush(flushToDisk: true);
                }

                uniqueHashes.Add(sha256);
                restoredImages.Add(new AbacusFp5RestoredImage(
                    imageIndex,
                    idHex,
                    logicalId,
                    relativePath,
                    jpegBytes.Length,
                    expectedLength,
                    dimensions.Width,
                    dimensions.Height,
                    sha256));
            },
            vehicleRecordBuilders,
            gifImageIds,
            vehicleFieldNames,
            collectVehicleMapping,
            cancellationToken);

        if (stream.Length != sourceFile.Length || sourceLastWriteTimeUtc != new FileInfo(sourcePath).LastWriteTimeUtc)
        {
            throw new IOException("復元中に入力FP5/UCSファイルが変化しました。ABACUSを終了して再試行してください。");
        }

        var reportPath = Path.Combine(outputRoot, "fp5-image-restoration-report.json");
        var result = new AbacusFp5ImageRestoration(
            sourcePath,
            outputRoot,
            reportPath,
            sourceFile.Length,
            sourceSha256,
            checked((int)(sourceFile.Length / SectorBytes)),
            catalog.BlockChainLevels,
            dataSectorPositions.Count,
            restoredImages.Count,
            restoredImages.Count,
            restoredImages.Count,
            restoredImages.Count,
            uniqueHashes.Count,
            restoredImages);

        if (!result.IsValid)
        {
            throw new InvalidDataException("FP5画像復元の検証件数が一致しませんでした。");
        }

        WriteReport(result, reportPath);
        var vehicleRecords = collectVehicleMapping
            ? vehicleRecordBuilders.Values.Select(record => record.Complete()).ToList()
            : [];
        return new AbacusFp5VehicleImageSource(result, vehicleRecords, gifImageIds, vehicleFieldNames);
    }

    private static void ValidatePaths(FileInfo sourceFile, string outputParent)
    {
        if (!sourceFile.Exists)
        {
            throw new FileNotFoundException("入力FP5/UCSファイルが見つかりません。", sourceFile.FullName);
        }

        if (sourceFile.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("リンクまたは再解析ポイントのFP5/UCSファイルは復元できません。");
        }

        if (sourceFile.Directory is null ||
            sourceFile.Directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("リンクまたは再解析ポイントの入力フォルダーは使用できません。");
        }

        if (sourceFile.Length < 5L * SectorBytes ||
            sourceFile.Length > MaximumSourceFileBytes ||
            sourceFile.Length % SectorBytes != 0)
        {
            throw new InvalidDataException("FP5/UCSファイルのサイズまたは1024-byte sector境界が不正です。");
        }

        if (!Directory.Exists(outputParent))
        {
            throw new DirectoryNotFoundException("復元先の親フォルダーが見つかりません。");
        }

        if (new DirectoryInfo(outputParent).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("リンクまたは再解析ポイントの復元先は使用できません。");
        }

        var extension = sourceFile.Extension;
        if (!extension.Equals(".ucs", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".fp5", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("入力ファイルの拡張子は.ucsまたは.fp5である必要があります。");
        }
    }

    internal static SectorCatalog ReadSectorCatalog(FileStream stream, CancellationToken cancellationToken)
    {
        var header = new byte[SectorBytes];
        ReadExactlyAt(stream, 0, header);
        if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic) ||
            header[541] != Version.Length ||
            !header.AsSpan(542, Version.Length).SequenceEqual(Version))
        {
            throw new InvalidDataException("FileMaker Pro 5.0のヘッダーを確認できませんでした。");
        }

        var topHeader = ReadSectorHeader(stream, 2L * SectorBytes);
        if (topHeader.DeletedFlag == 0xFF || topHeader.PreviousId != 0)
        {
            throw new InvalidDataException("FP5最上位index sectorのヘッダーが不正です。");
        }

        var levels = topHeader.Level;
        if (levels is < 1 or > 16)
        {
            throw new InvalidDataException("FP5 block chainの階層数が許容範囲外です。");
        }

        var sectorCount = stream.Length / SectorBytes;
        var largestBlockId = topHeader.NextId;
        if (largestBlockId == 0 || largestBlockId > int.MaxValue || largestBlockId >= sectorCount)
        {
            throw new InvalidDataException("FP5 block IDの上限値がファイル範囲外です。");
        }

        var previousIdToPosition = new long[checked((int)largestBlockId + 1)];
        var firstPositions = new long[levels + 1];
        var counts = new int[levels + 1];
        firstPositions[levels] = 2L * SectorBytes;
        counts[levels] = 1;

        for (var position = 3L * SectorBytes; position < stream.Length; position += SectorBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sector = ReadSectorHeader(stream, position);
            if (sector.DeletedFlag == 0xFF)
            {
                continue;
            }

            if (sector.Level > levels)
            {
                throw new InvalidDataException($"FP5 sector {position / SectorBytes:N0}の階層値が不正です。");
            }

            counts[sector.Level] = checked(counts[sector.Level] + 1);
            if (sector.PreviousId == 0)
            {
                if (firstPositions[sector.Level] != 0)
                {
                    throw new InvalidDataException($"FP5 level {sector.Level}に複数の先頭sectorがあります。");
                }

                firstPositions[sector.Level] = position;
                continue;
            }

            if (sector.PreviousId > largestBlockId)
            {
                throw new InvalidDataException("FP5 sectorのprevious block IDが上限を超えています。");
            }

            ref var mappedPosition = ref previousIdToPosition[sector.PreviousId];
            if (mappedPosition != 0)
            {
                throw new InvalidDataException($"FP5 previous block ID {sector.PreviousId:N0}が重複しています。");
            }

            mappedPosition = position;
        }

        for (var level = 0; level < levels; level++)
        {
            if (counts[level] == 0 || firstPositions[level] == 0)
            {
                throw new InvalidDataException($"FP5 level {level}のblock chainが見つかりません。");
            }
        }

        return new SectorCatalog(
            levels,
            largestBlockId,
            previousIdToPosition,
            firstPositions,
            counts);
    }

    internal static IReadOnlyList<long> OrderDataSectors(
        FileStream stream,
        SectorCatalog catalog,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<long> ordered = [catalog.FirstPositions[catalog.BlockChainLevels]];
        for (var level = catalog.BlockChainLevels - 1; level >= 0; level--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parentFirstPosition = catalog.FirstPositions[level + 1];
            var firstChildId = ReadFirstChildBlockId(stream, parentFirstPosition);
            ordered = OrderBlockChain(stream, catalog, level, firstChildId, cancellationToken);
        }

        return ordered;
    }

    private static IReadOnlyList<long> OrderBlockChain(
        FileStream stream,
        SectorCatalog catalog,
        int level,
        uint firstBlockId,
        CancellationToken cancellationToken)
    {
        var expectedCount = catalog.Counts[level];
        var positions = new List<long>(expectedCount);
        var seenIds = new HashSet<uint>();
        uint? nextBlockPreviousId = firstBlockId;
        uint currentBlockPreviousId = 0;
        uint previousBlockNextId = 0;

        while (nextBlockPreviousId is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (positions.Count >= expectedCount)
            {
                throw new InvalidDataException($"FP5 level {level}のblock chainが循環しています。");
            }

            var blockPosition = currentBlockPreviousId == 0
                ? catalog.FirstPositions[level]
                : GetMappedPosition(catalog, currentBlockPreviousId);
            var currentHeader = ReadSectorHeader(stream, blockPosition);
            if (currentHeader.Level != level)
            {
                throw new InvalidDataException("FP5 block chainのsector階層が一致しません。");
            }

            uint? nextProbe;
            if (currentHeader.NextId != 0)
            {
                var probePosition = GetMappedPosition(catalog, nextBlockPreviousId.Value);
                nextProbe = ReadSectorHeader(stream, probePosition).PreviousId;
            }
            else
            {
                nextProbe = null;
            }

            uint currentBlockId;
            if (nextProbe is not null)
            {
                currentBlockId = nextProbe.Value;
                nextBlockPreviousId = currentHeader.NextId;
            }
            else
            {
                currentBlockId = previousBlockNextId;
                nextBlockPreviousId = null;
            }

            if (!seenIds.Add(currentBlockId))
            {
                throw new InvalidDataException($"FP5 level {level}でblock ID {currentBlockId:N0}が重複しています。");
            }

            positions.Add(blockPosition);
            currentBlockPreviousId = currentBlockId;
            previousBlockNextId = currentHeader.NextId;
        }

        if (positions.Count != expectedCount)
        {
            throw new InvalidDataException(
                $"FP5 level {level}の論理sector数が一致しません（期待 {expectedCount:N0} / 復元 {positions.Count:N0}）。");
        }

        return positions;
    }

    private static long GetMappedPosition(SectorCatalog catalog, uint blockId)
    {
        if (blockId > catalog.LargestBlockId)
        {
            throw new InvalidDataException("FP5 block IDが上限を超えています。");
        }

        var position = catalog.PreviousIdToPosition[blockId];
        if (position == 0)
        {
            throw new InvalidDataException($"FP5 block ID {blockId:N0}に対応するsectorがありません。");
        }

        return position;
    }

    private static uint ReadFirstChildBlockId(FileStream stream, long parentFirstPosition)
    {
        Span<byte> bytes = stackalloc byte[6];
        ReadExactlyAt(stream, parentFirstPosition + SectorHeaderBytes, bytes);
        if (bytes[0] != 0x00 || bytes[1] != 0x04)
        {
            throw new InvalidDataException("FP5 index sectorの先頭child参照が不正です。");
        }

        return BinaryPrimitives.ReadUInt32BigEndian(bytes[2..]);
    }

    internal static void ParseImageNodes(
        FileStream stream,
        IReadOnlyList<long> dataSectorPositions,
        Action<byte[], long, ReadOnlyMemory<byte>> onImage,
        IDictionary<string, InternalVehicleRecordBuilder> vehicleRecords,
        ISet<string> gifImageIds,
        IDictionary<byte, byte[]> vehicleFieldNames,
        bool collectVehicleMapping,
        CancellationToken cancellationToken,
        Action<AbacusFp5RawToken>? onToken = null)
    {
        var path = new List<byte[]>();
        ImageNodeBuilder? image = null;
        var imageIds = new HashSet<string>(StringComparer.Ordinal);
        var payload = new byte[MaximumSectorPayloadBytes];

        for (var sectorIndex = 0; sectorIndex < dataSectorPositions.Count; sectorIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var position = dataSectorPositions[sectorIndex];
            var header = ReadSectorHeader(stream, position);
            ReadExactlyAt(stream, position + SectorHeaderBytes, payload.AsSpan(0, header.PayloadLength));
            var cursor = sectorIndex == 0 ? 0 : header.SkipBytes - 1;
            if (cursor < 0 || cursor > header.PayloadLength)
            {
                throw new InvalidDataException($"FP5 data sector {position / SectorBytes:N0}のskip値が不正です。");
            }

            while (cursor < header.PayloadLength)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var token = payload[cursor];

                if (token == 0x01 &&
                    cursor + 8 <= header.PayloadLength &&
                    payload[cursor + 1] == 0xFF &&
                    payload[cursor + 2] == 0x05)
                {
                    if (image is not null && IsJpegPath(path))
                    {
                        image.ApplyLengthCheck(ReadUInt40BigEndian(payload.AsSpan(cursor + 3, 5)));
                    }

                    cursor += 8;
                    continue;
                }

                if (token is >= 0x01 and <= 0x3F)
                {
                    var referenceLength = token;
                    EnsureAvailable(cursor, 2 + referenceLength, header.PayloadLength);
                    var dataLength = payload[cursor + 1 + referenceLength];
                    var dataStart = cursor + 2 + referenceLength;
                    EnsureAvailable(dataStart, dataLength, header.PayloadLength);
                    if (image is not null && IsJpegPath(path))
                    {
                        var reference = payload.AsSpan(cursor + 1, referenceLength);
                        if (image.HasSegments)
                        {
                            image.AppendSegment(
                                DecodeVariableLengthInteger(reference, subtract64: false),
                                payload.AsSpan(dataStart, dataLength));
                        }
                        else if (reference.Length == 1 && reference[0] == 0x01)
                        {
                            image.SetSinglePayload(payload.AsSpan(dataStart, dataLength));
                        }
                    }

                    if (collectVehicleMapping)
                    {
                        CaptureVehicleField(
                            path,
                            payload.AsSpan(cursor + 1, referenceLength),
                            payload.AsSpan(dataStart, dataLength),
                            vehicleRecords);
                        CaptureVehicleFieldName(
                            path,
                            payload.AsSpan(cursor + 1, referenceLength),
                            payload.AsSpan(dataStart, dataLength),
                            vehicleFieldNames);
                    }

                    onToken?.Invoke(new AbacusFp5RawToken(
                        path.Select(component => component.ToArray()).ToArray(),
                        payload.AsSpan(cursor + 1, referenceLength).ToArray(),
                        payload.AsSpan(dataStart, dataLength).ToArray(),
                        null));

                    cursor = dataStart + dataLength;
                    continue;
                }

                if (token is >= 0x40 and <= 0x7F)
                {
                    EnsureAvailable(cursor, 2, header.PayloadLength);
                    var dataLength = payload[cursor + 1];
                    var dataStart = cursor + 2;
                    EnsureAvailable(dataStart, dataLength, header.PayloadLength);
                    if (image is not null && IsJpegPath(path))
                    {
                        if (image.HasSegments)
                        {
                            image.AppendSegment((ulong)(token - 0x40), payload.AsSpan(dataStart, dataLength));
                        }
                        else if ((token & 0xBF) == 0x01)
                        {
                            image.SetSinglePayload(payload.AsSpan(dataStart, dataLength));
                        }
                    }

                    byte[] reference = [(byte)(token - 0x40)];
                    if (collectVehicleMapping)
                    {
                        CaptureVehicleField(
                            path,
                            reference,
                            payload.AsSpan(dataStart, dataLength),
                            vehicleRecords);
                        CaptureVehicleFieldName(
                            path,
                            reference,
                            payload.AsSpan(dataStart, dataLength),
                            vehicleFieldNames);
                    }

                    onToken?.Invoke(new AbacusFp5RawToken(
                        path.Select(component => component.ToArray()).ToArray(),
                        reference,
                        payload.AsSpan(dataStart, dataLength).ToArray(),
                        null));

                    cursor = dataStart + dataLength;
                    continue;
                }

                if (token is >= 0x81 and <= 0xBF)
                {
                    var tokenLength = 1 + token - 0x80;
                    EnsureAvailable(cursor, tokenLength, header.PayloadLength);
                    cursor += tokenLength;
                    continue;
                }

                if (token == 0xC0)
                {
                    if (path.Count == 0)
                    {
                        if (sectorIndex == dataSectorPositions.Count - 1 && cursor + 1 == header.PayloadLength)
                        {
                            return;
                        }

                        throw new InvalidDataException("FP5 token pathがルートより上へpopしました。");
                    }

                    if (image is not null && IsJpegPath(path))
                    {
                        var imageId = path[2];
                        var imageIdHex = Convert.ToHexString(imageId);
                        if (!imageIds.Add(imageIdHex))
                        {
                            throw new InvalidDataException($"FP5 JPEG image ID {imageIdHex}が重複しています。");
                        }

                        image.Complete((expectedLength, bytes) => onImage(imageId, expectedLength, bytes));
                        image.Dispose();
                        image = null;
                    }

                    path.RemoveAt(path.Count - 1);
                    cursor++;
                    continue;
                }

                if (token is >= 0xC1 and <= 0xFD)
                {
                    var componentLength = token - 0xC0;
                    var componentStart = cursor + 1;
                    EnsureAvailable(componentStart, componentLength, header.PayloadLength);
                    if (path.Count >= MaximumPathDepth)
                    {
                        throw new InvalidDataException($"FP5 token pathの深さが上限{MaximumPathDepth:N0}を超えています。");
                    }

                    path.Add(payload.AsSpan(componentStart, componentLength).ToArray());
                    if (collectVehicleMapping && path.Count == 2 &&
                        path[0].Length == 1 && path[0][0] == 0x05)
                    {
                        GetOrAddVehicleRecord(path[1], vehicleRecords);
                    }
                    if (collectVehicleMapping && IsGifPath(path) &&
                        !gifImageIds.Add(Convert.ToHexString(path[2])))
                    {
                        throw new InvalidDataException("FP5 GIF image IDが重複しています。");
                    }
                    if (IsJpegPath(path))
                    {
                        if (image is not null)
                        {
                            throw new InvalidDataException("FP5 JPEG nodeが閉じる前に次のJPEG nodeが始まりました。");
                        }

                        image = new ImageNodeBuilder(MaximumImageBytes);
                    }

                    cursor = componentStart + componentLength;
                    continue;
                }

                if (token == 0xFF)
                {
                    EnsureAvailable(cursor, 4, header.PayloadLength);
                    var referenceToken = payload[cursor + 1];
                    int dataStart;
                    int dataLength;
                    ulong? counter;
                    if (referenceToken is >= 0x01 and <= 0x04)
                    {
                        var referenceLength = referenceToken;
                        EnsureAvailable(cursor, 4 + referenceLength, header.PayloadLength);
                        counter = DecodeVariableLengthInteger(
                            payload.AsSpan(cursor + 2, referenceLength),
                            subtract64: false);
                        dataLength = BinaryPrimitives.ReadUInt16BigEndian(
                            payload.AsSpan(cursor + 2 + referenceLength, 2));
                        dataStart = cursor + 4 + referenceLength;
                    }
                    else if (referenceToken is >= 0x40 and <= 0x7F)
                    {
                        counter = DecodeVariableLengthInteger(
                            payload.AsSpan(cursor + 1, 1),
                            subtract64: true);
                        dataLength = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(cursor + 2, 2));
                        dataStart = cursor + 4;
                    }
                    else
                    {
                        throw new InvalidDataException($"未対応のFP5 long data token 0xFF 0x{referenceToken:X2}です。");
                    }

                    EnsureAvailable(dataStart, dataLength, header.PayloadLength);
                    if (image is not null && IsJpegPath(path))
                    {
                        image.AppendSegment(counter, payload.AsSpan(dataStart, dataLength));
                    }

                    onToken?.Invoke(new AbacusFp5RawToken(
                        path.Select(component => component.ToArray()).ToArray(),
                        [],
                        payload.AsSpan(dataStart, dataLength).ToArray(),
                        counter));

                    cursor = dataStart + dataLength;
                    continue;
                }

                if (token == 0x00)
                {
                    EnsureAvailable(cursor, 2, header.PayloadLength);
                    var dataLength = payload[cursor + 1];
                    EnsureAvailable(cursor + 2, dataLength, header.PayloadLength);
                    cursor += 2 + dataLength;
                    continue;
                }

                throw new InvalidDataException(
                    $"未対応のFP5 token 0x{token:X2}をdata sector {position / SectorBytes:N0}で検出しました。");
            }
        }

        if (image is not null)
        {
            image.Dispose();
            throw new InvalidDataException("FP5 JPEG nodeが閉じずにdata block chainが終了しました。");
        }
    }

    private static void CaptureVehicleField(
        IReadOnlyList<byte[]> path,
        ReadOnlySpan<byte> reference,
        ReadOnlySpan<byte> data,
        IDictionary<string, InternalVehicleRecordBuilder> vehicleRecords)
    {
        if (path.Count != 2 || path[0].Length != 1 || path[0][0] != 0x05 ||
            reference.Length != 1 || reference[0] is not (0x10 or 0x25 or 0x37))
        {
            return;
        }

        var record = GetOrAddVehicleRecord(path[1], vehicleRecords);
        record.SetField(reference[0], data);
    }

    private static InternalVehicleRecordBuilder GetOrAddVehicleRecord(
        ReadOnlySpan<byte> recordId,
        IDictionary<string, InternalVehicleRecordBuilder> vehicleRecords)
    {
        var recordIdHex = Convert.ToHexString(recordId);
        if (vehicleRecords.TryGetValue(recordIdHex, out var record))
        {
            return record;
        }

        if (vehicleRecords.Count >= 50_000)
        {
            throw new InvalidDataException("FP5車両レコード数が上限50,000件を超えています。");
        }

        record = new InternalVehicleRecordBuilder(recordIdHex);
        vehicleRecords.Add(recordIdHex, record);
        return record;
    }

    private static bool IsGifPath(IReadOnlyList<byte[]> path) =>
        path.Count == 4 &&
        path[0].Length == 1 && path[0][0] == 0x1F &&
        path[1].Length == 1 && path[1][0] == 0x05 &&
        path[3].AsSpan().SequenceEqual("GIFf"u8);

    private static void CaptureVehicleFieldName(
        IReadOnlyList<byte[]> path,
        ReadOnlySpan<byte> reference,
        ReadOnlySpan<byte> data,
        IDictionary<byte, byte[]> vehicleFieldNames)
    {
        if (path.Count != 3 || path[0].Length != 1 || path[0][0] != 0x03 ||
            path[1].Length != 1 || path[1][0] != 0x05 || path[2].Length != 1 ||
            path[2][0] is not (0x10 or 0x25 or 0x37) ||
            reference.Length != 1 || reference[0] != 0x01)
        {
            return;
        }

        if (!vehicleFieldNames.TryAdd(path[2][0], data.ToArray()))
        {
            throw new InvalidDataException($"FP5 field 0x{path[2][0]:X2}の名前が重複しています。");
        }
    }

    private static (int Width, int Height) ValidateDecodedJpeg(ReadOnlyMemory<byte> jpegBytes)
    {
        if (jpegBytes.Length < 4 ||
            jpegBytes.Span[0] != 0xFF || jpegBytes.Span[1] != 0xD8 ||
            jpegBytes.Span[^2] != 0xFF || jpegBytes.Span[^1] != 0xD9)
        {
            throw new InvalidDataException("復元データのJPEG SOI/EOIマーカーが不正です。");
        }

        using var stream = new MemoryStream(jpegBytes.ToArray(), writable: false);
        var decoder = new JpegBitmapDecoder(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count != 1)
        {
            throw new InvalidDataException("復元JPEGを1枚の画像としてデコードできません。");
        }

        var frame = decoder.Frames[0];
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 ||
            (long)frame.PixelWidth * frame.PixelHeight > MaximumPixels)
        {
            throw new InvalidDataException("復元JPEGの画像寸法が許容範囲外です。");
        }

        var bitsPerPixel = frame.Format.BitsPerPixel;
        if (bitsPerPixel <= 0)
        {
            throw new InvalidDataException("復元JPEGのpixel formatを確認できません。");
        }

        var stride = checked((frame.PixelWidth * bitsPerPixel + 7) / 8);
        var row = new byte[stride];
        for (var y = 0; y < frame.PixelHeight; y++)
        {
            frame.CopyPixels(new Int32Rect(0, y, frame.PixelWidth, 1), row, stride, 0);
        }

        return (frame.PixelWidth, frame.PixelHeight);
    }

    internal static SectorHeader ReadSectorHeader(FileStream stream, long position)
    {
        Span<byte> bytes = stackalloc byte[SectorHeaderBytes];
        ReadExactlyAt(stream, position, bytes);
        var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(bytes[12..14]);
        if (payloadLength > MaximumSectorPayloadBytes)
        {
            throw new InvalidDataException($"FP5 sector {position / SectorBytes:N0}のpayload長が不正です。");
        }

        return new SectorHeader(
            bytes[0],
            bytes[1],
            BinaryPrimitives.ReadUInt32BigEndian(bytes[2..6]),
            BinaryPrimitives.ReadUInt32BigEndian(bytes[6..10]),
            BinaryPrimitives.ReadUInt16BigEndian(bytes[10..12]),
            payloadLength);
    }

    internal static void ReadExactlyAt(FileStream stream, long position, Span<byte> destination)
    {
        stream.Position = position;
        stream.ReadExactly(destination);
    }

    private static void EnsureAvailable(int start, int length, int availableLength)
    {
        if (start < 0 || length < 0 || start > availableLength || length > availableLength - start)
        {
            throw new InvalidDataException("FP5 tokenの長さがsector payload範囲を超えています。");
        }
    }

    private static bool IsJpegPath(IReadOnlyList<byte[]> path) =>
        path.Count == 4 &&
        path[0].Length == 1 && path[0][0] == 0x1F &&
        path[1].Length == 1 && path[1][0] == 0x05 &&
        path[3].AsSpan().SequenceEqual(JpegNodeName);

    private static long ReadUInt40BigEndian(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 5)
        {
            throw new ArgumentException("40-bit値には5 bytesが必要です。", nameof(bytes));
        }

        return checked(
            ((long)bytes[0] << 32) |
            ((long)bytes[1] << 24) |
            ((long)bytes[2] << 16) |
            ((long)bytes[3] << 8) |
            bytes[4]);
    }

    private static ulong? DecodeVariableLengthInteger(ReadOnlySpan<byte> bytes, bool subtract64)
    {
        if (bytes.Length == 1 && bytes[0] <= 0x7F)
        {
            if (subtract64 && bytes[0] < 0x40)
            {
                return null;
            }

            return (ulong)(bytes[0] - (subtract64 ? 0x40 : 0));
        }

        if (bytes.Length == 1 && bytes[0] >= 0x80)
        {
            return bytes[0];
        }

        if (bytes.Length == 2 && bytes[0] is >= 0x80 and <= 0xBF)
        {
            return 0x80UL + (ulong)(bytes[0] - 0x80) * 0x100UL + bytes[1];
        }

        if (bytes.Length == 3 && bytes[0] is >= 0xC0 and <= 0xDF)
        {
            return 0x4080UL + (ulong)(bytes[0] - 0xC0) * 0x10000UL +
                   (ulong)bytes[1] * 0x100UL + bytes[2];
        }

        if (bytes.Length == 4 && bytes[0] is >= 0xE0 and <= 0xEF)
        {
            return 0x204080UL + (ulong)(bytes[0] - 0xE0) * 0x1000000UL +
                   (ulong)bytes[1] * 0x10000UL + (ulong)bytes[2] * 0x100UL + bytes[3];
        }

        if (bytes.Length == 5 && bytes[0] is >= 0xF0 and <= 0xF7)
        {
            return 0x10204080UL + (ulong)(bytes[0] - 0xF0) * 0x100000000UL +
                   (ulong)bytes[1] * 0x1000000UL + (ulong)bytes[2] * 0x10000UL +
                   (ulong)bytes[3] * 0x100UL + bytes[4];
        }

        return null;
    }

    private static string CreateOutputRoot(string outputParent)
    {
        var jobId = Guid.NewGuid().ToString("N")[..8];
        return Path.Combine(outputParent, $"ABACUS-FP5-Restoration-{DateTime.Now:yyyyMMdd-HHmmss}-{jobId}");
    }

    private static void WriteReport(AbacusFp5ImageRestoration result, string reportPath)
    {
        var report = new
        {
            formatVersion = 1,
            status = "gate13-verified",
            source = new
            {
                fileName = Path.GetFileName(result.SourceFilePath),
                fileSize = result.SourceFileSize,
                sha256 = result.SourceSha256,
            },
            fp5 = new
            {
                sectorSize = SectorBytes,
                result.SectorCount,
                result.BlockChainLevels,
                result.DataBlockCount,
                imagePath = "1F/05/<image-id>/JPEG",
            },
            verification = new
            {
                result.ImageNodeCount,
                result.RestoredImageCount,
                result.LengthCheckMatchCount,
                result.DecodeSuccessCount,
                result.UniqueImageSha256Count,
            },
            images = result.Images,
        };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(reportPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static bool IsInside(string candidatePath, string rootPath) =>
        candidatePath.Equals(rootPath, StringComparison.OrdinalIgnoreCase) ||
        candidatePath.StartsWith($"{rootPath}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    internal sealed record SectorCatalog(
        int BlockChainLevels,
        uint LargestBlockId,
        long[] PreviousIdToPosition,
        long[] FirstPositions,
        int[] Counts);

    internal sealed record SectorHeader(
        byte DeletedFlag,
        byte Level,
        uint PreviousId,
        uint NextId,
        ushort SkipBytes,
        ushort PayloadLength);

    internal sealed class InternalVehicleRecordBuilder(string recordIdHex)
    {
        private byte[]? registrationNumber;
        private byte[]? chassisNumber;
        private byte[]? imageReference;

        public void SetField(byte fieldId, ReadOnlySpan<byte> data)
        {
            var existing = fieldId switch
            {
                0x10 => registrationNumber,
                0x25 => imageReference,
                0x37 => chassisNumber,
                _ => throw new ArgumentOutOfRangeException(nameof(fieldId)),
            };
            if (existing is not null)
            {
                throw new InvalidDataException(
                    $"FP5車両レコード{recordIdHex}のfield 0x{fieldId:X2}が重複しています。");
            }

            var value = data.ToArray();
            switch (fieldId)
            {
                case 0x10:
                    registrationNumber = value;
                    break;
                case 0x25:
                    imageReference = value;
                    break;
                case 0x37:
                    chassisNumber = value;
                    break;
            }
        }

        public AbacusFp5InternalVehicleRecord Complete()
        {
            if (registrationNumber is null || chassisNumber is null || imageReference is null)
            {
                throw new InvalidDataException(
                    $"FP5車両レコード{recordIdHex}に登録番号・車台番号・画像参照のいずれかがありません。");
            }

            return new AbacusFp5InternalVehicleRecord(
                recordIdHex,
                registrationNumber,
                chassisNumber,
                imageReference);
        }
    }

    private sealed class ImageNodeBuilder(long maximumBytes) : IDisposable
    {
        private readonly MemoryStream segmentedData = new();
        private byte[]? singlePayload;
        private long? expectedLength;
        private int segmentCount;

        public bool HasSegments => segmentCount > 0;

        public void AppendSegment(ulong? counter, ReadOnlySpan<byte> data)
        {
            if (counter is null || counter.Value != (ulong)segmentCount + 1)
            {
                throw new InvalidDataException(
                    $"FP5画像segment counterが不連続です（期待 {segmentCount + 1:N0} / 実際 {counter?.ToString() ?? "不正"}）。");
            }

            if (segmentedData.Length > maximumBytes - data.Length)
            {
                throw new InvalidDataException($"復元JPEGが上限{maximumBytes:N0} bytesを超えています。");
            }

            segmentedData.Write(data);
            segmentCount++;
        }

        public void SetSinglePayload(ReadOnlySpan<byte> data)
        {
            if (data.Length > maximumBytes)
            {
                throw new InvalidDataException($"復元JPEGが上限{maximumBytes:N0} bytesを超えています。");
            }

            singlePayload = data.ToArray();
        }

        public void ApplyLengthCheck(long expected)
        {
            if (expectedLength is not null)
            {
                throw new InvalidDataException("FP5 JPEG nodeに複数のlength checkがあります。");
            }

            if (expected < 4 || expected > maximumBytes)
            {
                throw new InvalidDataException("FP5 JPEG nodeの期待byte数が許容範囲外です。");
            }

            var actual = HasSegments ? segmentedData.Length : singlePayload?.LongLength ?? 0;
            if (actual != expected)
            {
                throw new InvalidDataException(
                    $"FP5 JPEG nodeのlength checkが一致しません（期待 {expected:N0} / 復元 {actual:N0}）。");
            }

            expectedLength = expected;
        }

        public void Complete(Action<long, ReadOnlyMemory<byte>> onComplete)
        {
            if (expectedLength is null)
            {
                throw new InvalidDataException("FP5 JPEG nodeのlength checkがありません。");
            }

            if (HasSegments)
            {
                if (!segmentedData.TryGetBuffer(out var buffer))
                {
                    throw new InvalidDataException("復元JPEGのbufferを取得できません。");
                }

                onComplete(expectedLength.Value, buffer.AsMemory(0, checked((int)segmentedData.Length)));
                return;
            }

            if (singlePayload is null)
            {
                throw new InvalidDataException("FP5 JPEG nodeに画像dataがありません。");
            }

            onComplete(expectedLength.Value, singlePayload);
        }

        public void Dispose() => segmentedData.Dispose();
    }
}
