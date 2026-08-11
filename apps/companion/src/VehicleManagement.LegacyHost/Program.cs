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
    private const uint MenuByPosition = 0x00000400;
    private const uint MenuDisabled = 0x00000002;
    private const uint MenuGrayed = 0x00000001;
    private const uint MenuSeparator = 0x00000800;
    private const uint NoMenuCommand = uint.MaxValue;
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
                        "inspect-abacus-ui" => await InspectAbacusUiAsync(request.RequestId),
                        "inspect-abacus-menu" => InspectAbacusMenu(request.RequestId),
                        "inspect-abacus-native-windows" => InspectAbacusNativeWindows(request.RequestId),
                        "inspect-abacus-automation" => InspectAbacusAutomation(request.RequestId),
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
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

    private static Task<LegacyHostMessage> InspectAbacusUiAsync(string requestId)
    {
        if (abacusProcess is null || abacusProcess.HasExited || abacusProcess.MainWindowHandle == IntPtr.Zero)
        {
            return Task.FromResult(new LegacyHostMessage(
                "error",
                requestId,
                Status: "abacus-not-running",
                Message: "コピー側ABACUSを起動し、画像を表示してから診断してください。",
                Architecture: HostArchitecture));
        }

        var root = AutomationElement.FromHandle(abacusProcess.MainWindowHandle);
        var descendants = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        var elements = new List<LegacyAutomationElementInfo>();
        for (var index = 0; index < descendants.Count && elements.Count < 30; index++)
        {
            var element = descendants[index];
            try
            {
                var controlType = element.Current.ControlType?.ProgrammaticName
                    .Replace("ControlType.", string.Empty, StringComparison.Ordinal) ?? "Unknown";
                var name = NormalizeAutomationText(element.Current.Name, 60);
                var automationId = NormalizeAutomationText(element.Current.AutomationId, 40);
                var className = NormalizeAutomationText(element.Current.ClassName, 40);
                if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(automationId) &&
                    controlType is not ("Image" or "Document" or "Edit" or "Button" or "MenuItem" or "Custom"))
                {
                    continue;
                }

                elements.Add(new LegacyAutomationElementInfo(
                    elements.Count + 1,
                    GetAutomationDepth(element, root),
                    controlType,
                    name,
                    automationId,
                    className,
                    element.Current.NativeWindowHandle,
                    ToBoundedInt(element.Current.BoundingRectangle.Left),
                    ToBoundedInt(element.Current.BoundingRectangle.Top),
                    ToBoundedInt(element.Current.BoundingRectangle.Width),
                    ToBoundedInt(element.Current.BoundingRectangle.Height),
                    element.Current.IsEnabled,
                    element.Current.IsKeyboardFocusable,
                    element.Current.HasKeyboardFocus));
            }
            catch (ElementNotAvailableException)
            {
                // FileMaker may rebuild controls while moving between layouts; skip stale entries.
            }
        }

        return Task.FromResult(new LegacyHostMessage(
            "abacus-ui-inspected",
            requestId,
            Status: "ui-structure-ready",
            Message: elements.Count == 0
                ? "標準UI Automationで詳細要素を取得できませんでした。次段階では画面キャプチャ方式を検討します。"
                : $"UI要素を{elements.Count:N0}件取得しました（全子孫要素: {descendants.Count:N0}件、表示上限: 30件）。",
            ProcessId: abacusProcess.Id,
            Architecture: HostArchitecture,
            TargetPath: abacusExecutablePath,
            TargetArchitecture: abacusExecutablePath is null ? null : ReadPeArchitecture(abacusExecutablePath),
            WindowHandle: abacusProcess.MainWindowHandle.ToInt64(),
            WindowTitle: root.Current.Name,
            AutomationElementCount: descendants.Count,
            AutomationElements: elements));
    }

    private static LegacyHostMessage InspectAbacusMenu(string requestId)
    {
        if (abacusProcess is null || abacusProcess.HasExited)
        {
            return new LegacyHostMessage(
                "error",
                requestId,
                Status: "abacus-not-running",
                Message: "コピー側ABACUSを起動してから診断してください。",
                Architecture: HostArchitecture);
        }

        abacusProcess.Refresh();
        var windowHandle = abacusProcess.MainWindowHandle;
        if (windowHandle == IntPtr.Zero ||
            GetWindowThreadProcessId(windowHandle, out var ownerProcessId) == 0 ||
            ownerProcessId != (uint)abacusProcess.Id)
        {
            return new LegacyHostMessage(
                "error",
                requestId,
                Status: "abacus-window-unavailable",
                Message: "コピー側ABACUSのメインウィンドウを安全に確認できませんでした。",
                ProcessId: abacusProcess.Id,
                Architecture: HostArchitecture);
        }

        var menuHandle = GetMenu(windowHandle);
        if (menuHandle == IntPtr.Zero)
        {
            return new LegacyHostMessage(
                "abacus-menu-inspected",
                requestId,
                Status: "standard-menu-unavailable",
                Message: "標準Windowsメニューは取得できませんでした。操作・エクスポートは行っていません。",
                ProcessId: abacusProcess.Id,
                Architecture: HostArchitecture,
                TargetArchitecture: abacusExecutablePath is null ? null : ReadPeArchitecture(abacusExecutablePath),
                WindowHandle: windowHandle.ToInt64(),
                WindowTitle: NormalizeAutomationText(abacusProcess.MainWindowTitle, 80),
                MenuItems: []);
        }

        var items = new List<LegacyMenuItemInfo>();
        EnumerateMenu(menuHandle, 0, items);
        return new LegacyHostMessage(
            "abacus-menu-inspected",
            requestId,
            Status: items.Count == 0 ? "standard-menu-empty" : "standard-menu-ready",
            Message: items.Count == 0
                ? "標準Windowsメニューに項目がありませんでした。操作・エクスポートは行っていません。"
                : $"標準Windowsメニューを{items.Count:N0}件取得しました。操作・エクスポートは行っていません。",
            ProcessId: abacusProcess.Id,
            Architecture: HostArchitecture,
            TargetArchitecture: abacusExecutablePath is null ? null : ReadPeArchitecture(abacusExecutablePath),
            WindowHandle: windowHandle.ToInt64(),
            WindowTitle: NormalizeAutomationText(abacusProcess.MainWindowTitle, 80),
            MenuItems: items);
    }

    private static void EnumerateMenu(IntPtr menuHandle, int depth, List<LegacyMenuItemInfo> items)
    {
        if (depth > 7 || items.Count >= 40)
        {
            return;
        }

        var itemCount = Math.Clamp(GetMenuItemCount(menuHandle), 0, 40);
        for (var position = 0; position < itemCount && items.Count < 40; position++)
        {
            var state = GetMenuState(menuHandle, (uint)position, MenuByPosition);
            var submenu = GetSubMenu(menuHandle, position);
            var commandId = GetMenuItemID(menuHandle, position);
            var buffer = new StringBuilder(257);
            _ = GetMenuString(menuHandle, (uint)position, buffer, buffer.Capacity, MenuByPosition);
            var isSeparator = state != NoMenuCommand && (state & MenuSeparator) != 0;
            items.Add(new LegacyMenuItemInfo(
                items.Count + 1,
                depth,
                NormalizeMenuText(buffer.ToString()),
                commandId == NoMenuCommand ? null : commandId,
                state != NoMenuCommand && (state & (MenuDisabled | MenuGrayed)) == 0,
                isSeparator,
                submenu != IntPtr.Zero));

            if (submenu != IntPtr.Zero)
            {
                EnumerateMenu(submenu, depth + 1, items);
            }
        }
    }

    private static string NormalizeMenuText(string value)
    {
        var shortcutStart = value.IndexOf('\t');
        if (shortcutStart >= 0)
        {
            value = value[..shortcutStart];
        }

        return NormalizeAutomationText(value.Replace("&", string.Empty, StringComparison.Ordinal), 80);
    }

    private static LegacyHostMessage InspectAbacusNativeWindows(string requestId)
    {
        if (abacusProcess is null || abacusProcess.HasExited)
        {
            return new LegacyHostMessage(
                "error",
                requestId,
                Status: "abacus-not-running",
                Message: "コピー側ABACUSを起動してから診断してください。",
                Architecture: HostArchitecture);
        }

        abacusProcess.Refresh();
        var rootHandle = abacusProcess.MainWindowHandle;
        if (rootHandle == IntPtr.Zero ||
            GetWindowThreadProcessId(rootHandle, out var ownerProcessId) == 0 ||
            ownerProcessId != (uint)abacusProcess.Id)
        {
            return new LegacyHostMessage(
                "error",
                requestId,
                Status: "abacus-window-unavailable",
                Message: "コピー側ABACUSのメインウィンドウを安全に確認できませんでした。",
                ProcessId: abacusProcess.Id,
                Architecture: HostArchitecture);
        }

        var windows = new List<LegacyNativeWindowInfo>();
        EnumChildWindows(rootHandle, (windowHandle, _) =>
        {
            if (windows.Count >= 100 ||
                GetWindowThreadProcessId(windowHandle, out var childProcessId) == 0 ||
                childProcessId != (uint)abacusProcess.Id)
            {
                return windows.Count < 100;
            }

            windows.Add(new LegacyNativeWindowInfo(
                windows.Count + 1,
                GetNativeWindowDepth(windowHandle, rootHandle),
                windowHandle.ToInt64(),
                ReadClassName(windowHandle),
                ReadWindowTitle(windowHandle),
                GetDlgCtrlID(windowHandle),
                IsWindowVisible(windowHandle),
                IsWindowEnabled(windowHandle),
                GetWindowLongPtr(windowHandle, -16)));
            return windows.Count < 100;
        }, IntPtr.Zero);

        return new LegacyHostMessage(
            "abacus-native-windows-inspected",
            requestId,
            Status: windows.Count == 0 ? "native-windows-empty" : "native-windows-ready",
            Message: windows.Count == 0
                ? "ネイティブ子ウィンドウは取得できませんでした。操作・エクスポートは行っていません。"
                : $"ネイティブ子ウィンドウを{windows.Count:N0}件取得しました。操作・エクスポートは行っていません。",
            ProcessId: abacusProcess.Id,
            Architecture: HostArchitecture,
            TargetArchitecture: abacusExecutablePath is null ? null : ReadPeArchitecture(abacusExecutablePath),
            WindowHandle: rootHandle.ToInt64(),
            WindowTitle: NormalizeAutomationText(abacusProcess.MainWindowTitle, 80),
            NativeWindows: windows);
    }

    private static LegacyHostMessage InspectAbacusAutomation(string requestId)
    {
        if (abacusProcess is null || abacusProcess.HasExited)
        {
            return new LegacyHostMessage(
                "error",
                requestId,
                Status: "abacus-not-running",
                Message: "コピー側ABACUSを起動してから診断してください。",
                Architecture: HostArchitecture);
        }

        abacusProcess.Refresh();
        var probePath = Path.Combine(AppContext.BaseDirectory, "VehicleManagement.LegacyAutomationProbe.exe");
        if (!File.Exists(probePath))
        {
            return new LegacyHostMessage(
                "abacus-automation-inspected",
                requestId,
                Status: "automation-probe-unavailable",
                Message: "x86 ActiveX診断プロセスが見つかりません。スクリプト実行は行っていません。",
                ProcessId: abacusProcess.Id,
                Architecture: HostArchitecture,
                TargetArchitecture: abacusExecutablePath is null ? null : ReadPeArchitecture(abacusExecutablePath),
                AutomationServer: "FMPRO.Application (x86 probe missing)");
        }

        var exitCode = -1;
        try
        {
            using var probe = Process.Start(new ProcessStartInfo(probePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory,
            }) ?? throw new InvalidOperationException("x86 ActiveX診断プロセスを開始できませんでした。");
            if (!probe.WaitForExit(5000))
            {
                try
                {
                    probe.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // The probe may have exited between the timeout and Kill.
                }

                exitCode = -2;
            }
            else
            {
                exitCode = probe.ExitCode;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new LegacyHostMessage(
                "abacus-automation-inspected",
                requestId,
                Status: "automation-probe-failed",
                Message: $"x86 ActiveX診断プロセスを実行できませんでした: {exception.Message}",
                ProcessId: abacusProcess.Id,
                Architecture: HostArchitecture,
                TargetArchitecture: abacusExecutablePath is null ? null : ReadPeArchitecture(abacusExecutablePath),
                AutomationServer: "FMPRO.Application (x86 probe failed)");
        }

        var (status, message) = exitCode switch
        {
            0 => ("automation-active", "x86プローブでFMPRO.Applicationの実行中ActiveXインスタンスを確認しました。スクリプト実行は行っていません。"),
            10 => ("automation-unregistered", "x86プローブでもFMPRO.ApplicationのActiveX登録を確認できませんでした。スクリプト実行は行っていません。"),
            12 => ("automation-inactive", "FMPRO.Applicationはx86側で登録済みですが、実行中インスタンスを取得できませんでした。スクリプト実行は行っていません。"),
            -2 => ("automation-probe-timeout", "x86 ActiveX診断が5秒以内に終了しませんでした。スクリプト実行は行っていません。"),
            _ => ($"automation-probe-result-{exitCode}", $"x86 ActiveX診断が終了コード{exitCode}を返しました。スクリプト実行は行っていません。"),
        };

        return new LegacyHostMessage(
            "abacus-automation-inspected",
            requestId,
            Status: status,
            Message: message,
            ProcessId: abacusProcess.Id,
            Architecture: HostArchitecture,
            TargetArchitecture: abacusExecutablePath is null ? null : ReadPeArchitecture(abacusExecutablePath),
            AutomationServer: "FMPRO.Application (x86 probe)");
    }

    private static int GetNativeWindowDepth(IntPtr windowHandle, IntPtr rootHandle)
    {
        var depth = 1;
        var parent = GetParent(windowHandle);
        while (parent != IntPtr.Zero && parent != rootHandle && depth < 8)
        {
            depth++;
            parent = GetParent(parent);
        }

        return depth;
    }

    private static string ReadClassName(IntPtr windowHandle)
    {
        var buffer = new StringBuilder(81);
        _ = GetClassName(windowHandle, buffer, buffer.Capacity);
        return NormalizeAutomationText(buffer.ToString(), 80);
    }

    private static string ReadWindowTitle(IntPtr windowHandle)
    {
        var buffer = new StringBuilder(81);
        _ = GetWindowText(windowHandle, buffer, buffer.Capacity);
        return NormalizeAutomationText(buffer.ToString(), 80);
    }

    private static int GetAutomationDepth(AutomationElement element, AutomationElement root)
    {
        var depth = 0;
        var current = element;
        while (depth < 12)
        {
            current = TreeWalker.ControlViewWalker.GetParent(current);
            if (current is null || current.Equals(root))
            {
                break;
            }

            depth++;
        }

        return depth;
    }

    private static string NormalizeAutomationText(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = new string(value
            .Where(character => !char.IsControl(character))
            .ToArray())
            .Trim();
        return normalized.Length <= maximumLength ? normalized : $"{normalized[..maximumLength]}…";
    }

    private static int ToBoundedInt(double value) =>
        double.IsNaN(value) || double.IsInfinity(value)
            ? 0
            : (int)Math.Clamp(Math.Round(value), int.MinValue, int.MaxValue);

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

    [DllImport("user32.dll")]
    private static extern IntPtr GetMenu(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern int GetMenuItemCount(IntPtr menuHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetSubMenu(IntPtr menuHandle, int position);

    [DllImport("user32.dll")]
    private static extern uint GetMenuItemID(IntPtr menuHandle, int position);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMenuStringW")]
    private static extern int GetMenuString(
        IntPtr menuHandle,
        uint item,
        StringBuilder text,
        int maximumCharacters,
        uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetMenuState(IntPtr menuHandle, uint item, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    private delegate bool EnumWindowsCallback(IntPtr windowHandle, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(
        IntPtr parentWindowHandle,
        EnumWindowsCallback callback,
        IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassName(
        IntPtr windowHandle,
        StringBuilder className,
        int maximumCharacters);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowText(
        IntPtr windowHandle,
        StringBuilder title,
        int maximumCharacters);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern int GetDlgCtrlID(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr windowHandle);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr windowHandle, int index);

    [GeneratedRegex("^[A-Za-z0-9.-]{1,120}$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidPipeName();
}
