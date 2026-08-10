using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using VehicleManagement.LocalProtocol;

namespace VehicleManagement.LegacyHost;

internal static partial class Program
{
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
                    Architecture: RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()));

            while (pipe.IsConnected)
            {
                var request = await LegacyHostProtocol.ReadAsync(reader);
                switch (request.Type)
                {
                    case "ping":
                        await LegacyHostProtocol.WriteAsync(
                            writer,
                            new LegacyHostMessage(
                                "pong",
                                request.RequestId,
                                Status: "connected",
                                ProcessId: Environment.ProcessId,
                                Architecture: RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()));
                        break;

                    case "shutdown":
                        await LegacyHostProtocol.WriteAsync(
                            writer,
                            new LegacyHostMessage("stopping", request.RequestId, Status: "stopping"));
                        return 0;

                    default:
                        await LegacyHostProtocol.WriteAsync(
                            writer,
                            new LegacyHostMessage("error", request.RequestId, Status: "unsupported", Message: "Unsupported request."));
                        break;
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

    [GeneratedRegex("^[A-Za-z0-9.-]{1,120}$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidPipeName();
}
