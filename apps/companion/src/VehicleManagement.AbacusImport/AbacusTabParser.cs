using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace VehicleManagement.AbacusImport;

public sealed record AbacusTabSpecification(
    string FileName,
    string DisplayName,
    int ExpectedColumns,
    int CustomerNameColumn,
    int VehicleNameColumn,
    int ModelColumn,
    int MakerColumn,
    int RegistrationNumberColumn,
    int ChassisNumberColumn,
    int DocumentNumberColumn,
    int AddressColumn,
    int? PostalCodeColumn,
    IReadOnlyList<int> PhoneColumns);

public static class AbacusTabSpecifications
{
    public static readonly AbacusTabSpecification Sales = new(
        "BackUp-2.tab", "販売書類", 102, 0, 6, 8, 12, 13, 22, 51, 1, null, [4, 5]);

    public static readonly AbacusTabSpecification Maintenance = new(
        "BackUp-3.tab", "整備書類", 158, 0, 6, 8, 10, 11, 14, 77, 1, 31, [4, 5]);
}

public sealed record AbacusParsedRecord(int RowNumber, IReadOnlyList<string> Fields);

public sealed record AbacusParseError(int? RowNumber, string Message);

public sealed record AbacusTabParseResult(
    AbacusTabSpecification Specification,
    IReadOnlyList<AbacusParsedRecord> Records,
    IReadOnlyList<AbacusParseError> Errors,
    int LegacyControlCharacterCount)
{
    public bool IsValid => Errors.Count == 0;
}

public sealed class AbacusTabParser
{
    private const long MaximumFileBytes = 32L * 1024 * 1024;
    private const int MaximumRows = 100_000;
    private const int MaximumFieldCharacters = 256 * 1024;
    private static readonly HashSet<char> AllowedLegacyControls = ['\u0004', '\u000B', '\u001D'];

    public Task<AbacusTabParseResult> ParseAsync(
        string folderPath,
        AbacusTabSpecification specification,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Parse(folderPath, specification, cancellationToken), cancellationToken);

    private static AbacusTabParseResult Parse(
        string folderPath,
        AbacusTabSpecification specification,
        CancellationToken cancellationToken)
    {
        var records = new List<AbacusParsedRecord>();
        var errors = new List<AbacusParseError>();
        var controlCharacterCount = 0;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
        var path = Path.GetFullPath(Path.Combine(root, specification.FileName));
        if (!path.StartsWith($"{root}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            return Invalid(specification, "フォルダー外のファイルは解析できません。");
        }

        var file = new FileInfo(path);
        if (!file.Exists)
        {
            return Invalid(specification, $"必須ファイルがありません: {specification.FileName}");
        }

        if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return Invalid(specification, $"リンクまたは再解析ポイントは解析できません: {specification.FileName}");
        }

        if (file.Length > MaximumFileBytes)
        {
            return Invalid(specification, $"ファイルサイズが上限{MaximumFileBytes:N0} bytesを超えています。");
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var strictEncoding = Encoding.GetEncoding(
            932,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
        var parserEncoding = Encoding.GetEncoding(932);

        try
        {
            using var sourceLock = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.SequentialScan);
            using (var validationReader = new StreamReader(
                sourceLock,
                strictEncoding,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 1024 * 1024,
                leaveOpen: true))
            {
                var buffer = new char[64 * 1024];
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
            parser.SetDelimiters("\t");

            var rowNumber = 0;
            while (!parser.EndOfData)
            {
                cancellationToken.ThrowIfCancellationRequested();
                rowNumber++;
                if (rowNumber > MaximumRows)
                {
                    errors.Add(new AbacusParseError(rowNumber, $"行数が上限{MaximumRows:N0}行を超えています。"));
                    break;
                }

                string[] fields;
                try
                {
                    fields = parser.ReadFields() ?? [];
                }
                catch (MalformedLineException exception)
                {
                    errors.Add(new AbacusParseError(rowNumber, $"区切りまたは引用符の形式が不正です: {exception.Message}"));
                    break;
                }

                if (fields.Length != specification.ExpectedColumns)
                {
                    errors.Add(new AbacusParseError(
                        rowNumber,
                        $"列数が不正です。期待値{specification.ExpectedColumns}列、実際{fields.Length}列。"));
                    continue;
                }

                var recordIsSafe = true;
                for (var column = 0; column < fields.Length; column++)
                {
                    var value = fields[column];
                    if (value.Length > MaximumFieldCharacters)
                    {
                        errors.Add(new AbacusParseError(rowNumber, $"{column + 1}列目が文字数上限を超えています。"));
                        recordIsSafe = false;
                        break;
                    }

                    foreach (var character in value)
                    {
                        if (!char.IsControl(character) || character is '\r' or '\n' or '\t')
                        {
                            continue;
                        }

                        if (AllowedLegacyControls.Contains(character))
                        {
                            controlCharacterCount++;
                            continue;
                        }

                        errors.Add(new AbacusParseError(
                            rowNumber,
                            $"{column + 1}列目に許可されていない制御文字U+{(int)character:X4}があります。"));
                        recordIsSafe = false;
                        break;
                    }

                    if (!recordIsSafe)
                    {
                        break;
                    }
                }

                if (recordIsSafe)
                {
                    records.Add(new AbacusParsedRecord(rowNumber, fields));
                }
            }
        }
        catch (DecoderFallbackException exception)
        {
            errors.Add(new AbacusParseError(null, $"Shift-JISとして解釈できないバイトがあります: {exception.Message}"));
        }
        catch (IOException exception)
        {
            errors.Add(new AbacusParseError(null, $"ファイルを読み取れません: {exception.Message}"));
        }
        catch (UnauthorizedAccessException exception)
        {
            errors.Add(new AbacusParseError(null, $"ファイルを読み取る権限がありません: {exception.Message}"));
        }

        return new AbacusTabParseResult(specification, records, errors, controlCharacterCount);
    }

    private static AbacusTabParseResult Invalid(AbacusTabSpecification specification, string message) =>
        new(specification, [], [new AbacusParseError(null, message)], 0);
}
