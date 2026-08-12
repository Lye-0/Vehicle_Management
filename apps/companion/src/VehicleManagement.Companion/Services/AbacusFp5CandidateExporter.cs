using System.IO;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;
using VehicleManagement.AbacusImport;

namespace VehicleManagement.Companion.Services;

public sealed record AbacusFp5CandidateExportResult(
    string FilePath,
    long FileSize,
    int PixelWidth,
    int PixelHeight,
    string Sha256);

public sealed class AbacusFp5CandidateExporter
{
    private const long MaximumPixels = 50_000_000;
    private const long MaximumCandidateBytes = 256L * 1024 * 1024;
    private const int BufferBytes = 1024 * 1024;

    public async Task<AbacusFp5CandidateExportResult> ExportAsync(
        string sourceFolder,
        AbacusFp5ImageCandidate candidate,
        string destinationFolder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceFolder));
        var destinationRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationFolder));
        var sourcePath = Path.GetFullPath(Path.Combine(sourceRoot, "BackUp-5.fp5"));

        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException("ABACUSフォルダーが見つかりません。");
        }

        if (!Directory.Exists(destinationRoot))
        {
            throw new DirectoryNotFoundException("画像の保存先フォルダーが見つかりません。");
        }

        if (new DirectoryInfo(sourceRoot).Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            new DirectoryInfo(destinationRoot).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("リンクまたは再解析ポイントのフォルダーは使用できません。");
        }

        if (IsInside(destinationRoot, sourceRoot))
        {
            throw new InvalidOperationException("保存先はABACUSフォルダーの外側を指定してください。");
        }

        if (candidate.Offset < 0 || candidate.Length < 4 || candidate.Length > MaximumCandidateBytes)
        {
            throw new InvalidDataException("JPEG候補の位置またはサイズが許容範囲外です。");
        }

        var sourceFile = new FileInfo(sourcePath);
        if (!sourceFile.Exists)
        {
            throw new FileNotFoundException("BackUp-5.fp5が見つかりません。", sourcePath);
        }

        if (sourceFile.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            candidate.Offset > sourceFile.Length ||
            candidate.Length > sourceFile.Length - candidate.Offset)
        {
            throw new InvalidDataException("JPEG候補が現在のBackUp-5.fp5の範囲外です。再診断してください。");
        }

        var outputPath = CreateUniqueOutputPath(destinationRoot, candidate.Index);
        var temporaryPath = outputPath + ".partial";
        try
        {
            string sha256;
            await using (var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                source.Position = candidate.Offset;
                await ValidateJpegMarkersAsync(source, candidate.Length, cancellationToken);
                source.Position = candidate.Offset;

                await using var output = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    BufferBytes,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await CopySegmentAsync(source, output, candidate.Length, hash, cancellationToken);
                await output.FlushAsync(cancellationToken);
                sha256 = Convert.ToHexString(hash.GetHashAndReset());
            }

            ValidateStandaloneJpeg(temporaryPath);
            var dimensions = ValidateDecodedJpeg(temporaryPath);
            if ((long)dimensions.Width * dimensions.Height > MaximumPixels)
            {
                throw new InvalidDataException("JPEG画像の画素数が許容範囲を超えています。");
            }

            File.Move(temporaryPath, outputPath, overwrite: false);
            return new AbacusFp5CandidateExportResult(
                outputPath,
                candidate.Length,
                dimensions.Width,
                dimensions.Height,
                sha256);
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }
    }

    private static void ValidateStandaloneJpeg(string path)
    {
        try
        {
            ValidateStandaloneJpegCore(path);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException(
                "JPEG候補を標準JPEGとして検証できません。FileMaker内部の分割ブロックの可能性があるため保存しません。",
                exception);
        }
    }

    private static void ValidateStandaloneJpegCore(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 4 || ReadByte(stream) != 0xFF || ReadByte(stream) != 0xD8)
        {
            throw new InvalidDataException("JPEG候補のSOIマーカーを確認できません。");
        }

        var sawStartOfFrame = false;
        var sawStartOfScan = false;
        int? pendingMarker = null;
        while (true)
        {
            var marker = pendingMarker ?? ReadMarker(stream);
            pendingMarker = null;

            if (marker == 0xD9)
            {
                if (!sawStartOfFrame || !sawStartOfScan)
                {
                    throw new InvalidDataException("JPEG候補に必要なフレームまたはスキャンがありません。");
                }

                if (stream.Position != stream.Length)
                {
                    throw new InvalidDataException("JPEG終了マーカーの後ろに余分なデータがあります。");
                }

                return;
            }

            if (marker == 0xD8 || marker is >= 0xD0 and <= 0xD7 || marker == 0x01)
            {
                throw new InvalidDataException("JPEG候補内に独立したSOIまたは不正なマーカーがあります。FileMaker内部ブロックの可能性があります。");
            }

            var segmentLength = ReadSegmentLength(stream);
            if (marker is >= 0xC0 and <= 0xC3 or >= 0xC5 and <= 0xC7 or
                >= 0xC9 and <= 0xCB or >= 0xCD and <= 0xCF)
            {
                if (sawStartOfFrame)
                {
                    throw new InvalidDataException("JPEG候補内に複数の画像フレームがあります。FileMaker内部の分割ブロックと判定したため保存しません。");
                }

                if (segmentLength < 8)
                {
                    throw new InvalidDataException("JPEGフレーム情報の長さが不正です。");
                }

                sawStartOfFrame = true;
            }

            SkipSegment(stream, segmentLength - 2);
            if (marker != 0xDA)
            {
                continue;
            }

            if (!sawStartOfFrame)
            {
                throw new InvalidDataException("JPEGフレームより前にスキャンが現れました。");
            }

            sawStartOfScan = true;
            var markerAfterScan = ReadEntropyMarker(stream);
            if (markerAfterScan == 0xD9)
            {
                if (stream.Position != stream.Length)
                {
                    throw new InvalidDataException("JPEG終了マーカーの後ろに余分なデータがあります。");
                }

                return;
            }

            pendingMarker = markerAfterScan;
        }
    }

    private static int ReadMarker(FileStream stream)
    {
        if (ReadByte(stream) != 0xFF)
        {
            throw new InvalidDataException("JPEG候補のマーカー位置に予期しないデータがあります。");
        }

        var marker = ReadByte(stream);
        while (marker == 0xFF)
        {
            marker = ReadByte(stream);
        }

        if (marker <= 0 || marker == 0x00)
        {
            throw new InvalidDataException("JPEG候補のマーカーが不正です。");
        }

        return marker;
    }

    private static int ReadEntropyMarker(FileStream stream)
    {
        while (true)
        {
            var value = ReadByte(stream);
            if (value != 0xFF)
            {
                continue;
            }

            var marker = ReadByte(stream);
            while (marker == 0xFF)
            {
                marker = ReadByte(stream);
            }

            if (marker == 0x00 || marker is >= 0xD0 and <= 0xD7)
            {
                continue;
            }

            return marker;
        }
    }

    private static int ReadSegmentLength(FileStream stream)
    {
        var high = ReadByte(stream);
        var low = ReadByte(stream);
        var length = high << 8 | low;
        if (length < 2)
        {
            throw new InvalidDataException("JPEGセグメント長が不正です。");
        }

        if (length - 2 > stream.Length - stream.Position)
        {
            throw new InvalidDataException("JPEGセグメントがファイル範囲を超えています。");
        }

        return length;
    }

    private static void SkipSegment(FileStream stream, int bytes)
    {
        if (bytes < 0 || bytes > stream.Length - stream.Position)
        {
            throw new InvalidDataException("JPEGセグメントの範囲が不正です。");
        }

        stream.Position += bytes;
    }

    private static int ReadByte(FileStream stream)
    {
        var value = stream.ReadByte();
        if (value < 0)
        {
            throw new InvalidDataException("JPEG候補が途中で終了しました。");
        }

        return value;
    }

    private static async Task CopySegmentAsync(
        FileStream source,
        FileStream destination,
        long length,
        IncrementalHash hash,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferBytes];
        var remaining = length;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requested = (int)Math.Min(buffer.Length, remaining);
            var read = await source.ReadAsync(buffer.AsMemory(0, requested), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("JPEG候補の読み取り中にファイルが終了しました。");
            }

            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= read;
        }
    }

    private static async Task ValidateJpegMarkersAsync(
        FileStream source,
        long length,
        CancellationToken cancellationToken)
    {
        var start = new byte[2];
        await ReadExactlyAsync(source, start, cancellationToken);
        source.Position += length - 4;
        var end = new byte[2];
        await ReadExactlyAsync(source, end, cancellationToken);
        if (start[0] != 0xFF || start[1] != 0xD8 || end[0] != 0xFF || end[1] != 0xD9)
        {
            throw new InvalidDataException("JPEG候補の開始・終了マーカーを再確認できません。再診断してください。");
        }
    }

    private static async Task ReadExactlyAsync(
        FileStream source,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await source.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("JPEG候補の検証中にファイルが終了しました。");
            }

            offset += read;
        }
    }

    private static (int Width, int Height) ValidateDecodedJpeg(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = new JpegBitmapDecoder(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count != 1)
        {
            throw new InvalidDataException("JPEG候補を1枚の画像としてデコードできません。");
        }

        var frame = decoder.Frames[0];
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0)
        {
            throw new InvalidDataException("JPEG候補の画像寸法を確認できません。");
        }

        DecodedImageContentValidator.EnsureHasVisualContent(frame, "JPEG候補");

        return (frame.PixelWidth, frame.PixelHeight);
    }

    private static string CreateUniqueOutputPath(string destinationRoot, int index)
    {
        var baseName = $"ABACUS-fp5-candidate-{index:D4}-{DateTime.Now:yyyyMMdd-HHmmss}";
        for (var suffix = 0; suffix < 1000; suffix++)
        {
            var name = suffix == 0 ? $"{baseName}.jpg" : $"{baseName}-{suffix}.jpg";
            var path = Path.Combine(destinationRoot, name);
            if (!File.Exists(path) && !Directory.Exists(path) && !File.Exists(path + ".partial"))
            {
                return path;
            }
        }

        throw new IOException("一意な画像ファイル名を作成できませんでした。");
    }

    private static bool IsInside(string candidatePath, string rootPath) =>
        candidatePath.Equals(rootPath, StringComparison.OrdinalIgnoreCase) ||
        candidatePath.StartsWith($"{rootPath}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
}
