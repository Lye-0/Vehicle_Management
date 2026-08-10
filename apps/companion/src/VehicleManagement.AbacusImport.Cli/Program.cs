using System.Text.Encodings.Web;
using System.Text.Json;
using VehicleManagement.AbacusImport;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: VehicleManagement.AbacusImport.Cli <ABACUS folder>");
    return 2;
}

var analysis = await new AbacusDataAnalyzer(new AbacusTabParser()).AnalyzeAsync(args[0]);
Console.WriteLine(JsonSerializer.Serialize(analysis, new JsonSerializerOptions
{
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    WriteIndented = true,
}));
return analysis.IsStructurallyValid ? 0 : 1;
