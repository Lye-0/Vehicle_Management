using System.Buffers.Binary;
using System.Text;

namespace VehicleManagement.AbacusImport;

public sealed record AbacusFp5ImageCandidate(
    int Index,
    long Offset,
    long Length,
    int? PixelWidth,
    int? PixelHeight);

public sealed record AbacusFp5Inspection(
    string FolderPath,
    string FileName,
    long FileSize,
    bool IsFileMaker5,
    string FormatLabel,
    int JpegCandidateCount,
    long JpegCandidateBytes,
    int OversizeCandidateCount,
    IReadOnlyList<AbacusFp5ImageCandidate> Candidates,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public bool IsValid => Errors.Count == 0 && IsFileMaker5;
}

public sealed class AbacusFp5Inspector
{
    private const long MaximumFileBytes = 2L * 1024 * 1024 * 1024;
    private const long MaximumCandidateBytes = 256L * 1024 * 1024;
    private const int MaximumCandidates = 20_000;
    private const int MaximumReportedCandidates = 100;
    private const int MaximumJpegHeaderBytes = 64 * 1024;
    private const int BufferBytes = 1024 * 1024;

    public Task<AbacusFp5Inspection> InspectAsync(
        string folderPath,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Inspect(folderPath, cancellationToken), cancellationToken);

    private static AbacusFp5Inspection Inspect(
        string folderPath,
        CancellationToken cancellationToken)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
        var fileName = "BackUp-5.fp5";
        var errors = new List<string>();
        var warnings = new List<string>();
        var candidates = new List<AbacusFp5ImageCandidate>();
        var path = Path.GetFullPath(Path.Combine(root, fileName));

        if (!Directory.Exists(root))
        {
            return Invalid(root, fileName, "指定されたフォルダーが見つかりません。");
        }

        var rootInfo = new DirectoryInfo(root);
        if (rootInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return Invalid(root, fileName, "リンクまたは再解析ポイントのフォルダーは解析できません。");
        }

        if (!path.StartsWith($"{root}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            return Invalid(root, fileName, "フォルダー外のファイルは解析できません。");
        }

        FileInfo file;
        try
        {
            file = new FileInfo(path);
            if (!file.Exists)
            {
                return Invalid(root, fileName, $"必須ファイルがありません: {fileName}");
            }

            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return Invalid(root, fileName, $"リンクまたは再解析ポイントは解析できません: {fileName}");
            }

            if (file.Length > MaximumFileBytes)
            {
                return Invalid(root, fileName, $"ファイルサイズが上限{MaximumFileBytes:N0} bytesを超えています。");
            }
        }
        catch (IOException exception)
        {
            return Invalid(root, fileName, $"ファイル情報を読み取れません: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            return Invalid(root, fileName, $"ファイル情報を読み取る権限がありません: {exception.Message}");
        }

        var header = new byte[1024];
        var headerBytes = 0;
        var jpegCandidateCount = 0;
        var jpegCandidateBytes = 0L;
        var oversizeCandidateCount = 0;

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferBytes,
                FileOptions.SequentialScan);

            headerBytes = ReadHeader(stream, header, cancellationToken);
            var headerText = Encoding.ASCII.GetString(header, 0, headerBytes);
            var isFileMaker5 = headerText.Contains("FileMaker", StringComparison.OrdinalIgnoreCase) &&
                               headerText.Contains("Pro 5.0", StringComparison.OrdinalIgnoreCase);
            if (!isFileMaker5)
            {
                errors.Add("FileMaker Pro 5.0のヘッダーを確認できませんでした。");
            }

            stream.Position = 0;
            ScanJpegCandidates(
                stream,
                file.Length,
                candidates,
                ref jpegCandidateCount,
                ref jpegCandidateBytes,
                ref oversizeCandidateCount,
                warnings,
                cancellationToken);

            if (stream.Length != file.Length)
            {
                errors.Add("解析中にファイルサイズが変化しました。ABACUSを終了して再試行してください。");
            }

            var formatLabel = isFileMaker5 ? "FileMaker Pro 5.0" : "FileMaker 5形式を確認できません";
            if (oversizeCandidateCount > 0)
            {
                warnings.Add($"上限{MaximumCandidateBytes:N0} bytesを超えるJPEG候補を{oversizeCandidateCount:N0}件検出しました。候補一覧には含めていません。");
            }

            if (jpegCandidateCount == 0)
            {
                warnings.Add("JPEG開始・終了マーカーを含む候補は見つかりませんでした。");
            }

            return new AbacusFp5Inspection(
                root,
                fileName,
                file.Length,
                isFileMaker5,
                formatLabel,
                jpegCandidateCount,
                jpegCandidateBytes,
                oversizeCandidateCount,
                candidates,
                errors,
                warnings);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException exception)
        {
            errors.Add($"ファイルを読み取れません: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            errors.Add($"ファイルを読み取る権限がありません: {exception.Message}");
        }

        return new AbacusFp5Inspection(
            root,
            fileName,
            file.Length,
            false,
            headerBytes == 0 ? "読み取り失敗" : "判定不能",
            jpegCandidateCount,
            jpegCandidateBytes,
            oversizeCandidateCount,
            candidates,
            errors,
            warnings);
    }

    private static int ReadHeader(
        FileStream stream,
        byte[] header,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < header.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(header, total, header.Length - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static void ScanJpegCandidates(
        FileStream stream,
        long expectedLength,
        ICollection<AbacusFp5ImageCandidate> candidates,
        ref int jpegCandidateCount,
        ref long jpegCandidateBytes,
        ref int oversizeCandidateCount,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferBytes];
        var position = 0L;
        var previousByte = -1;
        long? startOffset = null;
        var candidateTooLarge = false;
        MemoryStream? candidateHeader = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            for (var index = 0; index < read; index++)
            {
                var current = buffer[index];
                if (startOffset is null)
                {
                    if (previousByte == 0xFF && current == 0xD8)
                    {
                        startOffset = position - 1;
                        candidateTooLarge = false;
                        candidateHeader = jpegCandidateCount < MaximumReportedCandidates
                            ? new MemoryStream(capacity: MaximumJpegHeaderBytes)
                            : null;
                        candidateHeader?.WriteByte(0xFF);
                        candidateHeader?.WriteByte(0xD8);
                    }
                }
                else
                {
                    if (candidateHeader is not null && candidateHeader.Length < MaximumJpegHeaderBytes)
                    {
                        candidateHeader.WriteByte(current);
                    }

                    var candidateLength = position - startOffset.Value + 1;
                    if (candidateLength > MaximumCandidateBytes)
                    {
                        candidateTooLarge = true;
                    }

                    if (previousByte == 0xFF && current == 0xD9)
                    {
                        if (candidateTooLarge)
                        {
                            oversizeCandidateCount++;
                        }
                        else
                        {
                            jpegCandidateCount++;
                            jpegCandidateBytes += candidateLength;
                            if (jpegCandidateCount > MaximumCandidates)
                            {
                                throw new InvalidDataException($"JPEG候補数が上限{MaximumCandidates:N0}件を超えています。");
                            }

                            if (candidates.Count < MaximumReportedCandidates)
                            {
                                var (width, height) = TryReadJpegDimensions(candidateHeader?.ToArray() ?? []);
                                candidates.Add(new AbacusFp5ImageCandidate(
                                    jpegCandidateCount,
                                    startOffset.Value,
                                    candidateLength,
                                    width,
                                    height));
                            }
                        }

                        candidateHeader?.Dispose();
                        candidateHeader = null;
                        startOffset = null;
                        candidateTooLarge = false;
                    }
                }

                previousByte = current;
                position++;
            }
        }

        candidateHeader?.Dispose();
        if (startOffset is not null)
        {
            warnings.Add($"JPEG開始マーカーを{startOffset.Value:N0} byte位置で検出しましたが、終了マーカーがありません。");
        }

        if (position != expectedLength)
        {
            throw new IOException("解析中の読み取り位置がファイルサイズと一致しません。");
        }
    }

    private static (int? Width, int? Height) TryReadJpegDimensions(byte[] bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8)
        {
            return (null, null);
        }

        var offset = 2;
        while (offset + 4 <= bytes.Length)
        {
            while (offset < bytes.Length && bytes[offset] == 0xFF)
            {
                offset++;
            }

            if (offset >= bytes.Length)
            {
                break;
            }

            var marker = bytes[offset++];
            if (marker is 0xD8 or 0xD9)
            {
                continue;
            }

            if (marker == 0xDA)
            {
                break;
            }

            if (offset + 2 > bytes.Length)
            {
                break;
            }

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));
            if (segmentLength < 2 || offset + segmentLength > bytes.Length)
            {
                break;
            }

            if (marker is >= 0xC0 and <= 0xC3 or >= 0xC5 and <= 0xC7 or >= 0xC9 and <= 0xCB or >= 0xCD and <= 0xCF)
            {
                if (segmentLength >= 7)
                {
                    var height = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 3, 2));
                    var width = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 5, 2));
                    return (width, height);
                }

                break;
            }

            offset += segmentLength;
        }

        return (null, null);
    }

    private static AbacusFp5Inspection Invalid(string folderPath, string fileName, string error) =>
        new(folderPath, fileName, 0, false, "判定不能", 0, 0, 0, [], [error], []);
}
