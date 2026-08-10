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
        var left = scanWidth;
        var top = scanHeight;
        var right = -1;
        var bottom = -1;
        for (var x = 0; x < scanWidth; x++)
        {
            var ink = 0;
            for (var y = 0; y < scanHeight; y++)
            {
                var offset = y * stride + x * BytesPerPixel;
                if (!IsWhite(pixels, offset))
                {
                    ink++;
                }
            }

            if (ink >= minimumColumnInk)
            {
                left = Math.Min(left, x);
                right = Math.Max(right, x);
            }
        }

        for (var y = 0; y < scanHeight; y++)
        {
            var ink = 0;
            for (var x = 0; x < scanWidth; x++)
            {
                var offset = y * stride + x * BytesPerPixel;
                if (!IsWhite(pixels, offset))
                {
                    ink++;
                }
            }

            if (ink >= minimumRowInk)
            {
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        }

        if (right < left || bottom < top)
        {
            return new AbacusCaptureCropResult(source, false, width, height);
        }

        left = Math.Max(0, left - Margin);
        top = Math.Max(0, top - Margin);
        right = Math.Min(scanWidth - 1, right + Margin);
        bottom = Math.Min(scanHeight - 1, bottom + Margin);
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
