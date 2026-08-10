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
            var linkage = await new AbacusLinkagePlanner(parser).PlanAsync(source);
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
                analysis.TotalImportCandidateRows == 7 &&
                analysis.TotalSkippedBlankCustomerRows == 2 &&
                linkage.IsValid &&
                linkage.CustomerCandidates == 5 &&
                linkage.VehicleCandidates == 6 &&
                linkage.CustomersWithMultipleVehicles == 1 &&
                linkage.SameNameConflictGroups == 1 &&
                linkage.VehicleIdentifierConflictGroups == 1 &&
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
        valid[specification.AddressColumn] = "住所A";
        valid[specification.PhoneColumns[0]] = "090-1111-1111";
        valid[specification.VehicleNameColumn] = "車両A";
        valid[specification.ModelColumn] = "MODEL-A";
        valid[specification.MakerColumn] = "メーカーA";
        valid[specification.RegistrationNumberColumn] = "大阪100あ1";
        valid[specification.ChassisNumberColumn] = "ABC-123";
        valid[specification.DocumentNumberColumn] = specification == AbacusTabSpecifications.Sales ? "S-1" : "M-1";
        valid[1] = "住所\u000B続き";

        var rows = new List<string[]> { valid };
        var blankCustomer = (string[])valid.Clone();
        blankCustomer[specification.CustomerNameColumn] = string.Empty;
        rows.Add(blankCustomer);

        if (specification == AbacusTabSpecifications.Sales)
        {
            rows.Add(Clone(valid, "顧客A", "住所A", "090-1111-1111", "大阪100あ2", "DEF-456", "S-2"));
            rows.Add(Clone(valid, "同姓同名", "住所B", "090-2222-2222", "大阪100あ3", "SAME-1", "S-3"));
            rows.Add(Clone(valid, "同姓同名", "住所C", "090-3333-3333", "大阪100あ4", "SAME-2", "S-4"));
            rows.Add(Clone(valid, "顧客B", "住所D", "090-4444-4444", "大阪100あ9", "CONFLICT-1", "S-5"));
            rows.Add(Clone(valid, "顧客C", "住所E", "090-5555-5555", "大阪100あ9", "CONFLICT-2", "S-6"));
        }

        var contents = string.Concat(rows.Select(row => $"{string.Join('\t', row)}\r\n"));
        await File.WriteAllTextAsync(Path.Combine(folder, specification.FileName), contents, encoding);

        string[] Clone(
            string[] source,
            string customer,
            string address,
            string phone,
            string registration,
            string chassis,
            string document)
        {
            var clone = (string[])source.Clone();
            clone[specification.CustomerNameColumn] = customer;
            clone[specification.AddressColumn] = address;
            clone[specification.PhoneColumns[0]] = phone;
            clone[specification.RegistrationNumberColumn] = registration;
            clone[specification.ChassisNumberColumn] = chassis;
            clone[specification.DocumentNumberColumn] = document;
            return clone;
        }
    }
}
