using System.Windows;
using System.IO;
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

            var inspector = new AbacusFolderInspector();
            var before = await inspector.InspectAsync(source);
            if (!before.IsValid)
            {
                return false;
            }

            var workspace = await new AbacusWorkspaceService(inspector).CreateAsync(before, destination);
            return workspace.WorkspaceReport.FolderFingerprint == before.FolderFingerprint &&
                workspace.SourceAfterCopyReport.FolderFingerprint == before.FolderFingerprint &&
                File.Exists(workspace.ManifestPath);
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
}
