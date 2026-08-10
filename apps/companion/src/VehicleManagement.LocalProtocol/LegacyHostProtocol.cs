using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VehicleManagement.LocalProtocol;

public static class LegacyHostProtocol
{
    public const int Version = 1;
    public const int MaximumMessageBytes = 16 * 1024;
    public const string PipeEnvironmentVariable = "VEHICLE_MANAGEMENT_LEGACY_HOST_PIPE";
    public const string TokenEnvironmentVariable = "VEHICLE_MANAGEMENT_LEGACY_HOST_TOKEN";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task WriteAsync(
        StreamWriter writer,
        LegacyHostMessage message,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(message, SerializerOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaximumMessageBytes)
        {
            throw new InvalidDataException("LegacyHost message exceeds the allowed size.");
        }

        await writer.WriteLineAsync(json.AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }

    public static async Task<LegacyHostMessage> ReadAsync(
        StreamReader reader,
        CancellationToken cancellationToken = default)
    {
        var json = await reader.ReadLineAsync(cancellationToken)
            ?? throw new EndOfStreamException("LegacyHost closed the connection.");

        if (Encoding.UTF8.GetByteCount(json) > MaximumMessageBytes)
        {
            throw new InvalidDataException("LegacyHost message exceeds the allowed size.");
        }

        return JsonSerializer.Deserialize<LegacyHostMessage>(json, SerializerOptions)
            ?? throw new InvalidDataException("LegacyHost returned an empty message.");
    }
}

public sealed record LegacyHostMessage(
    string Type,
    string RequestId,
    int ProtocolVersion = LegacyHostProtocol.Version,
    string? Token = null,
    string? Status = null,
    string? Message = null,
    int? ProcessId = null,
    string? Architecture = null);
