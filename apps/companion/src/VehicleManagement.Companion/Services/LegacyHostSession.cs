using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using VehicleManagement.LocalProtocol;

namespace VehicleManagement.Companion.Services;

public sealed class LegacyHostSession : IAsyncDisposable
{
    private readonly SemaphoreSlim operationLock = new(1, 1);
    private Process? process;
    private NamedPipeClientStream? pipe;
    private StreamReader? reader;
    private StreamWriter? writer;
    private bool disposed;

    public LegacyHostSnapshot Snapshot { get; private set; } = new(LegacyHostState.Stopped);

    public event EventHandler<LegacyHostSnapshot>? StateChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            if (Snapshot.State is LegacyHostState.Starting or LegacyHostState.Connected)
            {
                return;
            }

            Publish(new LegacyHostSnapshot(LegacyHostState.Starting, Detail: "LegacyHostを起動しています。"));
            var pipeName = $"VehicleManagement.LegacyHost.{Guid.NewGuid():N}";
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var hostPath = ResolveLegacyHostPath();

            var startInfo = new ProcessStartInfo(hostPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(hostPath)!,
            };
            startInfo.Environment[LegacyHostProtocol.PipeEnvironmentVariable] = pipeName;
            startInfo.Environment[LegacyHostProtocol.TokenEnvironmentVariable] = token;

            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("LegacyHostプロセスを開始できませんでした。");

            pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            using var connectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectionTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            await pipe.ConnectAsync(connectionTimeout.Token);

            reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true,
            };

            var requestId = Guid.NewGuid().ToString("N");
            await LegacyHostProtocol.WriteAsync(
                writer,
                new LegacyHostMessage("hello", requestId, Token: token),
                connectionTimeout.Token);
            var response = await LegacyHostProtocol.ReadAsync(reader, connectionTimeout.Token);
            EnsureResponse(response, requestId, "ready");

            Publish(new LegacyHostSnapshot(
                LegacyHostState.Connected,
                response.ProcessId ?? process.Id,
                response.Architecture,
                "LegacyHostと接続しました。"));
        }
        catch (Exception exception)
        {
            await CleanupProcessAsync();
            Publish(new LegacyHostSnapshot(LegacyHostState.Failed, Detail: ToUserMessage(exception)));
            throw;
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task PingAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            EnsureConnected();
            var requestId = Guid.NewGuid().ToString("N");
            await LegacyHostProtocol.WriteAsync(
                writer!,
                new LegacyHostMessage("ping", requestId),
                cancellationToken);
            var response = await LegacyHostProtocol.ReadAsync(reader!, cancellationToken);
            EnsureResponse(response, requestId, "pong");

            Publish(Snapshot with
            {
                ProcessId = response.ProcessId ?? Snapshot.ProcessId,
                Architecture = response.Architecture ?? Snapshot.Architecture,
                Detail = $"接続確認に成功しました（{DateTime.Now:HH:mm:ss}）。",
            });
        }
        catch (Exception exception)
        {
            Publish(Snapshot with { State = LegacyHostState.Failed, Detail = ToUserMessage(exception) });
            throw;
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task<AbacusRuntimeSnapshot> LaunchAndInspectAbacusAsync(
        string executablePath,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            EnsureConnected();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(40));
            var response = await SendAsync(
                new LegacyHostMessage("launch-abacus", Guid.NewGuid().ToString("N"), TargetPath: executablePath),
                timeout.Token);
            return ToAbacusSnapshot(response);
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task<AbacusRuntimeSnapshot> InspectAbacusAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            EnsureConnected();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(35));
            var response = await SendAsync(
                new LegacyHostMessage("inspect-abacus", Guid.NewGuid().ToString("N")),
                timeout.Token);
            return ToAbacusSnapshot(response);
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task<AbacusRuntimeSnapshot> InspectAbacusUiAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            EnsureConnected();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var response = await SendAsync(
                new LegacyHostMessage("inspect-abacus-ui", Guid.NewGuid().ToString("N")),
                timeout.Token);
            return ToAbacusSnapshot(response);
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task<AbacusRuntimeSnapshot> InspectAbacusMenuAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            EnsureConnected();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var response = await SendAsync(
                new LegacyHostMessage("inspect-abacus-menu", Guid.NewGuid().ToString("N")),
                timeout.Token);
            return ToAbacusSnapshot(response);
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task<AbacusRuntimeSnapshot> CloseAbacusAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            EnsureConnected();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var response = await SendAsync(
                new LegacyHostMessage("close-abacus", Guid.NewGuid().ToString("N")),
                timeout.Token);
            return ToAbacusSnapshot(response);
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            if (process is null && pipe is null)
            {
                Publish(new LegacyHostSnapshot(LegacyHostState.Stopped, Detail: "LegacyHostは停止しています。"));
                return;
            }

            Publish(Snapshot with { State = LegacyHostState.Stopping, Detail = "LegacyHostを停止しています。" });
            if (pipe?.IsConnected == true && writer is not null && reader is not null)
            {
                using var shutdownTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                shutdownTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    var requestId = Guid.NewGuid().ToString("N");
                    await LegacyHostProtocol.WriteAsync(
                        writer,
                        new LegacyHostMessage("shutdown", requestId),
                        shutdownTimeout.Token);
                    var response = await LegacyHostProtocol.ReadAsync(reader, shutdownTimeout.Token);
                    EnsureResponse(response, requestId, "stopping");
                }
                catch (OperationCanceledException)
                {
                    // CleanupProcessAsync terminates only the child process if graceful shutdown times out.
                }
                catch (IOException)
                {
                    // The host may close the pipe immediately after receiving the shutdown request.
                }
            }

            await CleanupProcessAsync();
            Publish(new LegacyHostSnapshot(LegacyHostState.Stopped, Detail: "LegacyHostを停止しました。"));
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        await StopAsync();
        disposed = true;
        operationLock.Dispose();
    }

    private static string ResolveLegacyHostPath()
    {
        var executableName = OperatingSystem.IsWindows()
            ? "VehicleManagement.LegacyHost.exe"
            : "VehicleManagement.LegacyHost";
        var path = Path.Combine(AppContext.BaseDirectory, "LegacyHost", executableName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("LegacyHostの実行ファイルが見つかりません。", path);
        }

        return path;
    }

    private static void EnsureResponse(LegacyHostMessage response, string requestId, string expectedType)
    {
        if (response.RequestId != requestId || response.Type != expectedType)
        {
            throw new InvalidDataException("LegacyHostから予期しない応答を受信しました。");
        }
    }

    private async Task<LegacyHostMessage> SendAsync(
        LegacyHostMessage request,
        CancellationToken cancellationToken)
    {
        await LegacyHostProtocol.WriteAsync(writer!, request, cancellationToken);
        var response = await LegacyHostProtocol.ReadAsync(reader!, cancellationToken);
        if (response.RequestId != request.RequestId)
        {
            throw new InvalidDataException("LegacyHostから別の要求に対する応答を受信しました。");
        }

        return response;
    }

    private static AbacusRuntimeSnapshot ToAbacusSnapshot(LegacyHostMessage response) => new(
        response.Type == "abacus-inspected" && response.Status == "ui-automation-ready",
        response.Status ?? "unknown",
        response.Message ?? "LegacyHostから詳細メッセージが返されませんでした。",
        response.ProcessId,
        response.Architecture,
        response.TargetArchitecture,
        response.WindowHandle,
        response.WindowTitle,
        response.AutomationElementCount,
        response.AutomationElements,
        response.MenuItems);

    private void EnsureConnected()
    {
        if (Snapshot.State != LegacyHostState.Connected || pipe?.IsConnected != true || process?.HasExited != false)
        {
            throw new InvalidOperationException("LegacyHostに接続されていません。");
        }
    }

    private async Task CleanupProcessAsync()
    {
        writer?.Dispose();
        reader?.Dispose();
        pipe?.Dispose();
        writer = null;
        reader = null;
        pipe = null;

        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    try
                    {
                        await process.WaitForExitAsync(exitTimeout.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync();
                    }
                }
            }
            finally
            {
                process.Dispose();
                process = null;
            }
        }
    }

    private void Publish(LegacyHostSnapshot snapshot)
    {
        Snapshot = snapshot;
        StateChanged?.Invoke(this, snapshot);
    }

    private static string ToUserMessage(Exception exception) => exception switch
    {
        FileNotFoundException => "LegacyHostの実行ファイルが見つかりません。再ビルドしてください。",
        OperationCanceledException => "LegacyHostへの接続がタイムアウトしました。",
        _ => $"LegacyHostとの通信に失敗しました: {exception.Message}",
    };
}
