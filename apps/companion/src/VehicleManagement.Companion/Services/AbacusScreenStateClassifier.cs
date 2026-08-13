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
        var total = 0;
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            var b = pixels[offset];
            var g = pixels[offset + 1];
            var r = pixels[offset + 2];
            var alpha = pixels[offset + 3];
            // PrintWindowで作成したHBITMAPには、未描画領域が透明画素として
            // 残ることがあります。透明画素を黒・灰色として数えると、画面全体
            // が暗色のように判定されるため、判定対象から除外します。
            if (alpha < 16)
            {
                continue;
            }

            total++;
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

        if (total == 0)
        {
            return new AbacusScreenVisualResult(AbacusScreenVisualState.Unknown, 0, 0, 0);
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
        // メニューは画面上部と下部に大きな青系の面があります。ABACUSの
        // PrintWindow結果では未描画部分が暗色として残る場合があるため、
        // 暗色比率を条件に含めず、青色比率を主な手掛かりにします。
        if (blueRatio >= 0.04)
        {
            return AbacusScreenVisualState.MainMenu;
        }

        // 車両一覧と車両詳細は同じ配色のため、ここでは一覧系として返し、
        // 詳細かどうかはabx-cs-sk.ucsの子ウィンドウ有無と組み合わせて判定します。
        if (blueRatio >= 0.0015)
        {
            return AbacusScreenVisualState.VehicleList;
        }

        // 拡大画像は車検証の灰色罫線が画面の大部分を占め、青い入力枠がほぼありません。
        if (grayInkRatio >= 0.08)
        {
            return AbacusScreenVisualState.ExpandedImage;
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
