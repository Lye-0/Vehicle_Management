using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace VehicleManagement.Companion.Services;

public sealed record AbacusClipboardInspection(
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

public sealed class AbacusClipboardInspector
{
    private const int MaximumAttempts = 4;

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

    private static AbacusClipboardInspection Inspect()
    {
        var dataObject = Clipboard.GetDataObject();
        if (dataObject is null)
        {
            return new AbacusClipboardInspection([], false, null, null, 0, []);
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

        return new AbacusClipboardInspection(
            formats,
            image is not null,
            image?.PixelWidth,
            image?.PixelHeight,
            fileCount,
            fileExtensions);
    }
}
