using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
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
            var automationWithoutAbacus = await session.InspectAbacusAutomationAsync();
            if (automationWithoutAbacus.Status != "abacus-not-running" || automationWithoutAbacus.IsRunning)
            {
                return 8;
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

            if (AbacusScreenStateClassifier.ClassifyRatios(0.068, 0.67, 0.67) is not AbacusScreenVisualState.MainMenu ||
                AbacusScreenStateClassifier.ClassifyRatios(0.005, 0.67, 0.69) is not AbacusScreenVisualState.VehicleList ||
                AbacusScreenStateClassifier.ClassifyRatios(0.000, 0.67, 0.72) is not AbacusScreenVisualState.ExpandedImage)
            {
                return 9;
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
        var outputPackageDestination = Path.Combine(testRoot, "output-package-parent");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        Directory.CreateDirectory(outputPackageDestination);

        try
        {
            File.Copy(Environment.ProcessPath!, Path.Combine(source, AbacusConstants.ExecutableFileName));
            foreach (var fileName in AbacusConstants.RequiredFiles.Where(name => name != AbacusConstants.ExecutableFileName))
            {
                await File.WriteAllTextAsync(Path.Combine(source, fileName), $"self-test:{fileName}");
            }
            var fakeFp5Header = new byte[1024];
            var fakeFp5HeaderText = Encoding.ASCII.GetBytes("Copyright 1984-1999 FileMaker, Inc. Pro 5.0");
            fakeFp5HeaderText.CopyTo(fakeFp5Header, 480);
            var fakeBitmap = BitmapSource.Create(
                3,
                2,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                new byte[]
                {
                    0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255,
                    255, 255, 255, 255, 100, 100, 100, 255, 50, 50, 50, 255,
                },
                3 * 4);
            using var fakeJpegStream = new MemoryStream();
            var fakeJpegEncoder = new JpegBitmapEncoder();
            fakeJpegEncoder.Frames.Add(BitmapFrame.Create(fakeBitmap));
            fakeJpegEncoder.Save(fakeJpegStream);
            var fakeJpeg = fakeJpegStream.ToArray();
            await WriteFp5ImageSelfTestAsync(Path.Combine(source, "abx-cs-sk.ucs"), fakeJpeg);
            await File.WriteAllBytesAsync(
                Path.Combine(source, "BackUp-5.fp5"),
                [.. fakeFp5Header, .. fakeJpeg]);
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
            var outputPackageStore = new AbacusImportOutputPackageStore();
            var outputPackageSession = await outputPackageStore.CreateAsync(
                outputPackageDestination,
                source,
                before.FolderFingerprint,
                "fp5-vehicle-record");
            var fp5RestorationParent = Path.Combine(testRoot, "fp5-restoration");
            Directory.CreateDirectory(fp5RestorationParent);
            var fp5Restoration = await new AbacusFp5ImageRestorer().RestoreAsync(
                Path.Combine(workspace.WorkspacePath, "abx-cs-sk.ucs"),
                fp5RestorationParent);
            var fp5RestorationReportText = await File.ReadAllTextAsync(fp5Restoration.ReportPath);
            var outputRootManifestText = await File.ReadAllTextAsync(outputPackageSession.RootManifestPath);
            var outputReadyManifestText = await File.ReadAllTextAsync(outputPackageSession.ReadyManifestPath);
            var outputPackageStructureValid =
                Directory.Exists(outputPackageSession.WorkAbacusCopyPath) &&
                Directory.EnumerateFiles(outputPackageSession.WorkAbacusCopyPath, "*", SearchOption.AllDirectories).Any() &&
                Directory.Exists(outputPackageSession.WorkIntermediatePath) &&
                Directory.Exists(outputPackageSession.WorkCheckpointsPath) &&
                Directory.Exists(outputPackageSession.WorkLogsPath) &&
                Directory.Exists(Path.Combine(outputPackageSession.ReadyPath, "data")) &&
                Directory.Exists(Path.Combine(outputPackageSession.ReadyPath, "mappings")) &&
                Directory.Exists(Path.Combine(outputPackageSession.ReadyPath, "images")) &&
                Directory.Exists(Path.Combine(outputPackageSession.ReadyPath, "reports")) &&
                outputRootManifestText.Contains("\"status\": \"in-progress\"", StringComparison.Ordinal) &&
                outputReadyManifestText.Contains("\"status\": \"pending\"", StringComparison.Ordinal) &&
                !string.Equals(Path.GetFullPath(outputPackageSession.RootPath), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase);
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
            var imageLinkManifest = await new AbacusImageLinkManifestStore().CreateAsync(
                imageExport,
                source,
                workspace.WorkspacePath,
                before.FolderFingerprint,
                workspace.WorkspaceReport.FolderFingerprint,
                "chassis-1",
                "大阪 537 む 16",
                "顧客A");
            var rejectedImageLinkWithoutIdentifier = false;
            try
            {
                await new AbacusImageLinkManifestStore().CreateAsync(
                    imageExport,
                    source,
                    workspace.WorkspacePath,
                    before.FolderFingerprint,
                    workspace.WorkspaceReport.FolderFingerprint,
                    string.Empty,
                    string.Empty,
                    string.Empty);
            }
            catch (InvalidDataException)
            {
                rejectedImageLinkWithoutIdentifier = true;
            }
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
            var fp5Inspection = await new AbacusFp5Inspector().InspectAsync(source);
            var fp5ExportDestination = Path.Combine(testRoot, "fp5-export");
            Directory.CreateDirectory(fp5ExportDestination);
            var fp5Export = await new AbacusFp5CandidateExporter().ExportAsync(
                source,
                fp5Inspection.Candidates[0],
                fp5ExportDestination);
            var rejectedInternalFp5Export = false;
            try
            {
                await new AbacusFp5CandidateExporter().ExportAsync(
                    source,
                    fp5Inspection.Candidates[0],
                    source);
            }
            catch (InvalidOperationException)
            {
                rejectedInternalFp5Export = true;
            }
            var malformedFp5Source = Path.Combine(testRoot, "malformed-fp5-source");
            var malformedFp5Destination = Path.Combine(testRoot, "malformed-fp5-destination");
            Directory.CreateDirectory(malformedFp5Source);
            Directory.CreateDirectory(malformedFp5Destination);
            var malformedJpeg = new byte[fakeJpeg.Length + 6];
            Buffer.BlockCopy(fakeJpeg, 0, malformedJpeg, 0, fakeJpeg.Length - 2);
            malformedJpeg[^6] = 0xFF;
            malformedJpeg[^5] = 0xC0;
            malformedJpeg[^4] = 0x00;
            malformedJpeg[^3] = 0x00;
            malformedJpeg[^2] = 0xFF;
            malformedJpeg[^1] = 0xD9;
            await File.WriteAllBytesAsync(
                Path.Combine(malformedFp5Source, "BackUp-5.fp5"),
                [.. fakeFp5Header, .. malformedJpeg]);
            var malformedFp5Inspection = await new AbacusFp5Inspector().InspectAsync(malformedFp5Source);
            var rejectedMalformedFp5Export = false;
            try
            {
                await new AbacusFp5CandidateExporter().ExportAsync(
                    malformedFp5Source,
                    malformedFp5Inspection.Candidates[0],
                    malformedFp5Destination);
            }
            catch (InvalidDataException)
            {
                rejectedMalformedFp5Export = true;
            }
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
            salesExportFields[15] = "車両A";
            salesExportFields[21] = "CHASSIS1";
            salesExportFields[22] = "大阪537む16";
            var maintenanceExportFields = Enumerable.Repeat(string.Empty, 29).ToArray();
            maintenanceExportFields[0] = "2026/08/11";
            maintenanceExportFields[1] = "M-1";
            maintenanceExportFields[4] = "顧客A";
            maintenanceExportFields[13] = "車両A";
            maintenanceExportFields[19] = "CHASSIS1";
            maintenanceExportFields[20] = "大阪537む16";
            maintenanceExportFields[28] = "既知\u0004区切り";
            maintenanceExportFields[25] = "2026/08/12";
            maintenanceExportFields[26] = "2026/08/13";
            var vehicleExportFields = Enumerable.Repeat(string.Empty, 23).ToArray();
            vehicleExportFields[0] = "顧客A";
            vehicleExportFields[11] = "メーカーA";
            vehicleExportFields[12] = "車両A";
            vehicleExportFields[13] = "H6";
            vehicleExportFields[14] = "1000";
            vehicleExportFields[15] = "5MT";
            vehicleExportFields[16] = "緑";
            vehicleExportFields[17] = "MODEL-A";
            vehicleExportFields[18] = "CHASSIS1";
            vehicleExportFields[19] = "大阪537む16";
            vehicleExportFields[20] = "10300";
            vehicleExportFields[21] = "H26.06.07";
            vehicleExportFields[22] = "有";
            await File.WriteAllTextAsync(Path.Combine(legacyExportFolder, "hanbai.csv"), string.Join(',', salesExportFields), shiftJis);
            await File.WriteAllTextAsync(Path.Combine(legacyExportFolder, "seibi.csv"), string.Join(',', maintenanceExportFields), shiftJis);
            await File.WriteAllTextAsync(Path.Combine(legacyExportFolder, "syaryou.csv"), string.Join(',', vehicleExportFields), shiftJis);
            await File.WriteAllTextAsync(Path.Combine(legacyExportFolder, "syaryou2.csv"), string.Join(',', vehicleExportFields), shiftJis);
            var fp5MappingParent = Path.Combine(outputPackageSession.WorkIntermediatePath, "fp5-mapping");
            var fp5MappingExportFolder = Path.Combine(testRoot, "fp5-mapping-export");
            Directory.CreateDirectory(fp5MappingParent);
            Directory.CreateDirectory(fp5MappingExportFolder);
            File.Copy(
                Path.Combine(legacyExportFolder, "syaryou.csv"),
                Path.Combine(fp5MappingExportFolder, "syaryou.csv"));
            var fp5Mapping = await new AbacusFp5VehicleImageMapper().MapAsync(
                Path.Combine(workspace.WorkspacePath, "abx-cs-sk.ucs"),
                fp5MappingExportFolder,
                fp5MappingParent);
            var fp5MappingReportText = await File.ReadAllTextAsync(fp5Mapping.ReportPath);
            var legacyExportAnalysis = await new AbacusLegacyExportInspector().AnalyzeAsync(legacyExportFolder);
            var legacyPreviewSource = Path.Combine(testRoot, "legacy-preview-source");
            Directory.CreateDirectory(legacyPreviewSource);
            File.Copy(Path.Combine(legacyExportFolder, "hanbai.csv"), Path.Combine(legacyPreviewSource, "hanbai.csv"));
            File.Copy(Path.Combine(legacyExportFolder, "seibi.csv"), Path.Combine(legacyPreviewSource, "seibi.csv"));
            File.Copy(Path.Combine(legacyExportFolder, "syaryou.csv"), Path.Combine(legacyPreviewSource, "syaryou.csv"));
            var legacyPreviewParent = Path.Combine(testRoot, "legacy-preview-packages");
            Directory.CreateDirectory(legacyPreviewParent);
            var legacyPreview = await new AbacusLegacyExportPreviewStore().CreateAsync(
                legacyPreviewSource,
                legacyPreviewParent);
            var legacyPreviewManifestText = await File.ReadAllTextAsync(legacyPreview.ManifestPath);
            var legacyPreviewCustomersText = await File.ReadAllTextAsync(Path.Combine(legacyPreview.PackagePath, "customers.csv"));
            var legacyPreviewVehiclesText = await File.ReadAllTextAsync(Path.Combine(legacyPreview.PackagePath, "vehicles.csv"));
            var legacyPreviewSalesText = await File.ReadAllTextAsync(Path.Combine(legacyPreview.PackagePath, "sales.csv"));
            var legacyPreviewMaintenanceText = await File.ReadAllTextAsync(Path.Combine(legacyPreview.PackagePath, "maintenance.csv"));
            var legacyPackageRead = await new AbacusLegacyExportPreviewPackageReader().ReadAsync(legacyPreview.PackagePath);
            var legacyCandidateGraph = await new AbacusLegacyExportCandidateGraphService().BuildAsync(legacyPackageRead);
            var gate14FinalPackageParent = Path.Combine(testRoot, "gate14-final-packages");
            Directory.CreateDirectory(gate14FinalPackageParent);
            // Gate 14のFP5/CSV側に全角空白が残っていても、候補グラフ側の
            // 正規化済み識別子へ再照合できることを検証します。
            var gate14MappingWithCompatibilitySpacing = fp5Mapping with
            {
                Mappings = fp5Mapping.Mappings
                    .Select(mapping => mapping with
                    {
                        RegistrationNumber = mapping.RegistrationNumber?.Replace(' ', '\u3000')
                    })
                    .ToArray()
            };
            var gate14FinalizationSnapshot = new AbacusLegacyGraphFinalizationSnapshot(
                legacyCandidateGraph.Customers
                    .Select(customer => new AbacusLegacyGraphFinalCustomerGroup(
                        $"self-test-{customer.CustomerId}",
                        "self-test",
                        true,
                        [customer.CustomerId],
                        customer.CustomerId,
                        customer.CustomerNumber,
                        customer.CustomerName,
                        customer.NameKana,
                        customer.PhoneNumber,
                        customer.EmailAddress,
                        customer.PostalCode,
                        customer.Address,
                        customer.Memo))
                    .ToArray(),
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, string>(StringComparer.Ordinal),
                Array.Empty<string>(),
                true);
            var gate14FinalPackage = await new AbacusLegacyGraphFinalPackageStore().CreateAsync(
                legacyCandidateGraph,
                gate14FinalizationSnapshot,
                gate14FinalPackageParent,
                gate14MappingWithCompatibilitySpacing);
            var gate14ReadyPackage = await outputPackageStore.CompleteAsync(
                outputPackageSession,
                gate14FinalPackage,
                gate14MappingWithCompatibilitySpacing.ReportPath);
            var gate14ReadyManifestText = await File.ReadAllTextAsync(gate14ReadyPackage.ReadyManifestPath);
            var gate14ImageMapping = fp5Mapping.Mappings.Single(mapping => mapping.Status == "matched");
            var gate14ReadyImagePath = Path.Combine(
                gate14ReadyPackage.ReadyPath,
                gate14ImageMapping.ImageRelativePath!.Replace('/', Path.DirectorySeparatorChar));
            var tamperedLegacyPackagePath = Path.Combine(testRoot, "legacy-preview-tampered");
            Directory.CreateDirectory(tamperedLegacyPackagePath);
            foreach (var packageFile in Directory.EnumerateFiles(legacyPreview.PackagePath))
            {
                File.Copy(packageFile, Path.Combine(tamperedLegacyPackagePath, Path.GetFileName(packageFile)));
            }
            await File.AppendAllTextAsync(Path.Combine(tamperedLegacyPackagePath, "vehicles.csv"), "tampered");
            var rejectedLegacyPackageTamper = false;
            try
            {
                await new AbacusLegacyExportPreviewPackageReader().ReadAsync(tamperedLegacyPackagePath);
            }
            catch (InvalidDataException)
            {
                rejectedLegacyPackageTamper = true;
            }
            var matchingExportFolder = Path.Combine(testRoot, "matching-export");
            Directory.CreateDirectory(matchingExportFolder);
            File.Copy(
                Path.Combine(legacyExportFolder, "syaryou.csv"),
                Path.Combine(matchingExportFolder, "syaryou.csv"));
            var imageLinkMatch = await new AbacusImageLinkMatcher().MatchAsync(
                destination,
                matchingExportFolder);
            var imageLinkApproval = await new AbacusImageLinkApprovalStore().CreateAsync(
                destination,
                matchingExportFolder,
                Path.GetFileName(imageLinkManifest.FilePath));
            var registrationPackageParent = Path.Combine(testRoot, "registration-packages");
            Directory.CreateDirectory(registrationPackageParent);
            var imageRegistrationPackage = await new AbacusImageRegistrationPreviewStore().CreateAsync(
                destination,
                matchingExportFolder,
                registrationPackageParent);
            var registrationPackageManifestText = await File.ReadAllTextAsync(
                imageRegistrationPackage.ManifestPath);
            var copiedRegistrationImage = Path.Combine(
                imageRegistrationPackage.PackagePath,
                "images",
                $"{imageRegistrationPackage.Candidates[0].CandidateId}.png");
            var copiedRegistrationImageSha256 = Convert.ToHexString(
                SHA256.HashData(await File.ReadAllBytesAsync(copiedRegistrationImage)));
            var webImportPackageParent = Path.Combine(testRoot, "web-import-packages");
            Directory.CreateDirectory(webImportPackageParent);
            var webImportPackage = await new AbacusWebImportPreviewStore().CreateAsync(
                imageRegistrationPackage.PackagePath,
                webImportPackageParent);
            var webImportManifestText = await File.ReadAllTextAsync(webImportPackage.ManifestPath);
            var webCustomersCsvText = await File.ReadAllTextAsync(webImportPackage.CustomersCsvPath);
            var webVehiclesCsvText = await File.ReadAllTextAsync(webImportPackage.VehiclesCsvPath);
            var webImageAttachmentsText = await File.ReadAllTextAsync(webImportPackage.ImageAttachmentsPath);
            var copiedWebImage = Path.Combine(
                webImportPackage.PackagePath,
                webImportPackage.Candidates[0].PackageImageFileName.Replace('/', Path.DirectorySeparatorChar));
            var copiedWebImageSha256 = Convert.ToHexString(
                SHA256.HashData(await File.ReadAllBytesAsync(copiedWebImage)));
            var mappingPackage = await new AbacusWebImportMappingStore().ReadPackageAsync(
                webImportPackage.PackagePath);
            var mappingDestinationParent = Path.Combine(testRoot, "web-import-mappings");
            Directory.CreateDirectory(mappingDestinationParent);
            var mappingApproval = await new AbacusWebImportMappingStore().CreateApprovalAsync(
                webImportPackage.PackagePath,
                mappingDestinationParent,
                mappingPackage.Candidates
                    .Select(candidate => new AbacusWebImportMappingSelection(
                        candidate.CandidateId,
                        candidate.DefaultCustomerGroupKey))
                    .ToArray());
            var mappingManifestText = await File.ReadAllTextAsync(mappingApproval.MappingManifestPath);
            var webImportRegistrationPackageParent = Path.Combine(testRoot, "web-import-registration-packages");
            Directory.CreateDirectory(webImportRegistrationPackageParent);
            var webImportRegistrationPackage = await new AbacusWebImportRegistrationPackageStore().CreateAsync(
                mappingApproval.MappingPackagePath,
                webImportRegistrationPackageParent);
            var webImportRegistrationManifestText = await File.ReadAllTextAsync(
                webImportRegistrationPackage.ManifestPath);
            var webImportRegistrationCustomersCsvText = await File.ReadAllTextAsync(
                webImportRegistrationPackage.CustomersCsvPath);
            var webImportRegistrationVehiclesCsvText = await File.ReadAllTextAsync(
                webImportRegistrationPackage.VehiclesCsvPath);
            var webImportRegistrationAttachmentsText = await File.ReadAllTextAsync(
                webImportRegistrationPackage.ImageAttachmentsPath);
            var copiedWebImportRegistrationImage = Path.Combine(
                webImportRegistrationPackage.PackagePath,
                webImportRegistrationPackage.Vehicles[0].PackageImageFileName.Replace('/', Path.DirectorySeparatorChar));
            var copiedWebImportRegistrationImageSha256 = Convert.ToHexString(
                SHA256.HashData(await File.ReadAllBytesAsync(copiedWebImportRegistrationImage)));
            var rejectedDuplicateImageLinkApproval = false;
            try
            {
                await new AbacusImageLinkApprovalStore().CreateAsync(
                    destination,
                    matchingExportFolder,
                    Path.GetFileName(imageLinkManifest.FilePath));
            }
            catch (InvalidDataException)
            {
                rejectedDuplicateImageLinkApproval = true;
            }
            var duplicateImageLinkMatch = await new AbacusImageLinkMatcher().MatchAsync(
                destination,
                legacyExportFolder);
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
            var selfTestPassed = workspace.WorkspaceReport.FolderFingerprint == before.FolderFingerprint &&
                workspace.SourceAfterCopyReport.FolderFingerprint == before.FolderFingerprint &&
                outputPackageStructureValid &&
                File.Exists(workspace.ManifestPath) &&
                verifiedWorkspace.WorkspaceReport.FolderFingerprint == before.FolderFingerprint &&
                verifiedWorkspace.WorkspacePath == workspace.WorkspacePath &&
                verifiedWorkspace.AllowedRuntimeChanges.Count == 0 &&
                File.Exists(imageExport.FilePath) &&
                imageExport.FileSize > 8 &&
                imageExport.PixelWidth == 2 &&
                imageExport.PixelHeight == 2 &&
                imageExport.Sha256.Length == 64 &&
                File.Exists(imageLinkManifest.FilePath) &&
                imageLinkManifest.Sha256.Length == 64 &&
                imageLinkManifest.MatchStrategy == "chassis" &&
                imageLinkManifest.ChassisNumber == "CHASSIS1" &&
                imageLinkManifest.RegistrationNumber == "大阪537む16" &&
                rejectedImageLinkWithoutIdentifier &&
                imageLinkMatch.IsValid &&
                imageLinkMatch.MatchedCount == 1 &&
                imageLinkMatch.Rows is [{ Status: "matched" }] &&
                File.Exists(imageLinkApproval.FilePath) &&
                imageLinkApproval.Sha256.Length == 64 &&
                imageLinkApproval.VehicleCsvFileName == "syaryou.csv" &&
                imageLinkApproval.VehicleCsvRowNumber == 1 &&
                imageRegistrationPackage.CandidateCount == 1 &&
                imageRegistrationPackage.ImageCount == 1 &&
                File.Exists(imageRegistrationPackage.ManifestPath) &&
                File.Exists(copiedRegistrationImage) &&
                copiedRegistrationImageSha256 == imageRegistrationPackage.Candidates[0].ImageSha256 &&
                registrationPackageManifestText.Contains("abacus-image-registration-preview", StringComparison.Ordinal) &&
                registrationPackageManifestText.Contains("\"status\": \"preview-only\"", StringComparison.Ordinal) &&
                webImportPackage.CandidateCount == 1 &&
                webImportPackage.CustomerRowCount == 1 &&
                webImportPackage.VehicleRowCount == 1 &&
                webImportPackage.ImageCount == 1 &&
                webImportPackage.SameNameGroupCount == 0 &&
                webImportPackage.ManifestSha256.Length == 64 &&
                File.Exists(webImportPackage.CustomersCsvPath) &&
                File.Exists(webImportPackage.VehiclesCsvPath) &&
                File.Exists(webImportPackage.ImageAttachmentsPath) &&
                File.Exists(copiedWebImage) &&
                copiedWebImageSha256 == webImportPackage.Candidates[0].ImageSha256 &&
                webImportPackage.Candidates[0].ModelYear == "1994" &&
                webImportPackage.Candidates[0].InspectionDate == "2014-06-07" &&
                webImportPackage.Candidates[0].Mileage == "10300" &&
                webImportPackage.Candidates[0].InspectionRecord == "あり" &&
                webCustomersCsvText.TrimStart('\uFEFF').StartsWith("顧客ID,顧客番号,顧客名,ふりがな,電話番号,メールアドレス,郵便番号,住所,メモ,車両台数", StringComparison.Ordinal) &&
                webVehiclesCsvText.TrimStart('\uFEFF').StartsWith("車両ID,顧客ID,顧客名,メーカー,車名,型式,登録番号,車台番号,年式,車検満了日,走行距離,車体色,排気量,ミッション,記録簿,備考", StringComparison.Ordinal) &&
                webVehiclesCsvText.Contains("大阪537む16", StringComparison.Ordinal) &&
                webVehiclesCsvText.Contains("CHASSIS1", StringComparison.Ordinal) &&
                webImageAttachmentsText.Contains("manual-upload-required", StringComparison.Ordinal) &&
                webImportManifestText.Contains("abacus-web-import-preview", StringComparison.Ordinal) &&
                webImportManifestText.Contains("\"status\": \"preview-only\"", StringComparison.Ordinal) &&
                mappingPackage.CandidateCount == 1 &&
                mappingPackage.SameNameGroupCount == 0 &&
                mappingPackage.Candidates[0].DefaultCustomerGroupKey == webImportPackage.Candidates[0].CustomerId &&
                mappingApproval.CandidateCount == 1 &&
                mappingApproval.CustomerGroupCount == 1 &&
                mappingApproval.GroupedVehicleCount == 0 &&
                mappingApproval.SameNameGroupCount == 0 &&
                mappingApproval.MappingManifestSha256.Length == 64 &&
                mappingManifestText.Contains("abacus-web-import-mapping", StringComparison.Ordinal) &&
                mappingManifestText.Contains("\"status\": \"human-reviewed\"", StringComparison.Ordinal) &&
                webImportRegistrationPackage.CandidateCount == 1 &&
                webImportRegistrationPackage.CustomerRowCount == 1 &&
                webImportRegistrationPackage.VehicleRowCount == 1 &&
                webImportRegistrationPackage.ImageCount == 1 &&
                webImportRegistrationPackage.MergedVehicleCount == 0 &&
                webImportRegistrationPackage.ManifestSha256.Length == 64 &&
                webImportRegistrationPackage.Groups.Count == 1 &&
                webImportRegistrationPackage.Vehicles.Count == 1 &&
                File.Exists(webImportRegistrationPackage.ManifestPath) &&
                File.Exists(webImportRegistrationPackage.CustomersCsvPath) &&
                File.Exists(webImportRegistrationPackage.VehiclesCsvPath) &&
                File.Exists(webImportRegistrationPackage.ImageAttachmentsPath) &&
                File.Exists(copiedWebImportRegistrationImage) &&
                copiedWebImportRegistrationImageSha256 == webImportRegistrationPackage.Vehicles[0].ImageSha256 &&
                webImportRegistrationCustomersCsvText.TrimStart('\uFEFF').StartsWith("顧客ID,顧客番号,顧客名,ふりがな,電話番号,メールアドレス,郵便番号,住所,メモ,車両台数", StringComparison.Ordinal) &&
                webImportRegistrationVehiclesCsvText.TrimStart('\uFEFF').StartsWith("車両ID,顧客ID,顧客名,メーカー,車名,型式,登録番号,車台番号,年式,車検満了日,走行距離,車体色,排気量,ミッション,記録簿,備考", StringComparison.Ordinal) &&
                webImportRegistrationCustomersCsvText.Contains(",1\r\n", StringComparison.Ordinal) &&
                webImportRegistrationVehiclesCsvText.Contains("大阪537む16", StringComparison.Ordinal) &&
                webImportRegistrationVehiclesCsvText.Contains("CHASSIS1", StringComparison.Ordinal) &&
                webImportRegistrationAttachmentsText.Contains("manual-upload-required", StringComparison.Ordinal) &&
                webImportRegistrationManifestText.Contains("abacus-web-import-registration-package", StringComparison.Ordinal) &&
                webImportRegistrationManifestText.Contains("\"status\": \"registration-preview\"", StringComparison.Ordinal) &&
                rejectedDuplicateImageLinkApproval &&
                duplicateImageLinkMatch.ConflictCount == 1 &&
                cropResult.WasCropped &&
                cropResult.Image.PixelWidth == 88 &&
                cropResult.Image.PixelHeight == 88 &&
                toolbarCropResult.WasCropped &&
                toolbarCropResult.Image.PixelWidth == 132 &&
                toolbarCropResult.Image.PixelHeight == 96 &&
                verifiedUsedWorkspace.AllowedRuntimeChanges.Count == 2 &&
                rejectedUnallowedChange &&
                fp5Restoration.IsValid &&
                fp5Restoration.ImageNodeCount == 1 &&
                fp5Restoration.RestoredImageCount == 1 &&
                fp5Restoration.LengthCheckMatchCount == 1 &&
                fp5Restoration.DecodeSuccessCount == 1 &&
                fp5Restoration.UniqueImageSha256Count == 1 &&
                fp5Restoration.Images[0].FileSize == fakeJpeg.Length &&
                fp5Restoration.Images[0].ExpectedFileSize == fakeJpeg.Length &&
                fp5Restoration.Images[0].PixelWidth == 3 &&
                fp5Restoration.Images[0].PixelHeight == 2 &&
                File.Exists(Path.Combine(fp5Restoration.OutputFolderPath, fp5Restoration.Images[0].RelativePath)) &&
                fp5RestorationReportText.Contains("gate13-verified", StringComparison.Ordinal) &&
                fp5Mapping.IsValid &&
                fp5Mapping.IsFullyMatched &&
                fp5Mapping.InternalVehicleRecordCount == 1 &&
                fp5Mapping.VehicleCsvRowCount == 1 &&
                fp5Mapping.JpegImageCount == 1 &&
                fp5Mapping.GifPlaceholderCount == 0 &&
                fp5Mapping.MatchedImageCount == 1 &&
                fp5Mapping.NoImageCount == 0 &&
                fp5Mapping.ReviewCount == 0 &&
                fp5Mapping.UnmatchedCount == 0 &&
                fp5Mapping.MultipleCandidateCount == 0 &&
                fp5Mapping.UnknownImageReferenceCount == 0 &&
                fp5Mapping.DuplicateImageReferenceCount == 0 &&
                fp5Mapping.DuplicateImageSha256Count == 0 &&
                 fp5Mapping.UnreferencedImageCount == 0 &&
                 fp5MappingReportText.Contains("gate14-verified", StringComparison.Ordinal) &&
                 fp5MappingReportText.Contains("fp5-record-image-reference+exact-chassis-registration", StringComparison.Ordinal) &&
                 gate14FinalPackage.ImageCount == fp5Mapping.MatchedImageCount &&
                 gate14FinalPackage.ImageAttachmentsPath is not null &&
                 File.Exists(gate14FinalPackage.ImageAttachmentsPath) &&
                 gate14ReadyPackage.ReadyPath == outputPackageSession.ReadyPath &&
                 File.Exists(gate14ReadyImagePath) &&
                 File.Exists(Path.Combine(gate14ReadyPackage.ReadyPath, "reports", "fp5-vehicle-image-mapping-report.json")) &&
                 gate14ReadyManifestText.Contains("fp5-vehicle-image-mapping-report.json", StringComparison.Ordinal) &&
                 analysis.IsStructurallyValid &&
                analysis.TotalImportCandidateRows == 7 &&
                analysis.TotalSkippedBlankCustomerRows == 2 &&
                linkage.IsValid &&
                linkage.CustomerCandidates == 5 &&
                linkage.VehicleCandidates == 6 &&
                linkage.CustomersWithMultipleVehicles == 1 &&
                linkage.SameNameConflictGroups == 1 &&
                linkage.VehicleIdentifierConflictGroups == 1 &&
                fp5Inspection.IsValid &&
                fp5Inspection.JpegCandidateCount == 1 &&
                fp5Inspection.Candidates.Count == 1 &&
                fp5Inspection.Candidates[0].PixelWidth == 3 &&
                fp5Inspection.Candidates[0].PixelHeight == 2 &&
                File.Exists(fp5Export.FilePath) &&
                fp5Export.FileSize == fp5Inspection.Candidates[0].Length &&
                fp5Export.PixelWidth == 3 &&
                fp5Export.PixelHeight == 2 &&
                fp5Export.Sha256.Length == 64 &&
                rejectedInternalFp5Export &&
                rejectedMalformedFp5Export &&
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
                legacyPreview.CustomerRowCount == 1 &&
                legacyPreview.VehicleRowCount == 1 &&
                legacyPreview.SalesRowCount == 1 &&
                legacyPreview.MaintenanceRowCount == 1 &&
                legacyPreview.SkippedBlankCustomerRows == 0 &&
                legacyPreview.SkippedMaintenanceWithoutVehicleRows == 0 &&
                legacyPreview.AmbiguousVehicleRows == 0 &&
                legacyPreview.ManifestSha256.Length == 64 &&
                legacyPreviewManifestText.Contains("abacus-export-import-preview", StringComparison.Ordinal) &&
                legacyPreviewManifestText.Contains("\"status\": \"preview-only\"", StringComparison.Ordinal) &&
                legacyPreviewCustomersText.TrimStart('\uFEFF').StartsWith("顧客ID,顧客番号,顧客名", StringComparison.Ordinal) &&
                legacyPreviewVehiclesText.Contains("CHASSIS1", StringComparison.Ordinal) &&
                legacyPreviewSalesText.Contains("S-1", StringComparison.Ordinal) &&
                legacyPreviewMaintenanceText.Contains("M-1", StringComparison.Ordinal) &&
                legacyPackageRead.ManifestSha256 == legacyPreview.ManifestSha256 &&
                legacyPackageRead.CustomerRowCount == legacyPreview.CustomerRowCount &&
                legacyPackageRead.VehicleRowCount == legacyPreview.VehicleRowCount &&
                legacyPackageRead.SalesRowCount == legacyPreview.SalesRowCount &&
                legacyPackageRead.MaintenanceRowCount == legacyPreview.MaintenanceRowCount &&
                legacyPackageRead.DataFiles.Count == 4 &&
                legacyPackageRead.Rows.Count == legacyPreview.PreviewRows.Count &&
                legacyCandidateGraph.Customers.Count == 1 &&
                legacyCandidateGraph.Customers[0].Vehicles.Count == 1 &&
                legacyCandidateGraph.Customers[0].Documents == 2 &&
                legacyCandidateGraph.AllDocuments.Count == 2 &&
                legacyCandidateGraph.UnresolvedDocuments.Count == 0 &&
                legacyCandidateGraph.UnresolvedVehicleRows.Count == 0 &&
                legacyCandidateGraph.AllDocuments.All(document => !string.IsNullOrWhiteSpace(document.DocumentDate)) &&
                legacyCandidateGraph.SolidLinkCount == 2 &&
                legacyCandidateGraph.ReviewLinkCount == 0 &&
                legacyCandidateGraph.UnmatchedDocumentCount == 0 &&
                rejectedLegacyPackageTamper &&
                !invalidSales.IsValid &&
                !invalidMaintenance.IsValid;
            return selfTestPassed;
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

    private static async Task WriteFp5ImageSelfTestAsync(string path, byte[] jpeg)
    {
        const int sectorBytes = 1024;
        var file = new byte[5 * sectorBytes];
        var magic = new byte[]
        {
            0x00, 0x01, 0x00, 0x00, 0x00, 0x02, 0x00, 0x01,
            0x00, 0x05, 0x00, 0x02, 0x00, 0x02, 0xC0,
        };
        magic.CopyTo(file, 0);
        var version = Encoding.ASCII.GetBytes("Pro 5.0");
        file[541] = checked((byte)version.Length);
        version.CopyTo(file, 542);

        var topPayload = new byte[] { 0x00, 0x04, 0x00, 0x00, 0x00, 0x01 };
        WriteSector(file, 2 * sectorBytes, level: 1, previousId: 0, nextId: 2, skipBytes: 0, topPayload);

        var finalSegmentLength = Math.Min(200, jpeg.Length / 2);
        var firstSegmentLength = jpeg.Length - finalSegmentLength;
        if (firstSegmentLength <= 0 || finalSegmentLength <= 0 || finalSegmentLength > byte.MaxValue)
        {
            throw new InvalidOperationException("FP5セルフテスト用JPEGを分割できません。");
        }

        using var payload = new MemoryStream();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var shiftJis = Encoding.GetEncoding(932);
        payload.Write([0xC1, 0x03, 0xC1, 0x05]);
        WriteFieldDefinition(payload, 0x10, "登録番号", shiftJis);
        WriteFieldDefinition(payload, 0x25, "車検証画像", shiftJis);
        WriteFieldDefinition(payload, 0x37, "車体番号", shiftJis);
        payload.Write([0xC0, 0xC0]);
        payload.Write([0xC1, 0x05, 0xC1, 0x01]);
        WriteSimpleField(payload, 0x10, shiftJis.GetBytes("大阪537む16"));
        WriteSimpleField(payload, 0x25, [0x01, 0x02]);
        WriteSimpleField(payload, 0x37, shiftJis.GetBytes("CHASSIS1"));
        payload.Write([0xC0, 0xC0]);
        payload.Write([0xC1, 0x1F, 0xC1, 0x05, 0xC1, 0x02, 0xC4, 0x4A, 0x50, 0x45, 0x47]);
        payload.WriteByte(0xFF);
        payload.WriteByte(0x41);
        Span<byte> longLength = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(longLength, checked((ushort)firstSegmentLength));
        payload.Write(longLength);
        payload.Write(jpeg, 0, firstSegmentLength);
        payload.WriteByte(0x42);
        payload.WriteByte(checked((byte)finalSegmentLength));
        payload.Write(jpeg, firstSegmentLength, finalSegmentLength);
        payload.Write([0x01, 0xFF, 0x05]);
        Span<byte> expectedLength = stackalloc byte[5];
        expectedLength[0] = checked((byte)((long)jpeg.Length >> 32));
        expectedLength[1] = checked((byte)((long)jpeg.Length >> 24));
        expectedLength[2] = checked((byte)((long)jpeg.Length >> 16));
        expectedLength[3] = checked((byte)((long)jpeg.Length >> 8));
        expectedLength[4] = checked((byte)jpeg.Length);
        payload.Write(expectedLength);
        payload.Write([0xC0, 0xC0]);

        WriteSector(file, 3 * sectorBytes, level: 0, previousId: 0, nextId: 2, skipBytes: 0, payload.ToArray());
        WriteSector(file, 4 * sectorBytes, level: 0, previousId: 1, nextId: 0, skipBytes: 1, [0xC0, 0xC0]);
        await File.WriteAllBytesAsync(path, file);

        static void WriteFieldDefinition(Stream stream, byte fieldId, string name, Encoding encoding)
        {
            var nameBytes = encoding.GetBytes(name);
            stream.Write([0xC1, fieldId, 0x41, checked((byte)nameBytes.Length)]);
            stream.Write(nameBytes);
            stream.WriteByte(0xC0);
        }

        static void WriteSimpleField(Stream stream, byte fieldId, byte[] value)
        {
            stream.WriteByte(checked((byte)(0x40 + fieldId)));
            stream.WriteByte(checked((byte)value.Length));
            stream.Write(value);
        }

        static void WriteSector(
            byte[] destination,
            int offset,
            byte level,
            uint previousId,
            uint nextId,
            ushort skipBytes,
            byte[] payloadBytes)
        {
            if (payloadBytes.Length > sectorBytes - 14)
            {
                throw new InvalidOperationException("FP5セルフテストpayloadがsector上限を超えています。");
            }

            destination[offset] = 0;
            destination[offset + 1] = level;
            BinaryPrimitives.WriteUInt32BigEndian(destination.AsSpan(offset + 2, 4), previousId);
            BinaryPrimitives.WriteUInt32BigEndian(destination.AsSpan(offset + 6, 4), nextId);
            BinaryPrimitives.WriteUInt16BigEndian(destination.AsSpan(offset + 10, 2), skipBytes);
            BinaryPrimitives.WriteUInt16BigEndian(destination.AsSpan(offset + 12, 2), checked((ushort)payloadBytes.Length));
            payloadBytes.CopyTo(destination, offset + 14);
        }
    }
}
