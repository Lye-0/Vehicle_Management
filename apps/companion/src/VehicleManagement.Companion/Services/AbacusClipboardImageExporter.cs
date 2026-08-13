using System.IO;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;

namespace VehicleManagement.Companion.Services;

public sealed record AbacusImageExportResult(
    string FilePath,
    long FileSize,
    int PixelWidth,
    int PixelHeight,
    string Sha256);

public sealed class AbacusClipboardImageExporter
{
    private const long MaximumPixels = 50_000_000;
    private const long MaximumEncodedBytes = 256L * 1024 * 1024;

    public async Task<AbacusImageExportResult> ExportAsync(
        BitmapSource image,
        string destinationFolder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        var destinationRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationFolder));
        if (!Directory.Exists(destinationRoot))
        {
            throw new DirectoryNotFoundException("画像の保存先フォルダーが見つかりません。");
        }

        if (new DirectoryInfo(destinationRoot).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("リンクまたは再解析ポイントのフォルダーには保存できません。");
        }

        if (image.PixelWidth <= 0 || image.PixelHeight <= 0 ||
            (long)image.PixelWidth * image.PixelHeight > MaximumPixels)
        {
            throw new InvalidDataException("画像の寸法が許容範囲外です。");
        }

        using var encoded = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(encoded);
        if (encoded.Length <= 8 || encoded.Length > MaximumEncodedBytes)
        {
            throw new InvalidDataException("PNG画像のサイズが許容範囲外です。");
        }

        encoded.Position = 0;
        var decoder = new PngBitmapDecoder(encoded, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count != 1 ||
            decoder.Frames[0].PixelWidth != image.PixelWidth ||
            decoder.Frames[0].PixelHeight != image.PixelHeight)
        {
            throw new InvalidDataException("PNG画像の保存前検証に失敗しました。");
        }

        encoded.Position = 0;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(encoded, cancellationToken));
        encoded.Position = 0;
        var outputPath = CreateUniqueOutputPath(destinationRoot);
        var createdOutput = false;
        try
        {
            await using var output = new FileStream(
                outputPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            createdOutput = true;
            await encoded.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
        catch
        {
            if (createdOutput && File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }

            throw;
        }

        return new AbacusImageExportResult(
            outputPath,
            encoded.Length,
            image.PixelWidth,
            image.PixelHeight,
            hash);
    }

    private static string CreateUniqueOutputPath(string destinationRoot)
    {
        var baseName = $"ABACUS-image-{DateTime.Now:yyyyMMdd-HHmmss}";
        for (var suffix = 0; suffix < 1000; suffix++)
        {
            var name = suffix == 0 ? $"{baseName}.png" : $"{baseName}-{suffix}.png";
            var path = Path.Combine(destinationRoot, name);
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return path;
            }
        }

        throw new IOException("一意な画像ファイル名を作成できませんでした。");
    }
}
