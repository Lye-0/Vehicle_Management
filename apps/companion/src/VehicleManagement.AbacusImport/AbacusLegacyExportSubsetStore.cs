using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VehicleManagement.AbacusImport;

public sealed record AbacusLegacyExportCustomerOption(
    string Key,
    string DisplayName,
    string Name,
    string Address,
    int VehicleRows,
    int SalesRows,
    int MaintenanceRows)
{
    public int TotalRows => VehicleRows + SalesRows + MaintenanceRows;

    public bool HasVehicle => VehicleRows > 0;
}

public sealed record AbacusLegacyExportSubsetResult(
    string PackagePath,
    string ManifestPath,
    string ManifestSha256,
    string CustomerName,
    string CustomerAddress,
    int VehicleRows,
    int SalesRows,
    int MaintenanceRows,
    IReadOnlyList<string> DataFilePaths);

/// <summary>
/// 固定列CSVから選択した顧客の行だけを新しい検証用フォルダーへ抽出します。
/// 原本は読み取り専用で扱い、出力は常に標準ファイル名・CP932・見出しなしで作成します。
/// </summary>
public sealed class AbacusLegacyExportSubsetStore
{
    private const string ManifestFileName = "manifest.json";
    private const long MaximumOutputBytes = 64L * 1024 * 1024;
    private const string KeySeparator = "\u001F";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly AbacusLegacyExportReader reader;

    public AbacusLegacyExportSubsetStore(AbacusLegacyExportReader? reader = null)
    {
        this.reader = reader ?? new AbacusLegacyExportReader();
    }

    public IReadOnlyList<AbacusLegacyExportCustomerOption> GetCustomerOptions(
        AbacusLegacyExportReadResult input)
    {
        var options = new Dictionary<string, MutableCustomerOption>(StringComparer.Ordinal);
        foreach (var file in input.Files)
        {
            var customerColumn = GetCustomerColumn(file.Kind);
            var addressColumn = GetAddressColumn(file.Kind);
            if (customerColumn is null)
            {
                continue;
            }

            foreach (var row in file.Rows)
            {
                var name = NormalizeIdentity(GetField(row.Fields, customerColumn.Value));
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var address = addressColumn is int column
                    ? NormalizeIdentity(GetField(row.Fields, column))
                    : string.Empty;
                var key = BuildCustomerKey(name, address);
                if (!options.TryGetValue(key, out var option))
                {
                    option = new MutableCustomerOption(key, name, address);
                    options.Add(key, option);
                }

                switch (file.Kind)
                {
                    case "車両一覧":
                        option.VehicleRows++;
                        break;
                    case "販売書類":
                        option.SalesRows++;
                        break;
                    case "整備書類":
                        option.MaintenanceRows++;
                        break;
                }
            }
        }

        return options.Values
            .OrderBy(option => option.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(option => option.Address, StringComparer.OrdinalIgnoreCase)
            .Select(option => new AbacusLegacyExportCustomerOption(
                option.Key,
                FormatDisplayName(option.Name, option.Address),
                option.Name,
                option.Address,
                option.VehicleRows,
                option.SalesRows,
                option.MaintenanceRows))
            .ToArray();
    }

    public async Task<AbacusLegacyExportSubsetResult> CreateAsync(
        string sourceFolder,
        string destinationParent,
        string customerKey,
        CancellationToken cancellationToken = default)
    {
        var sourceRoot = ValidateFolder(sourceFolder, "ABACUSエクスポートフォルダー");
        var destinationRoot = ValidateFolder(destinationParent, "検証用CSVの保存先");
        if (IsSameOrSubPath(destinationRoot, sourceRoot))
        {
            throw new InvalidDataException("原本フォルダー自身またはその配下には抽出先を作成できません。原本と別の保存先を選択してください。");
        }

        var input = await reader.ReadAsync(sourceRoot, cancellationToken);
        if (!input.IsValid)
        {
            throw new InvalidDataException("固定列CSVの診断に合格していないため、顧客抽出を実行できません。先に診断結果を確認してください。");
        }

        var option = GetCustomerOptions(input).FirstOrDefault(item =>
            string.Equals(item.Key, customerKey, StringComparison.Ordinal));
        if (option is null)
        {
            throw new InvalidDataException("選択した顧客が現在のCSV診断結果に見つかりません。もう一度診断して選択してください。");
        }

        if (!option.HasVehicle)
        {
            throw new InvalidDataException("車両一覧に一致する行がない顧客は、画像対応付けや書類紐付けの検証対象にできません。");
        }

        var vehicleRows = FilterRows(input, "車両一覧", option).ToArray();
        var salesRows = FilterRows(input, "販売書類", option).ToArray();
        var maintenanceRows = FilterRows(input, "整備書類", option).ToArray();
        var packagePath = CreateUniquePackageDirectory(destinationRoot);

        try
        {
            var outputFiles = new List<SubsetOutputFile>();
            outputFiles.Add(await WriteCsvAsync(
                Path.Combine(packagePath, "hanbai.csv"),
                salesRows,
                cancellationToken));
            outputFiles.Add(await WriteCsvAsync(
                Path.Combine(packagePath, "seibi.csv"),
                maintenanceRows,
                cancellationToken));
            outputFiles.Add(await WriteCsvAsync(
                Path.Combine(packagePath, "syaryou.csv"),
                vehicleRows,
                cancellationToken));
            if (outputFiles.Sum(file => file.SizeBytes) > MaximumOutputBytes)
            {
                throw new InvalidDataException($"抽出CSVの合計サイズが上限{MaximumOutputBytes:N0} bytesを超えています。");
            }

            var manifest = new SubsetManifest(
                Version: 1,
                Kind: "abacus-export-customer-subset",
                Status: "local-verification-only",
                CreatedAtUtc: DateTime.UtcNow,
                SourceFolder: sourceRoot,
                SelectedCustomer: new SelectedCustomer(option.Key, option.Name, option.Address),
                SourceFiles: input.Files.Select(file => new SourceFile(
                    file.FileName,
                    file.Kind,
                    file.Sha256 ?? throw new InvalidDataException($"{file.FileName}のSHA-256を確認できません。"),
                    file.TotalRows,
                    file.ValidRows)).ToArray(),
                OutputFiles: outputFiles,
                Summary: new SubsetSummary(vehicleRows.Length, salesRows.Length, maintenanceRows.Length),
                Warnings:
                [
                    "このフォルダーは選択顧客の検証用コピーです。ABACUS原本は変更していません。",
                    "空欄の顧客名行は選択対象に含めていません。",
                    "同名で住所が異なる顧客は別候補として扱います。住所が空欄の行は自動的に補完しません。",
                ]);
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
            var manifestPath = Path.Combine(packagePath, ManifestFileName);
            await File.WriteAllBytesAsync(manifestPath, manifestBytes, cancellationToken);
            var manifestSha256 = Convert.ToHexString(SHA256.HashData(manifestBytes));

            var validation = await reader.ReadAsync(packagePath, cancellationToken);
            if (!validation.IsValid || validation.VehicleFileCount != 1 || validation.VehicleRows != vehicleRows.Length ||
                validation.SalesRows != salesRows.Length || validation.MaintenanceRows != maintenanceRows.Length)
            {
                throw new InvalidDataException("抽出後CSVの再読込検証に失敗しました。原本は変更されていません。");
            }

            return new AbacusLegacyExportSubsetResult(
                packagePath,
                manifestPath,
                manifestSha256,
                option.Name,
                option.Address,
                vehicleRows.Length,
                salesRows.Length,
                maintenanceRows.Length,
                outputFiles.Select(file => Path.Combine(packagePath, file.FileName)).ToArray());
        }
        catch
        {
            if (Directory.Exists(packagePath) && IsSameOrSubPath(packagePath, destinationRoot))
            {
                Directory.Delete(packagePath, recursive: true);
            }

            throw;
        }
    }

    private static IEnumerable<AbacusLegacyExportRow> FilterRows(
        AbacusLegacyExportReadResult input,
        string kind,
        AbacusLegacyExportCustomerOption option)
    {
        var customerColumn = GetCustomerColumn(kind);
        if (customerColumn is null)
        {
            return [];
        }

        var addressColumn = GetAddressColumn(kind);
        return input.Files
            .Where(file => file.Kind == kind)
            .SelectMany(file => file.Rows)
            .Where(row =>
            {
                var name = NormalizeIdentity(GetField(row.Fields, customerColumn.Value));
                var address = addressColumn is int column
                    ? NormalizeIdentity(GetField(row.Fields, column))
                    : string.Empty;
                return string.Equals(name, option.Name, StringComparison.Ordinal) &&
                       string.Equals(address, option.Address, StringComparison.Ordinal);
            });
    }

    private static int? GetCustomerColumn(string kind) => kind switch
    {
        "車両一覧" => 0,
        "販売書類" => 5,
        "整備書類" => 4,
        _ => null,
    };

    private static int? GetAddressColumn(string kind) => kind switch
    {
        "車両一覧" => 3,
        "販売書類" => 8,
        "整備書類" => 7,
        _ => null,
    };

    private static string GetField(IReadOnlyList<string> fields, int index) =>
        index >= 0 && index < fields.Count ? fields[index] : string.Empty;

    private static string NormalizeIdentity(string value) =>
        string.Join(" ", value.Normalize(NormalizationForm.FormKC).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string BuildCustomerKey(string name, string address) => name + KeySeparator + address;

    private static string FormatDisplayName(string name, string address) =>
        string.IsNullOrEmpty(address) ? name : $"{name}（住所: {address}）";

    private static async Task<SubsetOutputFile> WriteCsvAsync(
        string path,
        IReadOnlyList<AbacusLegacyExportRow> rows,
        CancellationToken cancellationToken)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(
            932,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
        var builder = new StringBuilder();
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.AppendJoin(',', row.Fields.Select(EscapeCsvField));
            builder.AppendLine();
        }

        await File.WriteAllTextAsync(path, builder.ToString(), encoding, cancellationToken);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        return new SubsetOutputFile(
            Path.GetFileName(path),
            rows.Count,
            bytes.LongLength,
            Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static string EscapeCsvField(string value)
    {
        if (!value.Contains(',', StringComparison.Ordinal) &&
            !value.Contains('"', StringComparison.Ordinal) &&
            !value.Contains('\r', StringComparison.Ordinal) &&
            !value.Contains('\n', StringComparison.Ordinal))
        {
            return value;
        }

        return '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    }

    private static string ValidateFolder(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidDataException($"{label}を指定してください。");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var directory = new DirectoryInfo(root);
        if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException($"{label}が存在しないか、リンクです。");
        }

        return root;
    }

    private static string CreateUniquePackageDirectory(string destinationRoot)
    {
        var prefix = $"ABACUS-Export-Subset-{DateTime.Now:yyyyMMdd-HHmmss}";
        for (var index = 0; index < 1000; index++)
        {
            var suffix = index == 0 ? string.Empty : $"-{index:D3}";
            var path = Path.Combine(destinationRoot, prefix + suffix);
            if (!Directory.Exists(path) && !File.Exists(path))
            {
                Directory.CreateDirectory(path);
                return path;
            }
        }

        throw new IOException("検証用CSVの保存先フォルダーを一意に作成できません。");
    }

    private static bool IsSameOrSubPath(string candidate, string root)
    {
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               normalizedCandidate.StartsWith($"{normalizedRoot}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class MutableCustomerOption(string key, string name, string address)
    {
        public string Key { get; } = key;
        public string Name { get; } = name;
        public string Address { get; } = address;
        public int VehicleRows { get; set; }
        public int SalesRows { get; set; }
        public int MaintenanceRows { get; set; }
    }

    private sealed record SourceFile(
        string FileName,
        string Kind,
        string Sha256,
        int TotalRows,
        int ValidRows);

    private sealed record SelectedCustomer(string Key, string Name, string Address);

    private sealed record SubsetOutputFile(
        string FileName,
        int Rows,
        long SizeBytes,
        string Sha256);

    private sealed record SubsetSummary(
        int VehicleRows,
        int SalesRows,
        int MaintenanceRows);

    private sealed record SubsetManifest(
        int Version,
        string Kind,
        string Status,
        DateTime CreatedAtUtc,
        string SourceFolder,
        SelectedCustomer SelectedCustomer,
        IReadOnlyList<SourceFile> SourceFiles,
        IReadOnlyList<SubsetOutputFile> OutputFiles,
        SubsetSummary Summary,
        IReadOnlyList<string> Warnings);
}
