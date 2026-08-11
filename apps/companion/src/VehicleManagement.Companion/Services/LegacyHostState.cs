using VehicleManagement.LocalProtocol;

namespace VehicleManagement.Companion.Services;

public enum LegacyHostState
{
    Stopped,
    Starting,
    Connected,
    Stopping,
    Failed,
}

public sealed record LegacyHostSnapshot(
    LegacyHostState State,
    int? ProcessId = null,
    string? Architecture = null,
    string? Detail = null);

public sealed record AbacusRuntimeSnapshot(
    bool IsRecognized,
    string Status,
    string Message,
    int? ProcessId = null,
    string? HostArchitecture = null,
    string? TargetArchitecture = null,
    long? WindowHandle = null,
    string? WindowTitle = null,
    int? AutomationElementCount = null,
    IReadOnlyList<LegacyAutomationElementInfo>? AutomationElements = null,
    IReadOnlyList<LegacyMenuItemInfo>? MenuItems = null,
    IReadOnlyList<LegacyNativeWindowInfo>? NativeWindows = null)
{
    public bool IsRunning =>
        ProcessId.HasValue &&
        Status is not ("not-running" or "closed" or "abacus-exited" or "abacus-not-running");
}
