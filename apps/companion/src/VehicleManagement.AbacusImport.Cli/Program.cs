using System.Text.Encodings.Web;
using System.Text.Json;
using VehicleManagement.AbacusImport;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: VehicleManagement.AbacusImport.Cli <ABACUS folder>");
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
