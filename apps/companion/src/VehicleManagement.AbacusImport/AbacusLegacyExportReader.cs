using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace VehicleManagement.AbacusImport;

public sealed record AbacusLegacyExportRow(
    string FileName,
    int RowNumber,
    IReadOnlyList<string> Fields);

public sealed record AbacusLegacyExportColumnSample(
    string FileName,
    int RowNumber,
    int ColumnIndex,
    string Value);

public sealed record AbacusLegacyExportFileReadResult(
    string FileName,
    string Kind,
    int ExpectedColumns,
    int TotalRows,
    int BlankRequiredRows,
    int InvalidDateRows,
    IReadOnlyList<AbacusLegacyExportRow> Rows,
    IReadOnlyList<AbacusParseError> Errors,
    string? Sha256)
{
    private const int MaximumSampleCharacters = 512;

    public int ValidRows => Rows.Count;

    public bool IsValid => Errors.Count == 0 && InvalidDateRows == 0;

    public IReadOnlyList<AbacusLegacyExportColumnSample> FirstRowSamples =>
        Rows.Count == 0
            ? []
            : Rows[0].Fields
                .Select((value, index) => new AbacusLegacyExportColumnSample(
                    FileName,
                    Rows[0].RowNumber,
                    index,
                    FormatSampleValue(value)))
                .ToArray();

    private static string FormatSampleValue(string value)
    {
        var display = value
            .Replace('\u0004', '␄')
            .Replace('\u000B', '␋')
            .Replace('\u001D', '␝');
        return display.Length <= MaximumSampleCharacters
            ? display
            : display[..MaximumSampleCharacters] + "…";
    }
}

public sealed record AbacusLegacyExportReadResult(
    string FolderPath,
    IReadOnlyList<AbacusLegacyExportFileReadResult> Files,
    IReadOnlyList<AbacusParseError> Errors)
{
    public bool IsValid => Files.Count > 0 && Errors.Count == 0 && Files.All(file => file.IsValid);

    public int SalesRows => Files.Where(file => file.Kind == "販売書類").Sum(file => file.ValidRows);

    public int MaintenanceRows => Files.Where(file => file.Kind == "整備書類").Sum(file => file.ValidRows);

    public int VehicleRows => Files.Where(file => file.Kind == "車両一覧").Sum(file => file.ValidRows);

    public int VehicleFileCount => Files.Count(file => file.Kind == "車両一覧");

    public IReadOnlyList<AbacusLegacyExportColumnSample> FirstRowSamples =>
        Files.SelectMany(file => file.FirstRowSamples).ToArray();
}

/// <summary>
/// ABACUSの見出しなし固定列CSVを、書き込みなしで厳格に読み取ります。
/// 診断と列サンプル表示で同じパーサーを使うため、表示した値と後続変換の入力を一致させます。
/// </summary>
public sealed class AbacusLegacyExportReader
{
    private const long MaximumFileBytes = 5L * 1024 * 1024;
    private const int MaximumRows = 5_000;
    private const int MaximumFieldCharacters = 256 * 1024;
    private static readonly HashSet<char> AllowedLegacyControls = ['\u0004', '\u000B', '\u001D'];

    public static IReadOnlyList<ExportSpecification> Specifications { get; } =
    [
        new("hanbai.csv", "販売書類", 33, 5, 0, 1, null),
        new("seibi.csv", "整備書類", 29, 4, 0, 1, null),
        new("syaryou.csv", "車両一覧", 23, 0, null, null, 12),
        new("syaryou2.csv", "車両一覧", 23, 0, null, null, 12),
    ];

    public Task<AbacusLegacyExportReadResult> ReadAsync(
        string folderPath,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Read(folderPath, cancellationToken), cancellationToken);

    private static AbacusLegacyExportReadResult Read(
        string folderPath,
        CancellationToken cancellationToken)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
        var directory = new DirectoryInfo(root);
        if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("安全に読み取れるABACUSエクスポートフォルダーではありません。");
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var files = Specifications
            .Where(specification => File.Exists(Path.Combine(root, specification.FileName)))
            .Select(specification => ReadFile(root, specification, cancellationToken))
            .ToList();
        if (files.Count == 0)
        {
            throw new InvalidDataException("販売・整備・車両一覧のABACUS CSVが見つかりません。");
        }

        var errors = files
            .SelectMany(file => file.Errors.Select(error => new AbacusParseError(
                error.RowNumber,
                $"{file.FileName}: {error.Message}")))
            .ToList();
        return new AbacusLegacyExportReadResult(root, files, errors);
    }

    private static AbacusLegacyExportFileReadResult ReadFile(
        string root,
        ExportSpecification specification,
        CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(Path.Combine(root, specification.FileName));
        if (!path.StartsWith($"{root}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            return Invalid(specification, "フォルダー外のCSVは読み取れません。");
        }

        FileInfo file;
        try
        {
            file = new FileInfo(path);
            if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return Invalid(specification, "CSVが存在しない、またはリンクです。");
            }

            if (file.Length > MaximumFileBytes)
            {
                return Invalid(specification, $"CSVサイズが上限{MaximumFileBytes:N0} bytesを超えています。");
            }
        }
        catch (IOException exception)
        {
            return Invalid(specification, $"CSVの情報を読み取れません: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            return Invalid(specification, $"CSVの権限を確認できません: {exception.Message}");
        }

        var strictEncoding = Encoding.GetEncoding(
            932,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
        var parserEncoding = Encoding.GetEncoding(932);
        var rows = new List<AbacusLegacyExportRow>();
        var errors = new List<AbacusParseError>();
        var totalRows = 0;
        var blankRequiredRows = 0;
        var invalidDateRows = 0;
        string? beforeSha256 = null;
        try
        {
            beforeSha256 = CalculateSha256(path, cancellationToken);
            using (var sourceLock = new FileStream(
                       path,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       64 * 1024,
                       FileOptions.SequentialScan))
            using (var validationReader = new StreamReader(
                       sourceLock,
                       strictEncoding,
                       detectEncodingFromByteOrderMarks: false,
                       bufferSize: 64 * 1024,
                       leaveOpen: true))
            {
                var buffer = new char[32 * 1024];
                while (validationReader.Read(buffer, 0, buffer.Length) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            using var parser = new TextFieldParser(path, parserEncoding, detectEncoding: false)
            {
                TextFieldType = FieldType.Delimited,
                HasFieldsEnclosedInQuotes = true,
                TrimWhiteSpace = false,
            };
            parser.SetDelimiters(",");
            while (!parser.EndOfData)
            {
                cancellationToken.ThrowIfCancellationRequested();
                totalRows++;
                if (totalRows > MaximumRows)
                {
                    errors.Add(new AbacusParseError(totalRows, $"行数が上限{MaximumRows:N0}行を超えています。"));
                    break;
                }

                string[] fields;
                try
                {
                    fields = parser.ReadFields() ?? [];
                }
                catch (MalformedLineException exception)
                {
                    errors.Add(new AbacusParseError(totalRows, $"CSVの引用符または区切りが不正です: {exception.Message}"));
                    continue;
                }

                if (fields.Length != specification.ExpectedColumns)
                {
                    errors.Add(new AbacusParseError(
                        totalRows,
                        $"列数が不正です。期待値{specification.ExpectedColumns}列、実際{fields.Length}列。"));
                    continue;
                }

                var invalidField = fields
                    .Select((value, index) => (value, index))
                    .FirstOrDefault(item => item.value.Length > MaximumFieldCharacters ||
                        item.value.Any(character => char.IsControl(character) &&
                            character is not '\r' and not '\n' and not '\t' &&
                            !AllowedLegacyControls.Contains(character)));
                if (invalidField.value is not null)
                {
                    errors.Add(new AbacusParseError(
                        totalRows,
                        $"{invalidField.index + 1}列目が文字数上限を超えているか、許可されていない制御文字を含んでいます。"));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(fields[specification.RequiredTextColumn]) ||
                    specification.DocumentNumberColumn is int documentNumberColumn &&
                    string.IsNullOrWhiteSpace(fields[documentNumberColumn]))
                {
                    blankRequiredRows++;
                }
                if (specification.DateColumn is int dateColumn && !IsExportDate(fields[dateColumn]))
                {
                    invalidDateRows++;
                }

                rows.Add(new AbacusLegacyExportRow(specification.FileName, totalRows, fields));
            }

            var afterSha256 = CalculateSha256(path, cancellationToken);
            if (!string.Equals(beforeSha256, afterSha256, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new AbacusParseError(null, "解析中にCSVが変更されました。ABACUSを終了して再試行してください。"));
                beforeSha256 = null;
            }
        }
        catch (DecoderFallbackException exception)
        {
            errors.Add(new AbacusParseError(null, $"Shift-JISとして解釈できないバイトがあります: {exception.Message}"));
        }
        catch (IOException exception)
        {
            errors.Add(new AbacusParseError(null, $"CSVを読み取れません: {exception.Message}"));
        }
        catch (UnauthorizedAccessException exception)
        {
            errors.Add(new AbacusParseError(null, $"CSVを読み取る権限がありません: {exception.Message}"));
        }

        return new AbacusLegacyExportFileReadResult(
            specification.FileName,
            specification.Kind,
            specification.ExpectedColumns,
            totalRows,
            blankRequiredRows,
            invalidDateRows,
            rows,
            errors,
            beforeSha256);
    }

    private static string CalculateSha256(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.SequentialScan);
        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool IsExportDate(string value) =>
        DateTime.TryParseExact(value.Trim(), ["yyyy/M/d", "yyyy/MM/dd"], null,
            System.Globalization.DateTimeStyles.None, out _);

    private static AbacusLegacyExportFileReadResult Invalid(
        ExportSpecification specification,
        string message) =>
        new(
            specification.FileName,
            specification.Kind,
            specification.ExpectedColumns,
            0,
            0,
            0,
            [],
            [new AbacusParseError(null, message)],
            null);

    public sealed record ExportSpecification(
        string FileName,
        string Kind,
        int ExpectedColumns,
        int RequiredTextColumn,
        int? DateColumn,
        int? DocumentNumberColumn,
        int? VehicleNameColumn);
}
