using System.Text.Encodings.Web;
using System.Text;
using System.Text.Json;
using VehicleManagement.AbacusImport;

if ((args.Length == 4 || args.Length == 5) && args[0].Equals("--fp5-map", StringComparison.OrdinalIgnoreCase))
{
    var result = await new AbacusFp5VehicleImageMapper().MapAsync(
        args[1],
        args[2],
        args[3],
        allowPartialScope: args.Length == 5 && args[4].Equals("--partial", StringComparison.OrdinalIgnoreCase));
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        result.OutputFolderPath,
        result.ReportPath,
        result.InternalVehicleRecordCount,
        result.VehicleCsvRowCount,
        result.JpegImageCount,
        result.GifPlaceholderCount,
        result.MatchedImageCount,
        result.NoImageCount,
        result.ReviewCount,
        result.UnmatchedCount,
        result.MultipleCandidateCount,
        result.UnknownImageReferenceCount,
        result.DuplicateImageReferenceCount,
        result.DuplicateImageSha256Count,
        result.UnreferencedImageCount,
        result.IsValid,
        result.IsFullyMatched,
        result.InScopeVehicleRecordCount,
        result.OutOfScopeRecordCount,
    }, new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    }));
    return result.IsValid ? 0 : 1;
}

if (args.Length == 3 && args[0].Equals("--fp5-images", StringComparison.OrdinalIgnoreCase))
{
    var result = await new AbacusFp5ImageRestorer().RestoreAsync(args[1], args[2]);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        result.OutputFolderPath,
        result.ReportPath,
        result.SourceFileSize,
        result.SourceSha256,
        result.SectorCount,
        result.BlockChainLevels,
        result.DataBlockCount,
        result.ImageNodeCount,
        result.RestoredImageCount,
        result.LengthCheckMatchCount,
        result.DecodeSuccessCount,
        result.UniqueImageSha256Count,
        result.IsValid,
    }, new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    }));
    return result.IsValid ? 0 : 1;
}

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

if (args.Length == 4 && args[0].Equals("--legacy-subset", StringComparison.OrdinalIgnoreCase))
{
    var reader = new AbacusLegacyExportReader();
    var input = await reader.ReadAsync(args[1]);
    if (!input.IsValid)
    {
        Console.Error.WriteLine("CSV診断に失敗しています。");
        return 1;
    }

    var options = new AbacusLegacyExportSubsetStore(reader).GetCustomerOptions(input);
    var option = options.FirstOrDefault(item =>
        string.Equals(item.Name, args[2].Normalize(NormalizationForm.FormKC).Trim(), StringComparison.Ordinal));
    if (option is null)
    {
        Console.Error.WriteLine("指定した顧客名が見つかりません。--legacy-customers相当の画面で候補を確認してください。");
        return 1;
    }

    var result = await new AbacusLegacyExportSubsetStore(reader).CreateAsync(args[1], args[3], option.Key);
    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    }));
    return 0;
}

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: VehicleManagement.AbacusImport.Cli <ABACUS folder>");
    Console.Error.WriteLine("       VehicleManagement.AbacusImport.Cli --legacy-export <CSV folder>");
    Console.Error.WriteLine("       VehicleManagement.AbacusImport.Cli --legacy-subset <CSV folder> <customer name> <destination parent>");
    Console.Error.WriteLine("       VehicleManagement.AbacusImport.Cli --fp5-images <FP5/UCS file> <output parent folder>");
    Console.Error.WriteLine("       VehicleManagement.AbacusImport.Cli --fp5-map <FP5/UCS file> <CSV folder> <output parent folder>");
    Console.Error.WriteLine("       VehicleManagement.AbacusImport.Cli --fp5-map <FP5/UCS file> <CSV folder> <output parent folder> --partial");
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
