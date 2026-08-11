using System.Text.Encodings.Web;
using System.Text.Json;
using VehicleManagement.AbacusImport;

if (args.Length == 2 && args[0].Equals("--legacy-export", StringComparison.OrdinalIgnoreCase))
{
    var result = await new AbacusLegacyExportReader().ReadAsync(args[1]);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        result.FolderPath,
        result.IsValid,
        Files = result.Files.Select(file => new
        {
            file.FileName,
            file.Kind,
            file.ExpectedColumns,
            file.TotalRows,
            file.ValidRows,
            file.BlankRequiredRows,
            file.InvalidDateRows,
            file.Errors,
            Columns = file.FirstRowSamples,
        }),
        result.Errors,
    }, new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    }));
    return result.IsValid ? 0 : 1;
}

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: VehicleManagement.AbacusImport.Cli <ABACUS folder>");
    Console.Error.WriteLine("       VehicleManagement.AbacusImport.Cli --legacy-export <CSV folder>");
    return 2;
}

var parser = new AbacusTabParser();
var analysis = await new AbacusDataAnalyzer(parser).AnalyzeAsync(args[0]);
var linkage = await new AbacusLinkagePlanner(parser).PlanAsync(args[0]);
var sanitizedLinkage = new
{
    linkage.ImportCandidateDocuments,
    linkage.SkippedBlankCustomerDocuments,
    linkage.CustomerCandidates,
    linkage.VehicleCandidates,
    linkage.CustomersWithMultipleVehicles,
    linkage.SameNameConflictGroups,
    linkage.SameNameConflictDocuments,
    linkage.VehicleIdentifierConflictGroups,
    linkage.VehiclesLinkedToMultipleCustomers,
    linkage.DocumentsWithoutVehicleInformation,
    linkage.DocumentsWithVehicleButWithoutStrongIdentifier,
    linkage.Errors,
    linkage.IsValid,
};
Console.WriteLine(JsonSerializer.Serialize(new { Analysis = analysis, Linkage = sanitizedLinkage }, new JsonSerializerOptions
{
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    WriteIndented = true,
}));
return analysis.IsStructurallyValid && linkage.IsValid ? 0 : 1;
