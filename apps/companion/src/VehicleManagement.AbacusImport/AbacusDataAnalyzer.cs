using System.Text;

namespace VehicleManagement.AbacusImport;

public sealed record AbacusDocumentAnalysis(
    string Kind,
    string FileName,
    int ExpectedColumns,
    int TotalRows,
    int StructurallyValidRows,
    int ImportCandidateRows,
    int SkippedBlankCustomerRows,
    int CandidateRowsWithoutVehicle,
    int DuplicateDocumentNumberRows,
    int ConservativeVehicleCandidates,
    int LegacyControlCharacterCount,
    IReadOnlyList<AbacusParseError> Errors);

public sealed record AbacusDataAnalysis(
    string FolderPath,
    AbacusDocumentAnalysis Sales,
    AbacusDocumentAnalysis Maintenance,
    int TotalImportCandidateRows,
    int TotalSkippedBlankCustomerRows,
    int ConservativeVehicleCandidates,
    DateTime AnalyzedAtUtc)
{
    public bool IsStructurallyValid => Sales.Errors.Count == 0 && Maintenance.Errors.Count == 0;
}

public sealed class AbacusDataAnalyzer(AbacusTabParser parser)
{
    public async Task<AbacusDataAnalysis> AnalyzeAsync(
        string folderPath,
        CancellationToken cancellationToken = default)
    {
        var salesTask = parser.ParseAsync(folderPath, AbacusTabSpecifications.Sales, cancellationToken);
        var maintenanceTask = parser.ParseAsync(folderPath, AbacusTabSpecifications.Maintenance, cancellationToken);
        await Task.WhenAll(salesTask, maintenanceTask);

        var allVehicleKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sales = Analyze(salesTask.Result, allVehicleKeys);
        var maintenance = Analyze(maintenanceTask.Result, allVehicleKeys);
        return new AbacusDataAnalysis(
            Path.GetFullPath(folderPath),
            sales,
            maintenance,
            sales.ImportCandidateRows + maintenance.ImportCandidateRows,
            sales.SkippedBlankCustomerRows + maintenance.SkippedBlankCustomerRows,
            allVehicleKeys.Count,
            DateTime.UtcNow);
    }

    private static AbacusDocumentAnalysis Analyze(
        AbacusTabParseResult parseResult,
        ISet<string> allVehicleKeys)
    {
        var specification = parseResult.Specification;
        var skippedBlankCustomer = 0;
        var candidateRowsWithoutVehicle = 0;
        var documentNumbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var vehicleKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in parseResult.Records)
        {
            var customerName = NormalizeText(record.Fields[specification.CustomerNameColumn]);
            if (string.IsNullOrEmpty(customerName))
            {
                skippedBlankCustomer++;
                continue;
            }

            var documentNumber = NormalizeIdentifier(record.Fields[specification.DocumentNumberColumn]);
            if (!string.IsNullOrEmpty(documentNumber))
            {
                documentNumbers[documentNumber] = documentNumbers.GetValueOrDefault(documentNumber) + 1;
            }

            var maker = NormalizeText(record.Fields[specification.MakerColumn]);
            var vehicleName = NormalizeText(record.Fields[specification.VehicleNameColumn]);
            var model = NormalizeText(record.Fields[specification.ModelColumn]);
            var registrationNumber = NormalizeIdentifier(record.Fields[specification.RegistrationNumberColumn]);
            var chassisNumber = NormalizeIdentifier(record.Fields[specification.ChassisNumberColumn]);

            if (string.IsNullOrEmpty(maker) &&
                string.IsNullOrEmpty(vehicleName) &&
                string.IsNullOrEmpty(model) &&
                string.IsNullOrEmpty(registrationNumber) &&
                string.IsNullOrEmpty(chassisNumber))
            {
                candidateRowsWithoutVehicle++;
                continue;
            }

            var key = !string.IsNullOrEmpty(chassisNumber)
                ? $"chassis:{chassisNumber}"
                : !string.IsNullOrEmpty(registrationNumber)
                    ? $"registration:{registrationNumber}"
                    : $"fallback:{customerName}|{maker}|{model}|{vehicleName}";
            vehicleKeys.Add(key);
            allVehicleKeys.Add(key);
        }

        return new AbacusDocumentAnalysis(
            specification.DisplayName,
            specification.FileName,
            specification.ExpectedColumns,
            parseResult.Records.Count + parseResult.Errors.Count(error => error.RowNumber.HasValue),
            parseResult.Records.Count,
            parseResult.Records.Count - skippedBlankCustomer,
            skippedBlankCustomer,
            candidateRowsWithoutVehicle,
            documentNumbers.Values.Sum(count => Math.Max(0, count - 1)),
            vehicleKeys.Count,
            parseResult.LegacyControlCharacterCount,
            parseResult.Errors);
    }

    private static string NormalizeText(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWasSpace = false;
        foreach (var character in value.Trim())
        {
            var normalized = character is '\u0004' or '\u000B' or '\u001D' ? ' ' : character;
            if (char.IsWhiteSpace(normalized) || normalized == '　')
            {
                if (!previousWasSpace)
                {
                    builder.Append(' ');
                    previousWasSpace = true;
                }
            }
            else
            {
                builder.Append(normalized);
                previousWasSpace = false;
            }
        }

        return builder.ToString();
    }

    private static string NormalizeIdentifier(string value)
    {
        var normalized = NormalizeText(value).Normalize(NormalizationForm.FormKC);
        return string.Concat(normalized.Where(character => !char.IsWhiteSpace(character) && character != '-'))
            .ToUpperInvariant();
    }
}
