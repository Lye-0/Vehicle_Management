using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace VehicleManagement.AbacusImport;

public sealed record AbacusVehicleExportRow(
    string FileName,
    int RowNumber,
    string CustomerName,
    string Maker,
    string VehicleName,
    string ModelYear,
    string Displacement,
    string Transmission,
    string BodyColor,
    string Model,
    string ChassisNumber,
    string RegistrationNumber,
    string Mileage,
    string InspectionDate,
    string InspectionRecord);

public sealed record AbacusVehicleExportFileResult(
    string FileName,
    int TotalRows,
    int ValidRows,
    IReadOnlyList<AbacusVehicleExportRow> Rows,
    IReadOnlyList<AbacusParseError> Errors,
    string? Sha256)
{
    public bool IsValid => Errors.Count == 0;
}

public sealed record AbacusVehicleExportReadResult(
    string FolderPath,
    IReadOnlyList<AbacusVehicleExportFileResult> Files,
    IReadOnlyList<AbacusVehicleExportRow> Rows,
    IReadOnlyList<AbacusParseError> Errors)
{
    public bool IsValid => Files.Count > 0 && Errors.Count == 0 && Files.All(file => file.IsValid);
}

public sealed class AbacusVehicleExportReader
{
    private const long MaximumFileBytes = 5L * 1024 * 1024;
    private const int MaximumRows = 5_000;
    private const int ExpectedColumns = 23;
    private const int MaximumFieldCharacters = 256 * 1024;
    private static readonly string[] FileNames = ["syaryou.csv", "syaryou2.csv"];
    private static readonly HashSet<char> AllowedLegacyControls = ['\u0004', '\u000B', '\u001D'];

    public Task<AbacusVehicleExportReadResult> ReadAsync(
        string folderPath,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Read(folderPath, cancellationToken), cancellationToken);

    private static AbacusVehicleExportReadResult Read(
        string folderPath,
        CancellationToken cancellationToken)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
        var directory = new DirectoryInfo(root);
        if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("安全に読み取れるABACUS車両一覧フォルダーではありません。");
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var files = ResolveFileNames(root)
            .Select(fileName => ParseFile(root, fileName, cancellationToken))
            .ToList();
        if (files.Count == 0)
        {
            throw new InvalidDataException("syaryou.csvまたはsyaryou2.csvが見つかりません。");
        }

        var errors = files
            .SelectMany(file => file.Errors.Select(error => new AbacusParseError(
                error.RowNumber,
                $"{file.FileName}: {error.Message}")))
            .ToList();
        return new AbacusVehicleExportReadResult(
            root,
            files,
            files.SelectMany(file => file.Rows).ToList(),
            errors);
    }

    private static IReadOnlyList<string> ResolveFileNames(string root)
    {
        var canonical = FileNames
            .Where(fileName => File.Exists(Path.Combine(root, fileName)))
            .ToArray();
        if (canonical.Length > 0)
        {
            return canonical;
        }

        // ABACUSの単品出力では、車両一覧が「顧客名.csv」などの名前になります。
        // 23列という形式を確認したCSVだけを車両一覧として扱い、販売・整備CSVを誤って読みません。
        return Directory.EnumerateFiles(root, "*.csv", System.IO.SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(fileName => !string.IsNullOrWhiteSpace(fileName))
            .Where(fileName => HasVehicleAliasSuffix(fileName!) ||
                               HasExpectedColumnCount(Path.Combine(root, fileName!), ExpectedColumns))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(fileName => fileName, StringComparer.OrdinalIgnoreCase)
            .ToArray()!;
    }

    private static bool HasVehicleAliasSuffix(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return stem.EndsWith("_syaryou", StringComparison.OrdinalIgnoreCase) ||
               stem.EndsWith("-syaryou", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(stem, "syaryou", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasExpectedColumnCount(string path, int expectedColumns)
    {
        try
        {
            var parserEncoding = Encoding.GetEncoding(932);
            using var parser = new TextFieldParser(path, parserEncoding, detectEncoding: false)
            {
                TextFieldType = FieldType.Delimited,
                HasFieldsEnclosedInQuotes = true,
                TrimWhiteSpace = false,
            };
            parser.SetDelimiters(",");
            return !parser.EndOfData && (parser.ReadFields()?.Length ?? 0) == expectedColumns;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (MalformedLineException)
        {
            return false;
        }
    }

    private static AbacusVehicleExportFileResult ParseFile(
        string root,
        string fileName,
        CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(Path.Combine(root, fileName));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return Invalid(fileName, "フォルダー外のCSVは読み取れません。");
        }

        FileInfo file;
        try
        {
            file = new FileInfo(path);
            if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return Invalid(fileName, "CSVが存在しない、またはリンクです。");
            }

            if (file.Length > MaximumFileBytes)
            {
                return Invalid(fileName, $"CSVサイズが上限{MaximumFileBytes:N0} bytesを超えています。");
            }
        }
        catch (IOException exception)
        {
            return Invalid(fileName, $"CSVの情報を読み取れません: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            return Invalid(fileName, $"CSVの権限を確認できません: {exception.Message}");
        }

        var strictEncoding = Encoding.GetEncoding(
            932,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
        var parserEncoding = Encoding.GetEncoding(932);
        var rows = new List<AbacusVehicleExportRow>();
        var errors = new List<AbacusParseError>();
        var totalRows = 0;
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

                if (fields.Length != ExpectedColumns)
                {
                    errors.Add(new AbacusParseError(
                        totalRows,
                        $"列数が不正です。期待値{ExpectedColumns}列、実際{fields.Length}列。"));
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

                rows.Add(new AbacusVehicleExportRow(
                    fileName,
                    totalRows,
                    fields[0],
                    fields[11],
                    fields[12],
                    fields[13],
                    fields[14],
                    fields[15],
                    fields[16],
                    fields[17],
                    fields[18],
                    fields[19],
                    fields[20],
                    fields[21],
                    fields[22]));
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

        return new AbacusVehicleExportFileResult(fileName, totalRows, rows.Count, rows, errors, beforeSha256);
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

    private static AbacusVehicleExportFileResult Invalid(string fileName, string message) =>
        new(fileName, 0, 0, [], [new AbacusParseError(null, message)], null);
}
