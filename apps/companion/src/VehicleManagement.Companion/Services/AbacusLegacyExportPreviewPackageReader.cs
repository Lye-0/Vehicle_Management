using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;

namespace VehicleManagement.Companion.Services;

public sealed record AbacusLegacyExportPreviewPackageFile(
    string FileName,
    string Kind,
    int RowCount,
    long SizeBytes,
    string Sha256);

public sealed record AbacusLegacyExportPreviewPackageResult(
    string PackagePath,
    string ManifestPath,
    string ManifestSha256,
    int CustomerRowCount,
    int VehicleRowCount,
    int SalesRowCount,
    int MaintenanceRowCount,
    int SkippedBlankCustomerRows,
    int SkippedMaintenanceWithoutVehicleRows,
    int AmbiguousVehicleRows,
    IReadOnlyList<AbacusLegacyExportPreviewPackageFile> DataFiles,
    IReadOnlyList<AbacusLegacyExportPreviewRow> Rows,
    IReadOnlyList<string> Warnings);

/// <summary>
/// 固定列CSVから作成した登録前候補パッケージを、書き込みなしで再検証します。
/// マニフェスト、CSVのSHA-256・UTF-8・見出し・列数・行数を確認し、
/// DB、Web API、Object Storage、ABACUS原本には接続しません。
/// </summary>
public sealed class AbacusLegacyExportPreviewPackageReader
{
    private const string ManifestFileName = "manifest.json";
    private const long MaximumManifestBytes = 8L * 1024 * 1024;
    private const long MaximumDataFileBytes = 64L * 1024 * 1024;
    private const int MaximumRows = 5_000;
    private const int MaximumFieldCharacters = 256 * 1024;
    private const int MaximumPreviewRows = 5_000;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private static readonly PackageFileSpec[] PackageFileSpecs =
    [
        new("customers.csv", "顧客", 10),
        new("vehicles.csv", "車両", 16),
        new("sales.csv", "販売書類", 16),
        new("maintenance.csv", "整備書類", 18),
    ];
    private static readonly string[][] ExpectedHeaders =
    [
        ["顧客ID", "顧客番号", "顧客名", "ふりがな", "電話番号", "メールアドレス", "郵便番号", "住所", "メモ", "車両台数"],
        ["車両ID", "顧客ID", "顧客名", "メーカー", "車名", "型式", "登録番号", "車台番号", "年式", "車検満了日", "走行距離", "車体色", "排気量", "ミッション", "記録簿", "備考"],
        ["書類ID", "書類番号", "書類種別", "ステータス", "顧客名", "車名", "登録番号", "発行日", "支払期限", "税率", "小計", "消費税", "合計", "明細", "備考", "明細詳細"],
        ["書類ID", "書類番号", "書類種別", "入庫区分", "ステータス", "顧客名", "車名", "登録番号", "入庫日", "出庫予定日", "支払期限", "税率", "小計", "消費税", "合計", "明細", "備考", "明細詳細"],
    ];

    public Task<AbacusLegacyExportPreviewPackageResult> ReadAsync(
        string packageFolder,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Read(packageFolder, cancellationToken), cancellationToken);

    private static AbacusLegacyExportPreviewPackageResult Read(
        string packageFolder,
        CancellationToken cancellationToken)
    {
        var packageRoot = ValidateFolder(packageFolder);
        var manifestPath = Path.Combine(packageRoot, ManifestFileName);
        var manifestBytes = ReadBytes(manifestPath, MaximumManifestBytes, "登録前候補マニフェスト");
        var manifestSha256 = Convert.ToHexString(SHA256.HashData(manifestBytes));
        ManifestDocument manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ManifestDocument>(manifestBytes, JsonOptions)
                ?? throw new InvalidDataException("登録前候補マニフェストが空です。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("登録前候補マニフェストのJSONが不正です。", exception);
        }

        ValidateManifest(manifest);
        var dataFiles = new List<AbacusLegacyExportPreviewPackageFile>(PackageFileSpecs.Length);
        for (var index = 0; index < PackageFileSpecs.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var specification = PackageFileSpecs[index];
            var dataFile = manifest.DataFiles!
                .Where(file => string.Equals(file.FileName, specification.FileName, StringComparison.Ordinal))
                .ToArray();
            if (dataFile.Length != 1)
            {
                throw new InvalidDataException($"マニフェストに{specification.FileName}が1件だけありません。");
            }

            var file = dataFile[0];
            var path = SafeChildPath(packageRoot, file.FileName!, "登録前候補CSV");
            var bytes = ReadBytes(path, MaximumDataFileBytes, file.FileName!);
            if (file.SizeBytes != bytes.LongLength)
            {
                throw new InvalidDataException($"{file.FileName}のサイズがマニフェストと一致しません。");
            }

            var sha256 = Convert.ToHexString(SHA256.HashData(bytes));
            if (!string.Equals(sha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"{file.FileName}のSHA-256がマニフェストと一致しません。");
            }

            var rowCount = ValidateCsv(
                bytes,
                specification,
                ExpectedHeaders[index],
                cancellationToken);
            var expectedRows = specification.FileName switch
            {
                "customers.csv" => manifest.Summary!.CustomerRows,
                "vehicles.csv" => manifest.Summary!.VehicleRows,
                "sales.csv" => manifest.Summary!.SalesRows,
                "maintenance.csv" => manifest.Summary!.MaintenanceRows,
                _ => -1,
            };
            if (rowCount != expectedRows)
            {
                throw new InvalidDataException(
                    $"{file.FileName}の行数がマニフェストと一致しません。期待値{expectedRows}行、実際{rowCount}行。");
            }

            dataFiles.Add(new AbacusLegacyExportPreviewPackageFile(
                file.FileName!,
                specification.Kind,
                rowCount,
                bytes.LongLength,
                sha256));
        }

        var rows = manifest.Rows ?? [];
        ValidateRows(rows);
        return new AbacusLegacyExportPreviewPackageResult(
            packageRoot,
            manifestPath,
            manifestSha256,
            manifest.Summary!.CustomerRows,
            manifest.Summary.VehicleRows,
            manifest.Summary.SalesRows,
            manifest.Summary.MaintenanceRows,
            manifest.Summary.SkippedBlankCustomerRows,
            manifest.Summary.SkippedMaintenanceWithoutVehicleRows,
            manifest.Summary.AmbiguousVehicleRows,
            dataFiles,
            rows,
            manifest.Warnings ?? []);
    }

    private static void ValidateManifest(ManifestDocument manifest)
    {
        if (manifest.Version != 1 ||
            !string.Equals(manifest.Kind, "abacus-export-import-preview", StringComparison.Ordinal) ||
            !string.Equals(manifest.Status, "preview-only", StringComparison.Ordinal))
        {
            throw new InvalidDataException("対応していないABACUS固定列CSV候補パッケージです。");
        }

        if (manifest.Source is null || manifest.Summary is null || manifest.DataFiles is null ||
            !IsSha256(manifest.Source.Fingerprint) ||
            manifest.Source.Files is null || manifest.Source.Files.Count == 0 ||
            manifest.DataFiles.Count != PackageFileSpecs.Length)
        {
            throw new InvalidDataException("候補パッケージのマニフェスト構造が不正です。");
        }

        ValidateSummary(manifest.Summary);
        foreach (var sourceFile in manifest.Source.Files)
        {
            ValidateSafeFileName(sourceFile.FileName, "入力CSVファイル名");
            ValidateText(sourceFile.Kind, "入力CSVの種類");
            if (!IsSha256(sourceFile.Sha256) ||
                sourceFile.TotalRows < 0 || sourceFile.TotalRows > MaximumRows ||
                sourceFile.ValidRows < 0 || sourceFile.ValidRows > sourceFile.TotalRows)
            {
                throw new InvalidDataException("入力CSVの検証情報が不正です。");
            }
        }

        var duplicateDataFiles = manifest.DataFiles
            .Where(file => !string.IsNullOrWhiteSpace(file.FileName))
            .GroupBy(file => file.FileName!, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateDataFiles is not null)
        {
            throw new InvalidDataException($"候補CSVが重複しています: {duplicateDataFiles.Key}");
        }

        foreach (var dataFile in manifest.DataFiles)
        {
            ValidateSafeFileName(dataFile.FileName, "候補CSVファイル名");
            if (dataFile.SizeBytes < 0 || dataFile.SizeBytes > MaximumDataFileBytes || !IsSha256(dataFile.Sha256))
            {
                throw new InvalidDataException("候補CSVのサイズ・SHA-256が不正です。");
            }
        }
    }

    private static void ValidateSummary(SummaryDocument summary)
    {
        if (summary.CustomerRows is < 0 or > MaximumRows ||
            summary.VehicleRows is < 0 or > MaximumRows ||
            summary.SalesRows is < 0 or > MaximumRows ||
            summary.MaintenanceRows is < 0 or > MaximumRows ||
            summary.SkippedBlankCustomerRows is < 0 or > MaximumRows * 3 ||
            summary.SkippedMaintenanceWithoutVehicleRows is < 0 or > MaximumRows ||
            summary.AmbiguousVehicleRows is < 0 or > MaximumRows * 2)
        {
            throw new InvalidDataException("候補パッケージの件数が上限または下限を超えています。");
        }
    }

    private static int ValidateCsv(
        byte[] bytes,
        PackageFileSpec specification,
        IReadOnlyList<string> expectedHeader,
        CancellationToken cancellationToken)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException($"{specification.FileName}はUTF-8として読み取れません。", exception);
        }

        using var parser = new TextFieldParser(new StringReader(text))
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false,
        };
        parser.SetDelimiters(",");
        if (parser.EndOfData)
        {
            throw new InvalidDataException($"{specification.FileName}が空です。");
        }

        string[] header;
        try
        {
            header = parser.ReadFields() ?? [];
        }
        catch (MalformedLineException exception)
        {
            throw new InvalidDataException($"{specification.FileName}の見出し行が不正です。", exception);
        }

        if (header.Length != expectedHeader.Count)
        {
            throw new InvalidDataException($"{specification.FileName}の見出し列数が不正です。");
        }

        header[0] = header[0].TrimStart('\uFEFF');
        if (!header.SequenceEqual(expectedHeader, StringComparer.Ordinal))
        {
            throw new InvalidDataException($"{specification.FileName}の見出しが既定形式と一致しません。");
        }

        var dataRows = 0;
        while (!parser.EndOfData)
        {
            cancellationToken.ThrowIfCancellationRequested();
            dataRows++;
            if (dataRows > MaximumRows)
            {
                throw new InvalidDataException($"{specification.FileName}の行数が上限{MaximumRows:N0}行を超えています。");
            }

            string[] fields;
            try
            {
                fields = parser.ReadFields() ?? [];
            }
            catch (MalformedLineException exception)
            {
                throw new InvalidDataException($"{specification.FileName}の{dataRows}行目が不正です。", exception);
            }

            if (fields.Length != specification.ExpectedColumns ||
                fields.Any(field => field.Length > MaximumFieldCharacters || ContainsUnsafeControl(field)))
            {
                throw new InvalidDataException($"{specification.FileName}の{dataRows}行目の形式が不正です。");
            }
        }

        return dataRows;
    }

    private static void ValidateRows(IReadOnlyList<AbacusLegacyExportPreviewRow> rows)
    {
        if (rows.Count > MaximumPreviewRows)
        {
            throw new InvalidDataException($"マニフェストの表示行数が上限{MaximumPreviewRows:N0}行を超えています。");
        }

        foreach (var row in rows)
        {
            ValidateText(row.Kind, "行の種類");
            ValidateSafeFileName(row.SourceFileName, "行の出典ファイル名");
            ValidateText(row.CustomerName, "行の顧客名");
            ValidateText(row.VehicleName, "行の車名");
            ValidateText(row.DocumentNumber, "行の書類番号");
            ValidateText(row.MatchStatus, "行の判定");
            ValidateText(row.Warning, "行の理由");
            if (row.SourceRowNumber < 1 || row.SourceRowNumber > MaximumRows)
            {
                throw new InvalidDataException("行の出典番号が不正です。");
            }
        }
    }

    private static byte[] ReadBytes(string path, long maximumBytes, string label)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException($"{label}が存在しないか、リンクです。");
        }

        if (file.Length <= 0 || file.Length > maximumBytes)
        {
            throw new InvalidDataException($"{label}のサイズが上限以内ではありません。");
        }

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        var bytes = new byte[checked((int)file.Length)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
            {
                throw new InvalidDataException($"{label}を最後まで読み取れませんでした。");
            }

            offset += read;
        }

        return bytes;
    }

    private static string ValidateFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("登録前候補パッケージを指定してください。", nameof(path));
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var directory = new DirectoryInfo(root);
        if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("登録前候補パッケージが存在しないか、リンクです。");
        }

        return root;
    }

    private static string SafeChildPath(string root, string fileName, string label)
    {
        ValidateSafeFileName(fileName, label);
        var path = Path.GetFullPath(Path.Combine(root, fileName));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{label}がパッケージ外を指しています。");
        }

        return path;
    }

    private static void ValidateSafeFileName(string? fileName, string label)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal) ||
            fileName.Contains(Path.DirectorySeparatorChar) ||
            fileName.Contains(Path.AltDirectorySeparatorChar) ||
            Path.IsPathRooted(fileName))
        {
            throw new InvalidDataException($"{label}が不正です。");
        }
    }

    private static void ValidateText(string? value, string label)
    {
        if (value is null || value.Length > MaximumFieldCharacters || ContainsUnsafeControl(value))
        {
            throw new InvalidDataException($"{label}が不正です。");
        }
    }

    private static bool ContainsUnsafeControl(string value) =>
        value.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t');

    private static bool IsSha256(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length == 64 && value.All(Uri.IsHexDigit);

    private sealed record PackageFileSpec(string FileName, string Kind, int ExpectedColumns);

    private sealed record ManifestDocument(
        int Version,
        string? Kind,
        string? Status,
        DateTime CreatedAtUtc,
        SourceDocument? Source,
        SummaryDocument? Summary,
        List<DataFileDocument>? DataFiles,
        List<string>? Warnings,
        List<AbacusLegacyExportPreviewRow>? Rows);

    private sealed record SourceDocument(
        string? FolderPath,
        List<SourceFileDocument>? Files,
        string? Fingerprint);

    private sealed record SourceFileDocument(
        string? FileName,
        string? Kind,
        string? Sha256,
        int TotalRows,
        int ValidRows);

    private sealed record SummaryDocument(
        int CustomerRows,
        int VehicleRows,
        int SalesRows,
        int MaintenanceRows,
        int SkippedBlankCustomerRows,
        int SkippedMaintenanceWithoutVehicleRows,
        int AmbiguousVehicleRows);

    private sealed record DataFileDocument(
        string? FileName,
        long SizeBytes,
        string? Sha256);
}
