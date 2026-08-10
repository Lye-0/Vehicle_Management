using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VehicleManagement.Companion.Services;

public sealed record AbacusCaptureCropResult(
    BitmapSource Image,
    bool WasCropped,
    int OriginalWidth,
    int OriginalHeight);

public sealed class AbacusCaptureCropper
{
    private const int BytesPerPixel = 4;
    private const int Margin = 6;
    private const int OuterFrameIgnore = 8;
    private const double MostlyBlackRatio = 0.98;
    private const byte BlackThreshold = 8;
    private const byte WhiteThreshold = 245;

    public AbacusCaptureCropResult Crop(BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        if (width <= 0 || height <= 0)
        {
            return new AbacusCaptureCropResult(source, false, width, height);
        }

        var stride = checked(width * BytesPerPixel);
        var pixels = new byte[checked(stride * height)];
        converted.CopyPixels(pixels, stride, 0);

        var paintedHeight = height;
        while (paintedHeight > 1 && IsMostlyBlackRow(pixels, stride, width, paintedHeight - 1))
        {
            paintedHeight--;
        }

        var paintedWidth = width;
        while (paintedWidth > 1 && IsMostlyBlackColumn(pixels, stride, paintedWidth - 1, paintedHeight))
        {
            paintedWidth--;
        }

        var scanWidth = Math.Max(1, paintedWidth - OuterFrameIgnore);
        var scanHeight = Math.Max(1, paintedHeight - OuterFrameIgnore);
        var minimumColumnInk = Math.Max(3, (int)Math.Ceiling(scanHeight * 0.005));
        var minimumRowInk = Math.Max(3, (int)Math.Ceiling(scanWidth * 0.005));
        var columnInk = new int[scanWidth];
        var rowInk = new int[scanHeight];
        for (var x = 0; x < scanWidth; x++)
        {
            for (var y = OuterFrameIgnore; y < scanHeight; y++)
            {
                var offset = y * stride + x * BytesPerPixel;
                if (!IsWhite(pixels, offset))
                {
                    columnInk[x]++;
                }
            }
        }

        for (var y = 0; y < scanHeight; y++)
        {
            for (var x = OuterFrameIgnore; x < scanWidth; x++)
            {
                var offset = y * stride + x * BytesPerPixel;
                if (!IsWhite(pixels, offset))
                {
                    rowInk[y]++;
                }
            }
        }

        var horizontalRun = FindLargestRun(columnInk, minimumColumnInk);
        var verticalRun = FindLargestRun(rowInk, minimumRowInk);
        if (horizontalRun is null || verticalRun is null)
        {
            return new AbacusCaptureCropResult(source, false, width, height);
        }

        var left = Math.Max(0, horizontalRun.Value.Start - Margin);
        var top = Math.Max(0, verticalRun.Value.Start - Margin);
        var right = Math.Min(scanWidth - 1, horizontalRun.Value.End + Margin);
        var bottom = Math.Min(scanHeight - 1, verticalRun.Value.End + Margin);
        var contentStartAfterToolbar = FindContentStartAfterTopToolbar(rowInk, scanWidth);
        if (contentStartAfterToolbar is not null)
        {
            top = Math.Max(top, contentStartAfterToolbar.Value);
        }

        var cropWidth = right - left + 1;
        var cropHeight = bottom - top + 1;
        var cropPixels = (long)cropWidth * cropHeight;
        var originalPixels = (long)width * height;
        if (cropWidth < 64 || cropHeight < 64 || cropPixels < originalPixels / 20)
        {
            return new AbacusCaptureCropResult(source, false, width, height);
        }

        if (left == 0 && top == 0 && cropWidth == width && cropHeight == height)
        {
            return new AbacusCaptureCropResult(source, false, width, height);
        }

        var cropped = new CroppedBitmap(converted, new Int32Rect(left, top, cropWidth, cropHeight));
        cropped.Freeze();
        return new AbacusCaptureCropResult(cropped, true, width, height);
    }

    private static (int Start, int End)? FindLargestRun(int[] ink, int minimumInk)
    {
        (int Start, int End)? largest = null;
        var runStart = -1;
        for (var index = 0; index <= ink.Length; index++)
        {
            var isSignificant = index < ink.Length && ink[index] >= minimumInk;
            if (isSignificant && runStart < 0)
            {
                runStart = index;
            }

            if (isSignificant || runStart < 0)
            {
                continue;
            }

            var candidate = (Start: runStart, End: index - 1);
            if (largest is null || candidate.End - candidate.Start > largest.Value.End - largest.Value.Start)
            {
                largest = candidate;
            }

            runStart = -1;
        }

        return largest;
    }

    private static int? FindContentStartAfterTopToolbar(int[] rowInk, int scanWidth)
    {
        const int minimumToolbarHeight = 12;
        const int requiredSparseRows = 3;
        var searchLimit = Math.Min(rowInk.Length, Math.Max(64, rowInk.Length / 8));
        var denseThreshold = Math.Max(24, (int)Math.Ceiling(scanWidth * 0.12));
        var sparseThreshold = Math.Max(12, (int)Math.Floor(scanWidth * 0.08));
        var latestAllowedStart = Math.Max(32, rowInk.Length / 20);
        var denseStart = -1;
        var lastDense = -1;
        var sparseRows = 0;

        for (var y = 0; y < searchLimit; y++)
        {
            if (rowInk[y] >= denseThreshold)
            {
                if (denseStart < 0)
                {
                    denseStart = y;
                }

                lastDense = y;
                sparseRows = 0;
                continue;
            }

            if (denseStart < 0)
            {
                continue;
            }

            if (rowInk[y] <= sparseThreshold)
            {
                sparseRows++;
            }
            else
            {
                sparseRows = 0;
            }

            if (sparseRows < requiredSparseRows)
            {
                continue;
            }

            var denseHeight = lastDense - denseStart + 1;
            if (denseHeight >= minimumToolbarHeight && denseStart <= latestAllowedStart)
            {
                return lastDense + 1;
            }

            denseStart = -1;
            lastDense = -1;
            sparseRows = 0;
        }

        return null;
    }

    private static bool IsMostlyBlackRow(byte[] pixels, int stride, int width, int y)
    {
        var black = 0;
        for (var x = 0; x < width; x++)
        {
            if (IsBlack(pixels, y * stride + x * BytesPerPixel))
            {
                black++;
            }
        }

        return black >= width * MostlyBlackRatio;
    }

    private static bool IsMostlyBlackColumn(byte[] pixels, int stride, int x, int height)
    {
        var black = 0;
        for (var y = 0; y < height; y++)
        {
            if (IsBlack(pixels, y * stride + x * BytesPerPixel))
            {
                black++;
            }
        }

        return black >= height * MostlyBlackRatio;
    }

    private static bool IsBlack(byte[] pixels, int offset) =>
        pixels[offset] <= BlackThreshold &&
        pixels[offset + 1] <= BlackThreshold &&
        pixels[offset + 2] <= BlackThreshold;

    private static bool IsWhite(byte[] pixels, int offset) =>
        pixels[offset] >= WhiteThreshold &&
        pixels[offset + 1] >= WhiteThreshold &&
        pixels[offset + 2] >= WhiteThreshold;
}
