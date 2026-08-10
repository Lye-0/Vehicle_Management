using System.IO;
using System.Text;
using System.Windows;
using VehicleManagement.AbacusImport;
using VehicleManagement.Companion.Services;

namespace VehicleManagement.Companion;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var exitCode = await RunSelfTestAsync();
            Shutdown(exitCode);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private static async Task<int> RunSelfTestAsync()
    {
        await using var session = new LegacyHostSession();
        try
        {
            await session.StartAsync();
            await session.PingAsync();
            await session.StopAsync();
            if (session.Snapshot.State != LegacyHostState.Stopped)
            {
                return 2;
            }

            return await RunFolderSelfTestAsync() ? 0 : 3;
        }
        catch
        {
            return 1;
        }
    }

    private static async Task<bool> RunFolderSelfTestAsync()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"VehicleManagement.Companion.SelfTest.{Guid.NewGuid():N}");
        var source = Path.Combine(testRoot, "source");
        var destination = Path.Combine(testRoot, "destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);

        try
        {
            File.Copy(Environment.ProcessPath!, Path.Combine(source, AbacusConstants.ExecutableFileName));
            foreach (var fileName in AbacusConstants.RequiredFiles.Where(name => name != AbacusConstants.ExecutableFileName))
            {
                await File.WriteAllTextAsync(Path.Combine(source, fileName), $"self-test:{fileName}");
            }

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var shiftJis = Encoding.GetEncoding(932);
            await WriteTabSelfTestAsync(source, AbacusTabSpecifications.Sales, shiftJis);
            await WriteTabSelfTestAsync(source, AbacusTabSpecifications.Maintenance, shiftJis);

            var inspector = new AbacusFolderInspector();
            var before = await inspector.InspectAsync(source);
            if (!before.IsValid)
            {
                return false;
            }

            var workspace = await new AbacusWorkspaceService(inspector).CreateAsync(before, destination);
            var parser = new AbacusTabParser();
            var analysis = await new AbacusDataAnalyzer(parser).AnalyzeAsync(source);
            var invalidFolder = Path.Combine(testRoot, "invalid");
            Directory.CreateDirectory(invalidFolder);
            await File.WriteAllTextAsync(
                Path.Combine(invalidFolder, AbacusTabSpecifications.Sales.FileName),
                "列不足\t列不足\r\n",
                shiftJis);
            await File.WriteAllBytesAsync(
                Path.Combine(invalidFolder, AbacusTabSpecifications.Maintenance.FileName),
                [0x81]);
            var invalidSales = await parser.ParseAsync(invalidFolder, AbacusTabSpecifications.Sales);
            var invalidMaintenance = await parser.ParseAsync(invalidFolder, AbacusTabSpecifications.Maintenance);
            return workspace.WorkspaceReport.FolderFingerprint == before.FolderFingerprint &&
                workspace.SourceAfterCopyReport.FolderFingerprint == before.FolderFingerprint &&
                File.Exists(workspace.ManifestPath) &&
                analysis.IsStructurallyValid &&
                analysis.TotalImportCandidateRows == 2 &&
                analysis.TotalSkippedBlankCustomerRows == 2 &&
                analysis.ConservativeVehicleCandidates == 1 &&
                !invalidSales.IsValid &&
                !invalidMaintenance.IsValid;
        }
        finally
        {
            if (Directory.Exists(testRoot) &&
                testRoot.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static async Task WriteTabSelfTestAsync(
        string folder,
        AbacusTabSpecification specification,
        Encoding encoding)
    {
        var valid = new string[specification.ExpectedColumns];
        Array.Fill(valid, string.Empty);
        valid[specification.CustomerNameColumn] = "顧客A";
        valid[specification.VehicleNameColumn] = "車両A";
        valid[specification.ModelColumn] = "MODEL-A";
        valid[specification.MakerColumn] = "メーカーA";
        valid[specification.RegistrationNumberColumn] = "大阪100あ1";
        valid[specification.ChassisNumberColumn] = "ABC-123";
        valid[specification.DocumentNumberColumn] = specification == AbacusTabSpecifications.Sales ? "S-1" : "M-1";
        valid[1] = "住所\u000B続き";

        var blankCustomer = (string[])valid.Clone();
        blankCustomer[specification.CustomerNameColumn] = string.Empty;
        var contents = $"{string.Join('\t', valid)}\r\n{string.Join('\t', blankCustomer)}\r\n";
        await File.WriteAllTextAsync(Path.Combine(folder, specification.FileName), contents, encoding);
    }
}
