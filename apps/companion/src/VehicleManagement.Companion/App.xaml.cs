using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
            var menuWithoutAbacus = await session.InspectAbacusMenuAsync();
            if (menuWithoutAbacus.Status != "abacus-not-running" ||
                menuWithoutAbacus.IsRunning ||
                menuWithoutAbacus.MenuItems is { Count: > 0 })
            {
                return 6;
            }
            var nativeWindowsWithoutAbacus = await session.InspectAbacusNativeWindowsAsync();
            if (nativeWindowsWithoutAbacus.Status != "abacus-not-running" ||
                nativeWindowsWithoutAbacus.IsRunning ||
                nativeWindowsWithoutAbacus.NativeWindows is { Count: > 0 })
            {
                return 7;
            }
            await session.StopAsync();
            if (session.Snapshot.State != LegacyHostState.Stopped)
            {
                return 2;
            }

            var externallyExited = new AbacusRuntimeSnapshot(
                false,
                "abacus-exited",
                "self-test",
                ProcessId: 1234);
            var recognizedRunning = new AbacusRuntimeSnapshot(
                true,
                "ui-automation-ready",
                "self-test",
                ProcessId: 1234);
            if (externallyExited.IsRunning || !recognizedRunning.IsRunning)
            {
                return 4;
            }

            var rejectedInvalidWindow = false;
            try
            {
                _ = new AbacusWindowCaptureService().Capture(0, Environment.ProcessId);
            }
            catch (InvalidOperationException)
            {
                rejectedInvalidWindow = true;
            }
            if (!rejectedInvalidWindow)
            {
                return 5;
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
            await File.WriteAllTextAsync(Path.Combine(source, "abx-cs-hb.ucs"), "self-test:active-runtime");
            await File.WriteAllTextAsync(Path.Combine(source, "sbx-cs-hb.ucs"), "self-test:standby-runtime");

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

            var workspaceService = new AbacusWorkspaceService(inspector);
            var workspace = await workspaceService.CreateAsync(before, destination);
            var verifiedWorkspace = await workspaceService.VerifyExistingAsync(workspace.WorkspacePath);
            var testImage = BitmapSource.Create(
                2,
                2,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                new byte[]
                {
                    0, 0, 255, 255, 0, 255, 0, 255,
                    255, 0, 0, 255, 255, 255, 255, 255,
                },
                8);
            var imageExport = await new AbacusClipboardImageExporter().ExportAsync(testImage, destination);
            var cropPixels = new byte[140 * 120 * 4];
            for (var y = 0; y < 100; y++)
            {
                for (var x = 0; x < 100; x++)
                {
                    var offset = (y * 140 + x) * 4;
                    var shade = x is >= 10 and <= 89 && y is >= 10 and <= 89 ? (byte)100 : (byte)255;
                    cropPixels[offset] = shade;
                    cropPixels[offset + 1] = shade;
                    cropPixels[offset + 2] = shade;
                    cropPixels[offset + 3] = 255;
                }
            }
            var cropSource = BitmapSource.Create(
                140,
                120,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                cropPixels,
                140 * 4);
            var cropResult = new AbacusCaptureCropper().Crop(cropSource);
            var toolbarCropPixels = Enumerable.Repeat((byte)255, 160 * 140 * 4).ToArray();
            for (var y = 10; y <= 119; y++)
            {
                for (var x = 10; x <= 129; x++)
                {
                    var isToolbar = y <= 29 && x <= 80;
                    var isDocument = y >= 45;
                    var isConnector = x is 10 or 70 or 129;
                    if (isToolbar || isDocument || isConnector)
                    {
                        var offset = (y * 160 + x) * 4;
                        toolbarCropPixels[offset] = 100;
                        toolbarCropPixels[offset + 1] = 100;
                        toolbarCropPixels[offset + 2] = 100;
                    }
                }
            }
            for (var y = 45; y <= 100; y++)
            {
                for (var x = 145; x <= 148; x++)
                {
                    var offset = (y * 160 + x) * 4;
                    toolbarCropPixels[offset] = 100;
                    toolbarCropPixels[offset + 1] = 100;
                    toolbarCropPixels[offset + 2] = 100;
                }
            }
            var toolbarCropSource = BitmapSource.Create(
                160,
                140,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                toolbarCropPixels,
                160 * 4);
            var toolbarCropResult = new AbacusCaptureCropper().Crop(toolbarCropSource);
            var mutablePath = Path.Combine(workspace.WorkspacePath, "abx-cs-mn.ucs");
            var mutableBytes = await File.ReadAllBytesAsync(mutablePath);
            mutableBytes[^1] ^= 0x01;
            await File.WriteAllBytesAsync(mutablePath, mutableBytes);
            var secondMutablePath = Path.Combine(workspace.WorkspacePath, "abx-cs-hb.ucs");
            var secondMutableBytes = await File.ReadAllBytesAsync(secondMutablePath);
            secondMutableBytes[^1] ^= 0x01;
            await File.WriteAllBytesAsync(secondMutablePath, secondMutableBytes);
            var verifiedUsedWorkspace = await workspaceService.VerifyExistingAsync(workspace.WorkspacePath);
            var forbiddenPath = Path.Combine(workspace.WorkspacePath, "sbx-cs-hb.ucs");
            var forbiddenBytes = await File.ReadAllBytesAsync(forbiddenPath);
            forbiddenBytes[^1] ^= 0x01;
            await File.WriteAllBytesAsync(forbiddenPath, forbiddenBytes);
            var rejectedUnallowedChange = false;
            try
            {
                await workspaceService.VerifyExistingAsync(workspace.WorkspacePath);
            }
            catch (InvalidDataException)
            {
                rejectedUnallowedChange = true;
            }
            var parser = new AbacusTabParser();
            var analysis = await new AbacusDataAnalyzer(parser).AnalyzeAsync(source);
            var linkage = await new AbacusLinkagePlanner(parser).PlanAsync(source);
            var packageParent = Path.Combine(testRoot, "migration-packages");
            Directory.CreateDirectory(packageParent);
            var migrationPreview = await new AbacusMigrationPreviewStore(
                new AbacusDataAnalyzer(new AbacusTabParser()),
                new AbacusLinkagePlanner(new AbacusTabParser()))
                .CreateAsync(source, packageParent);
            var migrationManifestText = await File.ReadAllTextAsync(migrationPreview.ManifestPath);
            var legacyExportFolder = Path.Combine(testRoot, "legacy-export");
            Directory.CreateDirectory(legacyExportFolder);
            var salesExportFields = Enumerable.Repeat(string.Empty, 33).ToArray();
            salesExportFields[0] = "2026/08/11";
            salesExportFields[1] = "S-1";
            salesExportFields[5] = "顧客A";
            var maintenanceExportFields = Enumerable.Repeat(string.Empty, 29).ToArray();
            maintenanceExportFields[0] = "2026/08/11";
            maintenanceExportFields[1] = "M-1";
            maintenanceExportFields[4] = "顧客A";
            maintenanceExportFields[28] = "既知\u0004区切り";
            var vehicleExportFields = Enumerable.Repeat(string.Empty, 23).ToArray();
            vehicleExportFields[0] = "顧客A";
            vehicleExportFields[12] = "車両A";
            await File.WriteAllTextAsync(Path.Combine(legacyExportFolder, "hanbai.csv"), string.Join(',', salesExportFields), shiftJis);
            await File.WriteAllTextAsync(Path.Combine(legacyExportFolder, "seibi.csv"), string.Join(',', maintenanceExportFields), shiftJis);
            await File.WriteAllTextAsync(Path.Combine(legacyExportFolder, "syaryou.csv"), string.Join(',', vehicleExportFields), shiftJis);
            await File.WriteAllTextAsync(Path.Combine(legacyExportFolder, "syaryou2.csv"), string.Join(',', vehicleExportFields), shiftJis);
            var legacyExportAnalysis = await new AbacusLegacyExportInspector().AnalyzeAsync(legacyExportFolder);
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
                verifiedWorkspace.WorkspaceReport.FolderFingerprint == before.FolderFingerprint &&
                verifiedWorkspace.WorkspacePath == workspace.WorkspacePath &&
                verifiedWorkspace.AllowedRuntimeChanges.Count == 0 &&
                File.Exists(imageExport.FilePath) &&
                imageExport.FileSize > 8 &&
                imageExport.PixelWidth == 2 &&
                imageExport.PixelHeight == 2 &&
                imageExport.Sha256.Length == 64 &&
                cropResult.WasCropped &&
                cropResult.Image.PixelWidth == 88 &&
                cropResult.Image.PixelHeight == 88 &&
                toolbarCropResult.WasCropped &&
                toolbarCropResult.Image.PixelWidth == 132 &&
                toolbarCropResult.Image.PixelHeight == 96 &&
                verifiedUsedWorkspace.AllowedRuntimeChanges.Count == 2 &&
                rejectedUnallowedChange &&
                analysis.IsStructurallyValid &&
                analysis.TotalImportCandidateRows == 7 &&
                analysis.TotalSkippedBlankCustomerRows == 2 &&
                linkage.IsValid &&
                linkage.CustomerCandidates == 5 &&
                linkage.VehicleCandidates == 6 &&
                linkage.CustomersWithMultipleVehicles == 1 &&
                linkage.SameNameConflictGroups == 1 &&
                linkage.VehicleIdentifierConflictGroups == 1 &&
                Directory.GetFiles(migrationPreview.PackagePath).Length == 1 &&
                migrationPreview.ManifestSha256.Length == 64 &&
                migrationPreview.CustomerCandidates == linkage.CustomerCandidates &&
                migrationPreview.VehicleCandidates == linkage.VehicleCandidates &&
                migrationPreview.DocumentCandidates == linkage.ImportCandidateDocuments &&
                migrationManifestText.Contains("\"status\": \"preview-only\"", StringComparison.OrdinalIgnoreCase) &&
                migrationManifestText.Contains("\"dataFiles\": []", StringComparison.OrdinalIgnoreCase) &&
                migrationManifestText.Contains("\"imageFiles\": []", StringComparison.OrdinalIgnoreCase) &&
                legacyExportAnalysis.IsValid &&
                legacyExportAnalysis.SalesRows == 1 &&
                legacyExportAnalysis.MaintenanceRows == 1 &&
                legacyExportAnalysis.VehicleRows == 2 &&
                legacyExportAnalysis.VehicleFileCount == 2 &&
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
