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
