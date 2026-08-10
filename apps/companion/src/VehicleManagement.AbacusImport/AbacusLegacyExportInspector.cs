using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace VehicleManagement.AbacusImport;

public sealed record AbacusLegacyExportFileAnalysis(
    string FileName,
    string Kind,
    int ExpectedColumns,
    int TotalRows,
    int ValidRows,
    int BlankRequiredRows,
    int InvalidDateRows,
    IReadOnlyList<AbacusParseError> Errors)
{
    public bool IsValid => Errors.Count == 0 && InvalidDateRows == 0;
}

public sealed record AbacusLegacyExportAnalysis(
    string FolderPath,
    IReadOnlyList<AbacusLegacyExportFileAnalysis> Files,
    DateTime AnalyzedAtUtc)
{
    public bool IsValid => Files.Count > 0 && Files.All(file => file.IsValid);
    public int SalesRows => Files.Where(file => file.Kind == "販売書類").Sum(file => file.ValidRows);
    public int MaintenanceRows => Files.Where(file => file.Kind == "整備書類").Sum(file => file.ValidRows);
    public int VehicleRows => Files.Where(file => file.Kind == "車両一覧").Sum(file => file.ValidRows);
    public int VehicleFileCount => Files.Count(file => file.Kind == "車両一覧");
}

public sealed class AbacusLegacyExportInspector
{
    private const long MaximumFileBytes = 5L * 1024 * 1024;
    private const int MaximumRows = 5_000;
    private const int MaximumFieldCharacters = 256 * 1024;
    private static readonly HashSet<char> AllowedLegacyControls = ['\u0004', '\u000B', '\u001D'];
    private static readonly ExportSpecification[] Specifications =
    [
        new("hanbai.csv", "販売書類", 33, 5, 0, 1, null),
        new("seibi.csv", "整備書類", 29, 4, 0, 1, null),
        new("syaryou.csv", "車両一覧", 23, 0, null, null, 12),
        new("syaryou2.csv", "車両一覧", 23, 0, null, null, 12),
    ];

    public Task<AbacusLegacyExportAnalysis> AnalyzeAsync(
        string folderPath,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Analyze(folderPath, cancellationToken), cancellationToken);

    private static AbacusLegacyExportAnalysis Analyze(string folderPath, CancellationToken cancellationToken)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
        var directory = new DirectoryInfo(root);
        if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("安全に読み取れるABACUSエクスポートフォルダーではありません。");
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var results = Specifications
            .Where(specification => File.Exists(Path.Combine(root, specification.FileName)))
            .Select(specification => AnalyzeFile(root, specification, cancellationToken))
            .ToList();
        if (results.Count == 0)
        {
            throw new InvalidDataException("販売・整備・車両一覧のABACUS CSVが見つかりません。");
        }

        return new AbacusLegacyExportAnalysis(root, results, DateTime.UtcNow);
    }

    private static AbacusLegacyExportFileAnalysis AnalyzeFile(
        string root,
        ExportSpecification specification,
        CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(Path.Combine(root, specification.FileName));
        if (!path.StartsWith($"{root}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            return Invalid(specification, "フォルダー外のCSVは読み取れません。");
        }

        var file = new FileInfo(path);
        if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.Length > MaximumFileBytes)
        {
            return Invalid(specification, "CSVが存在しない、リンクである、またはサイズ上限を超えています。");
        }

        var strictEncoding = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        var errors = new List<AbacusParseError>();
        var totalRows = 0;
        var validRows = 0;
        var blankRequiredRows = 0;
        var invalidDateRows = 0;
        try
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
            using (var reader = new StreamReader(stream, strictEncoding, false, 64 * 1024))
            {
                var buffer = new char[32 * 1024];
                while (reader.Read(buffer, 0, buffer.Length) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            using var parser = new TextFieldParser(path, Encoding.GetEncoding(932), false)
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
                    errors.Add(new AbacusParseError(totalRows, $"列数が不正です。期待値{specification.ExpectedColumns}列、実際{fields.Length}列。"));
                    continue;
                }

                if (fields.Any(field => field.Length > MaximumFieldCharacters ||
                                        field.Any(character => char.IsControl(character) &&
                                            character is not '\r' and not '\n' and not '\t' &&
                                            !AllowedLegacyControls.Contains(character))))
                {
                    errors.Add(new AbacusParseError(totalRows, "文字数上限または許可されていない制御文字を検出しました。"));
                    continue;
                }

                validRows++;
                if (string.IsNullOrWhiteSpace(fields[specification.RequiredTextColumn]) ||
                    specification.DocumentNumberColumn is int documentNumberColumn && string.IsNullOrWhiteSpace(fields[documentNumberColumn]) ||
                    specification.VehicleNameColumn is int vehicleNameColumn && string.IsNullOrWhiteSpace(fields[vehicleNameColumn]))
                {
                    blankRequiredRows++;
                }
                if (specification.DateColumn is int dateColumn && !IsExportDate(fields[dateColumn]))
                {
                    invalidDateRows++;
                }
            }
        }
        catch (DecoderFallbackException exception)
        {
            errors.Add(new AbacusParseError(null, $"Shift-JISとして解釈できないバイトがあります: {exception.Message}"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            errors.Add(new AbacusParseError(null, $"CSVを読み取れません: {exception.Message}"));
        }

        return new AbacusLegacyExportFileAnalysis(
            specification.FileName,
            specification.Kind,
            specification.ExpectedColumns,
            totalRows,
            validRows,
            blankRequiredRows,
            invalidDateRows,
            errors);
    }

    private static bool IsExportDate(string value) =>
        DateTime.TryParseExact(value.Trim(), ["yyyy/M/d", "yyyy/MM/dd"], null,
            System.Globalization.DateTimeStyles.None, out _);

    private static AbacusLegacyExportFileAnalysis Invalid(ExportSpecification specification, string message) =>
        new(specification.FileName, specification.Kind, specification.ExpectedColumns, 0, 0, 0, 0,
            [new AbacusParseError(null, message)]);

    private sealed record ExportSpecification(
        string FileName,
        string Kind,
        int ExpectedColumns,
        int RequiredTextColumn,
        int? DateColumn,
        int? DocumentNumberColumn,
        int? VehicleNameColumn);
}
