using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace VehicleManagement.Companion.Services;

public sealed record AbacusClipboardInspection(
    uint SequenceNumber,
    IReadOnlyList<string> Formats,
    bool HasBitmapImage,
    int? PixelWidth,
    int? PixelHeight,
    int FileCount,
    IReadOnlyList<string> FileExtensions)
{
    public bool HasPotentialImageData =>
        HasBitmapImage ||
        FileCount > 0 ||
        Formats.Any(format =>
            format.Contains("bitmap", StringComparison.OrdinalIgnoreCase) ||
            format.Contains("dib", StringComparison.OrdinalIgnoreCase) ||
            format.Contains("png", StringComparison.OrdinalIgnoreCase) ||
            format.Contains("jpeg", StringComparison.OrdinalIgnoreCase) ||
            format.Contains("jfif", StringComparison.OrdinalIgnoreCase));
}

public sealed record AbacusClipboardImage(
    uint SequenceNumber,
    BitmapSource Image);

public sealed class AbacusClipboardInspector
{
    private const int MaximumAttempts = 4;

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    public async Task<AbacusClipboardInspection> InspectAsync(CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return Inspect();
            }
            catch (COMException) when (attempt < MaximumAttempts)
            {
                await Task.Delay(100, cancellationToken);
            }
        }

        throw new InvalidOperationException("クリップボードが他のアプリケーションで使用中です。少し待ってから再試行してください。");
    }

    public async Task<AbacusClipboardImage> ReadImageAsync(
        uint expectedSequenceNumber,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var sequenceBefore = GetClipboardSequenceNumber();
                if (sequenceBefore != expectedSequenceNumber)
                {
                    throw new InvalidOperationException("診断後にクリップボードの内容が変わりました。ABACUS画像をもう一度コピーして再診断してください。");
                }

                var image = Clipboard.GetImage()
                    ?? throw new InvalidOperationException("クリップボードから標準画像を取得できません。");
                var sequenceAfter = GetClipboardSequenceNumber();
                if (sequenceBefore != sequenceAfter)
                {
                    throw new InvalidOperationException("画像の読取中にクリップボードの内容が変わりました。再診断してください。");
                }

                if (image.CanFreeze)
                {
                    image.Freeze();
                }

                return new AbacusClipboardImage(sequenceAfter, image);
            }
            catch (COMException) when (attempt < MaximumAttempts)
            {
                await Task.Delay(100, cancellationToken);
            }
        }

        throw new InvalidOperationException("クリップボードが他のアプリケーションで使用中です。少し待ってから再試行してください。");
    }

    private static AbacusClipboardInspection Inspect()
    {
        var sequenceBefore = GetClipboardSequenceNumber();
        var dataObject = Clipboard.GetDataObject();
        if (dataObject is null)
        {
            return new AbacusClipboardInspection(sequenceBefore, [], false, null, null, 0, []);
        }

        var formats = dataObject.GetFormats(autoConvert: false)
            .Where(format => !string.IsNullOrWhiteSpace(format))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToArray();

        var image = Clipboard.GetImage();
        var fileExtensions = new List<string>();
        var fileCount = 0;
        if (dataObject.GetDataPresent(DataFormats.FileDrop, autoConvert: false) &&
            dataObject.GetData(DataFormats.FileDrop, autoConvert: false) is string[] files)
        {
            fileCount = files.Length;
            fileExtensions.AddRange(files
                .Select(Path.GetExtension)
                .Where(extension => !string.IsNullOrWhiteSpace(extension))
                .Select(extension => extension!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .Take(10));
        }

        var sequenceAfter = GetClipboardSequenceNumber();
        if (sequenceBefore != sequenceAfter)
        {
            throw new COMException("クリップボードの内容が診断中に変わりました。");
        }

        return new AbacusClipboardInspection(
            sequenceAfter,
            formats,
            image is not null,
            image?.PixelWidth,
            image?.PixelHeight,
            fileCount,
            fileExtensions);
    }
}
