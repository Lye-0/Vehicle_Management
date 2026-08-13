using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VehicleManagement.Companion.Services;

/// <summary>
/// JPEGデコーダーが壊れたFileMaker内部データを均一色の画像として返すケースを検出します。
/// 寸法やSOI/EOIだけでは実画像であることを保証できないため、縮小した画素サンプルも検証します。
/// </summary>
internal static class DecodedImageContentValidator
{
    private const int MaximumSampleEdge = 96;
    private const double DominantColorRatio = 0.98;
    private const int MinimumFileMakerBlockLabels = 8;

    public static void EnsureNotFileMakerBlock(ReadOnlySpan<byte> encodedBytes, string sourceLabel)
    {
        var labels = CountFileMakerBlockLabels(encodedBytes);
        if (labels >= MinimumFileMakerBlockLabels)
        {
            throw new InvalidDataException(
                $"{sourceLabel}にFileMaker内部ブロックの反復ヘッダーを検出したため、標準JPEGとして採用できません。" +
                "UCSの生バイト列をそのまま画像として保存しない安全措置です。");
        }
    }

    public static void EnsureNotFileMakerBlock(Stream encodedStream, string sourceLabel)
    {
        ArgumentNullException.ThrowIfNull(encodedStream);
        if (!encodedStream.CanSeek)
        {
            throw new InvalidDataException($"{sourceLabel}の形式を検証できません。");
        }

        var originalPosition = encodedStream.Position;
        try
        {
            encodedStream.Position = 0;
            var labels = 0;
            var carry = Array.Empty<byte>();
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = encodedStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                var combined = new byte[carry.Length + read];
                carry.CopyTo(combined, 0);
                Buffer.BlockCopy(buffer, 0, combined, carry.Length, read);
                labels += CountFileMakerBlockLabels(combined);
                if (labels >= MinimumFileMakerBlockLabels)
                {
                    throw new InvalidDataException(
                        $"{sourceLabel}にFileMaker内部ブロックの反復ヘッダーを検出したため、標準JPEGとして採用できません。" +
                        "UCSの生バイト列をそのまま画像として保存しない安全措置です。");
                }

                carry = combined.Length >= 3
                    ? combined[^3..]
                    : combined;
            }
        }
        finally
        {
            encodedStream.Position = originalPosition;
        }
    }

    public static void EnsureHasVisualContent(BitmapSource source, string sourceLabel)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.PixelWidth <= 0 || source.PixelHeight <= 0)
        {
            throw new InvalidDataException($"{sourceLabel}の画像寸法を確認できません。");
        }

        var sample = CreateSample(source);
        var converted = new FormatConvertedBitmap(sample, PixelFormats.Bgra32, null, 0);
        converted.Freeze();

        var stride = checked(converted.PixelWidth * 4);
        var pixels = new byte[checked(stride * converted.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);

        var colorCounts = new Dictionary<uint, int>();
        var total = 0;
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            // Alpha is intentionally ignored. FileMaker's placeholder may have a
            // fully opaque alpha channel while its RGB values are still uniform.
            var color = (uint)(pixels[offset] |
                               pixels[offset + 1] << 8 |
                               pixels[offset + 2] << 16);
            colorCounts[color] = colorCounts.TryGetValue(color, out var count) ? count + 1 : 1;
            total++;
        }

        if (total == 0)
        {
            throw new InvalidDataException($"{sourceLabel}に画素がありません。");
        }

        var dominantCount = colorCounts.Values.Max();
        // Invalid UCS block data often produces a single gray plane with a few
        // compression/noise colors near its border. The exact color count is
        // therefore not a reliable discriminator; the dominant-color ratio is.
        if (dominantCount / (double)total >= DominantColorRatio)
        {
            throw new InvalidDataException(
                $"{sourceLabel}は均一色のプレースホルダーとして復号されたため、実画像として採用できません。" +
                "ABACUSの画像表示画面をキャプチャする経路、または正式なFileMakerコンテナ解析が必要です。");
        }
    }

    private static BitmapSource CreateSample(BitmapSource source)
    {
        var scale = Math.Min(
            1d,
            Math.Min(
                MaximumSampleEdge / (double)source.PixelWidth,
                MaximumSampleEdge / (double)source.PixelHeight));
        if (scale >= 1d)
        {
            return source;
        }

        var transformed = new TransformedBitmap(
            source,
            new ScaleTransform(scale, scale));
        transformed.Freeze();
        return transformed;
    }

    private static int CountFileMakerBlockLabels(ReadOnlySpan<byte> bytes)
    {
        var count = 0;
        for (var index = 0; index <= bytes.Length - 4; index++)
        {
            if (bytes[index] == (byte)'J' &&
                bytes[index + 1] == (byte)'P' &&
                bytes[index + 2] == (byte)'E' &&
                bytes[index + 3] == (byte)'G')
            {
                count++;
            }
        }

        return count;
    }
}
