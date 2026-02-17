using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Farm.Infrastructure.Discovery;
using Farm.Infrastructure.Domain;
using Microsoft.Extensions.Logging;

namespace Farm.Backend.Plugin.FlashForge;

/// <summary>
/// Discovery probe for FlashForge printers using the proprietary TCP serial protocol.
/// Attempts TCP connection on known FlashForge ports (8899, 8080) and performs a
/// handshake (~M601 S1) followed by device info query (~M115) to identify the printer.
/// </summary>
public sealed partial class FlashForgeDiscoveryProbe : INetworkDiscoveryProbe
{
    private readonly ILogger<FlashForgeDiscoveryProbe> _logger;

    /// <summary>
    /// Ports to probe for FlashForge printers.
    /// 8899: Default FlashForge TCP port (most models).
    /// 8080: Used by some newer models (e.g., Adventurer 5X).
    /// </summary>
    private static readonly int[] ProbePorts = [8899, 8080];

    public FlashForgeDiscoveryProbe(ILogger<FlashForgeDiscoveryProbe> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string DisplayName => "FlashForge";

    /// <inheritdoc />
    public PrinterBackend Backend => PrinterBackend.FlashForge;

    /// <inheritdoc />
    public async Task<ProbeResult?> ProbeAsync(string ipAddress, int timeoutMs, CancellationToken cancellationToken)
    {
        foreach (int port in ProbePorts)
        {
            ProbeResult? result = await TryProbePortAsync(ipAddress, port, timeoutMs, cancellationToken).ConfigureAwait(false);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    private async Task<ProbeResult?> TryProbePortAsync(string ipAddress, int port, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);

            await client.ConnectAsync(ipAddress, port, timeoutCts.Token).ConfigureAwait(false);
            using NetworkStream stream = client.GetStream();

            // Handshake
            string handshakeResponse = await SendAndReadAsync(stream, "~M601 S1\n", timeoutCts.Token).ConfigureAwait(false);
            if (!handshakeResponse.Contains("ok", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // Query device info for identification
            string infoResponse = await SendAndReadAsync(stream, "~M115\n", timeoutCts.Token).ConfigureAwait(false);

            int confidence = 50; // Base confidence: TCP handshake succeeded
            string? name = null;
            string? model = null;
            string? manufacturer = "FlashForge";
            string reason = "FlashForge handshake succeeded";

            // Parse device info for higher confidence
            Match machineType = MachineTypeRegex().Match(infoResponse);
            if (machineType.Success)
            {
                model = machineType.Groups[1].Value.Trim();
                confidence = 75;
                reason = $"FlashForge M115 identified model: {model}";
            }

            Match machineName = MachineNameRegex().Match(infoResponse);
            if (machineName.Success)
            {
                name = machineName.Groups[1].Value.Trim();
                confidence = 100;
                reason = $"FlashForge M115 identified: {name} ({model ?? "unknown model"})";
            }

            // Fall back to a reasonable default name
            name ??= model ?? "FlashForge Printer";

            string serverUrl = $"http://{ipAddress}:{port}";

            DiscoveredPrinterDto printer = DiscoveredPrinterDto.FromProbe(
                ipAddress: ipAddress,
                serverUrl: serverUrl,
                name: name,
                backend: PrinterBackend.FlashForge,
                backendPort: port,
                manufacturer: manufacturer,
                model: model);

            _logger.LogDebug(
                "FlashForge probe on {Ip}:{Port} succeeded: {Name} (confidence {Confidence})",
                ipAddress, port, name, confidence);

            return new ProbeResult(printer, confidence, reason);
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or OperationCanceledException or IOException)
        {
            _logger.LogTrace("FlashForge probe on {Ip}:{Port} failed: {Message}", ipAddress, port, ex.Message);
            return null;
        }
    }

    private static async Task<string> SendAndReadAsync(NetworkStream stream, string data, CancellationToken ct)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(data);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        byte[] buffer = new byte[4096];
        var responseBuilder = new StringBuilder();

        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readCts.CancelAfter(TimeSpan.FromSeconds(5));

        while (!readCts.IsCancellationRequested)
        {
            int bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), readCts.Token).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            responseBuilder.Append(Encoding.ASCII.GetString(buffer, 0, bytesRead));
            if (responseBuilder.ToString().Contains("ok\n", StringComparison.Ordinal))
            {
                break;
            }
        }

        return responseBuilder.ToString();
    }

    [GeneratedRegex(@"Machine Type:\s*(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex MachineTypeRegex();

    [GeneratedRegex(@"Machine Name:\s*(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex MachineNameRegex();
}
