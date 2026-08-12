using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VehicleManagement.Companion.Services;

public enum AbacusScreenVisualState
{
    Unknown,
    MainMenu,
    VehicleList,
    VehicleDetail,
    ExpandedImage,
}

public sealed record AbacusScreenVisualResult(
    AbacusScreenVisualState State,
    double BlueRatio,
    double DarkRatio,
    double GrayInkRatio);

/// <summary>
/// ABACUSのメインウィンドウを読み取り専用でキャプチャした画像から、
/// 現在実際に表示されている画面を分類します。FileMakerは非表示の
/// レイアウト用子ウィンドウを残すため、ウィンドウ名だけでは判定しません。
/// </summary>
public sealed class AbacusScreenStateClassifier
{
    private const int MaximumSampleEdge = 320;

    public AbacusScreenVisualResult Classify(BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.PixelWidth <= 0 || source.PixelHeight <= 0)
        {
            return new AbacusScreenVisualResult(AbacusScreenVisualState.Unknown, 0, 0, 0);
        }

        var sample = CreateSample(source);
        var converted = new FormatConvertedBitmap(sample, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        var stride = checked(converted.PixelWidth * 4);
        var pixels = new byte[checked(stride * converted.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);

        var blue = 0;
        var dark = 0;
        var grayInk = 0;
        var total = converted.PixelWidth * converted.PixelHeight;
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            var b = pixels[offset];
            var g = pixels[offset + 1];
            var r = pixels[offset + 2];
            var maximum = Math.Max(r, Math.Max(g, b));
            var minimum = Math.Min(r, Math.Min(g, b));

            if (b >= r + 20 && b >= g + 4 && b >= 120)
            {
                blue++;
            }

            if (maximum < 160)
            {
                dark++;
            }

            if (maximum - minimum < 18 && maximum < 235)
            {
                grayInk++;
            }
        }

        var blueRatio = blue / (double)total;
        var darkRatio = dark / (double)total;
        var grayInkRatio = grayInk / (double)total;
        var state = ClassifyRatios(blueRatio, darkRatio, grayInkRatio);
        return new AbacusScreenVisualResult(state, blueRatio, darkRatio, grayInkRatio);
    }

    internal static AbacusScreenVisualState ClassifyRatios(
        double blueRatio,
        double darkRatio,
        double grayInkRatio)
    {
        // メニューは画面上部と下部に大きな青系の面があり、文字・罫線が少ない。
        if (blueRatio >= 0.08 && darkRatio <= 0.03)
        {
            return AbacusScreenVisualState.MainMenu;
        }

        // 拡大画像は車検証の灰色罫線が画面の大部分を占め、ABACUSの青い入力枠がほぼない。
        if (blueRatio <= 0.018 && grayInkRatio >= 0.08)
        {
            return AbacusScreenVisualState.ExpandedImage;
        }

        // 一覧は多数の青い行罫線と文字を持つ。詳細は青い入力枠に加えて、
        // 右側の画像領域があるため一覧より暗色画素の比率が低くなる。
        if (blueRatio >= 0.035 && darkRatio >= 0.04)
        {
            return AbacusScreenVisualState.VehicleList;
        }

        if (blueRatio >= 0.018 && darkRatio >= 0.012)
        {
            return AbacusScreenVisualState.VehicleDetail;
        }

        return AbacusScreenVisualState.Unknown;
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

        var transformed = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        transformed.Freeze();
        return transformed;
    }
}
