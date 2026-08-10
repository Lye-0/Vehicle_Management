using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using VehicleManagement.LocalProtocol;

namespace VehicleManagement.LegacyHost;

internal static partial class Program
{
    private const string AbacusExecutableFileName = "ABACUS カーショップPRO④.EXE";
    private static Process? abacusProcess;
    private static string? abacusExecutablePath;

    [STAThread]
    private static async Task<int> Main()
    {
        var pipeName = Environment.GetEnvironmentVariable(LegacyHostProtocol.PipeEnvironmentVariable);
        var expectedToken = Environment.GetEnvironmentVariable(LegacyHostProtocol.TokenEnvironmentVariable);

        if (string.IsNullOrWhiteSpace(pipeName) || !ValidPipeName().IsMatch(pipeName))
        {
            return 10;
        }

        if (string.IsNullOrWhiteSpace(expectedToken))
        {
            return 11;
        }

        try
        {
            await using var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            using var connectionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await pipe.WaitForConnectionAsync(connectionTimeout.Token);
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true,
            };

            var hello = await LegacyHostProtocol.ReadAsync(reader);
            if (hello.Type != "hello" ||
                hello.ProtocolVersion != LegacyHostProtocol.Version ||
                !TokensMatch(hello.Token, expectedToken))
            {
                await LegacyHostProtocol.WriteAsync(
                    writer,
                    new LegacyHostMessage("error", hello.RequestId, Status: "unauthorized", Message: "Handshake failed."));
                return 12;
            }

            await LegacyHostProtocol.WriteAsync(
                writer,
                new LegacyHostMessage(
                    "ready",
                    hello.RequestId,
                    Status: "connected",
                    ProcessId: Environment.ProcessId,
                    Architecture: HostArchitecture));

            while (pipe.IsConnected)
            {
                var request = await LegacyHostProtocol.ReadAsync(reader);
                LegacyHostMessage response;
                try
                {
                    response = request.Type switch
                    {
                        "ping" => new LegacyHostMessage(
                            "pong",
                            request.RequestId,
                            Status: "connected",
                            ProcessId: Environment.ProcessId,
                            Architecture: HostArchitecture),
                        "launch-abacus" => await LaunchAndInspectAbacusAsync(request),
                        "inspect-abacus" => await InspectAbacusAsync(request.RequestId),
                        "close-abacus" => await CloseAbacusAsync(request.RequestId),
                        "shutdown" => new LegacyHostMessage("stopping", request.RequestId, Status: "stopping"),
                        _ => new LegacyHostMessage(
                            "error",
                            request.RequestId,
                            Status: "unsupported",
                            Message: "Unsupported request."),
                    };
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or COMException)
                {
                    response = new LegacyHostMessage(
                        "error",
                        request.RequestId,
                        Status: "legacy-operation-failed",
                        Message: exception.Message,
                        Architecture: HostArchitecture);
                }

                await LegacyHostProtocol.WriteAsync(writer, response);
                if (request.Type == "shutdown")
                {
                    return 0;
                }
            }

            return 0;
        }
        catch (EndOfStreamException)
        {
            return 0;
        }
        catch (IOException)
        {
            return 20;
        }
        catch (UnauthorizedAccessException)
        {
            return 21;
        }
        catch (OperationCanceledException)
        {
            // Prevent an orphaned host if the companion exits before connecting.
            return 22;
        }
        finally
        {
            abacusProcess?.Dispose();
        }
    }

    private static async Task<LegacyHostMessage> LaunchAndInspectAbacusAsync(LegacyHostMessage request)
    {
        try
        {
            if (abacusProcess is { HasExited: false })
            {
                return await InspectAbacusAsync(request.RequestId);
            }

            var executablePath = ValidateAbacusExecutable(request.TargetPath);
            var startInfo = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            };
            abacusProcess = Process.Start(startInfo)
                ?? throw new InvalidOperationException("ABACUSプロセスを開始できませんでした。");
            abacusExecutablePath = executablePath;

            return await WaitForAbacusWindowAsync(request.RequestId, executablePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new LegacyHostMessage(
                "error",
                request.RequestId,
                Status: "abacus-launch-failed",
                Message: exception.Message,
                Architecture: HostArchitecture);
        }
    }

    private static async Task<LegacyHostMessage> InspectAbacusAsync(string requestId)
    {
        if (abacusProcess is null || abacusProcess.HasExited)
        {
            return new LegacyHostMessage(
                "error",
                requestId,
                Status: "abacus-not-running",
                Message: "ABACUSは起動していません。",
                Architecture: HostArchitecture);
        }

        return await WaitForAbacusWindowAsync(requestId, abacusExecutablePath);
    }

    private static async Task<LegacyHostMessage> WaitForAbacusWindowAsync(string requestId, string? executablePath)
    {
        if (abacusProcess is null)
        {
            throw new InvalidOperationException("ABACUSプロセスがありません。");
        }

        var timeoutAt = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < timeoutAt)
        {
            if (abacusProcess.HasExited)
            {
                return new LegacyHostMessage(
                    "error",
                    requestId,
                    Status: "abacus-exited",
                    Message: $"ABACUSが終了しました（終了コード: {abacusProcess.ExitCode}）。",
                    ProcessId: abacusProcess.Id,
                    Architecture: HostArchitecture,
                    TargetArchitecture: executablePath is null ? null : ReadPeArchitecture(executablePath));
            }

            abacusProcess.Refresh();
            var handle = abacusProcess.MainWindowHandle;
            if (handle != IntPtr.Zero)
            {
                try
                {
                    var element = AutomationElement.FromHandle(handle);
                    var children = element.FindAll(TreeScope.Children, Condition.TrueCondition);
                    return new LegacyHostMessage(
                        "abacus-inspected",
                        requestId,
                        Status: "ui-automation-ready",
                        Message: "x64 LegacyHostからABACUSのウィンドウを認識しました。",
                        ProcessId: abacusProcess.Id,
                        Architecture: HostArchitecture,
                        TargetPath: executablePath,
                        TargetArchitecture: executablePath is null ? null : ReadPeArchitecture(executablePath),
                        WindowHandle: handle.ToInt64(),
                        WindowTitle: element.Current.Name,
                        AutomationElementCount: children.Count);
                }
                catch (ElementNotAvailableException)
                {
                    // The runtime may replace its startup window; retry until the timeout.
                }
            }

            await Task.Delay(250);
        }

        return new LegacyHostMessage(
            "error",
            requestId,
            Status: "ui-automation-timeout",
            Message: "ABACUSのメインウィンドウを30秒以内に認識できませんでした。",
            ProcessId: abacusProcess.Id,
            Architecture: HostArchitecture,
            TargetArchitecture: executablePath is null ? null : ReadPeArchitecture(executablePath));
    }

    private static async Task<LegacyHostMessage> CloseAbacusAsync(string requestId)
    {
        if (abacusProcess is null || abacusProcess.HasExited)
        {
            return new LegacyHostMessage(
                "abacus-closed",
                requestId,
                Status: "not-running",
                Message: "ABACUSは起動していません。",
                Architecture: HostArchitecture);
        }

        abacusProcess.CloseMainWindow();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await abacusProcess.WaitForExitAsync(timeout.Token);
            return new LegacyHostMessage(
                "abacus-closed",
                requestId,
                Status: "closed",
                Message: "ABACUSを正常終了しました。",
                Architecture: HostArchitecture);
        }
        catch (OperationCanceledException)
        {
            return new LegacyHostMessage(
                "abacus-close-pending",
                requestId,
                Status: "manual-close-required",
                Message: "ABACUSを自動終了できません。ABACUS画面で未保存確認などに応答して閉じてください。",
                ProcessId: abacusProcess.Id,
                Architecture: HostArchitecture);
        }
    }

    private static string ValidateAbacusExecutable(string? requestedPath)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            throw new InvalidOperationException("ABACUS実行ファイルが指定されていません。");
        }

        var fullPath = Path.GetFullPath(requestedPath);
        if (!string.Equals(Path.GetFileName(fullPath), AbacusExecutableFileName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("許可されていない実行ファイルです。");
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("ABACUS実行ファイルが見つかりません。", fullPath);
        }

        if (new FileInfo(fullPath).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("リンクまたは再解析ポイントの実行ファイルは使用できません。");
        }

        return fullPath;
    }

    private static string ReadPeArchitecture(string executablePath)
    {
        try
        {
            using var stream = File.Open(executablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> header = stackalloc byte[64];
            if (stream.Read(header) != header.Length || header[0] != 'M' || header[1] != 'Z')
            {
                return "unknown";
            }

            stream.Position = BinaryPrimitives.ReadInt32LittleEndian(header[0x3c..]);
            Span<byte> peHeader = stackalloc byte[6];
            if (stream.Read(peHeader) != peHeader.Length)
            {
                return "unknown";
            }

            return BinaryPrimitives.ReadUInt16LittleEndian(peHeader[4..]) switch
            {
                0x014c => "x86",
                0x8664 => "x64",
                0xaa64 => "arm64",
                _ => "unknown",
            };
        }
        catch (IOException)
        {
            return "unknown";
        }
    }

    private static bool TokensMatch(string? actualToken, string expectedToken)
    {
        if (actualToken is null)
        {
            return false;
        }

        var actualBytes = Encoding.UTF8.GetBytes(actualToken);
        var expectedBytes = Encoding.UTF8.GetBytes(expectedToken);
        return actualBytes.Length == expectedBytes.Length &&
            CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
    }

    private static string HostArchitecture => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

    [GeneratedRegex("^[A-Za-z0-9.-]{1,120}$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidPipeName();
}
