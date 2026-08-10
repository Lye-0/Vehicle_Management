using System.Windows;
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
            return session.Snapshot.State == LegacyHostState.Stopped ? 0 : 2;
        }
        catch
        {
            return 1;
        }
    }
}
