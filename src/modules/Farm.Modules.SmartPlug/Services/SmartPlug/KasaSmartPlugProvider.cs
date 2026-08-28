using System.Text.Json;
using Farm.Infrastructure.Logging;
using Microsoft.Extensions.Logging;

namespace Farm.Modules.SmartPlug.Services.SmartPlug;

/// <summary>
/// Smart plug provider for TP-Link Kasa devices using the local LAN API (no cloud required).
/// Targets the EM-enabled Kasa devices (KP115, EP25, etc.) that expose a JSON-over-TCP protocol
/// on port 9999, wrapped in a simple XOR obfuscation layer.
/// </summary>
public sealed class KasaSmartPlugProvider : ISmartPlugProvider
{
    private const int KasaTcpPort = 9999;
    private const int MaxResponseBytes = 65_536; // 64 KB — no legitimate Kasa response is larger
    private static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger<KasaSmartPlugProvider> _logger;
    private readonly IKasaConnector _connector;
    private readonly TimeSpan _readTimeout;

    /// <summary>Production constructor: real TCP connections and the standard 5-second read timeout.</summary>
    public KasaSmartPlugProvider(ILogger<KasaSmartPlugProvider> logger)
        : this(logger, TcpKasaConnector.Instance, DefaultReadTimeout)
    {
    }

    /// <summary>
    /// Test seam: injects the connection factory and the read timeout so the timeout path can be
    /// exercised deterministically (a blocking stream plus a zero read timeout cancels immediately)
    /// without a real socket or wall-clock wait. Not used by production DI.
    /// </summary>
    internal KasaSmartPlugProvider(
        ILogger<KasaSmartPlugProvider> logger, IKasaConnector connector, TimeSpan readTimeout)
    {
        _logger = logger;
        _connector = connector;
        _readTimeout = readTimeout;
    }

    public string ProviderType => "Kasa";

    /// <inheritdoc/>
    public async Task<PowerReading?> GetCurrentReadingAsync(string deviceAddress, CancellationToken ct)
    {
        try
        {
            byte[] response = await SendKasaCommandAsync(deviceAddress, KasaCommands.EmeterRealtime, ct);
            return ParseEmeterRealtime(response);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Kasa GetCurrentReading timed out for {DeviceAddress}", LogSanitizer.Sanitize(deviceAddress));
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Kasa GetCurrentReading failed for {DeviceAddress}", LogSanitizer.Sanitize(deviceAddress));
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> TestConnectionAsync(string deviceAddress, CancellationToken ct)
    {
        try
        {
            byte[] response = await SendKasaCommandAsync(deviceAddress, KasaCommands.SysInfo, ct);
            return response.Length > 0;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Internal read timeout expired — treat as connection failure.
            _logger.LogDebug("Kasa TestConnection timed out for {DeviceAddress}", LogSanitizer.Sanitize(deviceAddress));
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Kasa TestConnection failed for {DeviceAddress}", LogSanitizer.Sanitize(deviceAddress));
            return false;
        }
    }

    internal async Task<byte[]> SendKasaCommandAsync(string host, byte[] command, CancellationToken ct)
    {
        (string hostname, int port) = ParseHostPort(host, KasaTcpPort);
        using System.IO.Stream stream = await _connector.ConnectAsync(hostname, port, ct);

        // Kasa protocol: 4-byte big-endian length prefix followed by XOR-obfuscated payload.
        byte[] encrypted = XorEncrypt(command);
        byte[] header = BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(encrypted.Length));
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(encrypted, ct);

        // Apply a read timeout to prevent a hung socket from blocking the polling thread.
        using CancellationTokenSource readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readCts.CancelAfter(_readTimeout);
        CancellationToken readCt = readCts.Token;

        byte[] lenBuf = new byte[4];
        await ReadExactAsync(stream, lenBuf, readCt);
        int len = System.Net.IPAddress.NetworkToHostOrder(BitConverter.ToInt32(lenBuf, 0));

        if (len <= 0 || len > MaxResponseBytes)
        {
            throw new InvalidOperationException(
                $"Kasa device returned invalid response length {len} (max allowed: {MaxResponseBytes})");
        }

        byte[] payload = new byte[len];
        await ReadExactAsync(stream, payload, readCt);
        return XorDecrypt(payload);
    }

    private static async Task ReadExactAsync(System.IO.Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (read == 0)
            {
                throw new System.IO.EndOfStreamException("Kasa device closed the connection");
            }

            offset += read;
        }
    }

    private static (string Host, int Port) ParseHostPort(string address, int defaultPort)
    {
        int lastColon = address.LastIndexOf(':');
        if (lastColon > 0 && int.TryParse(address.AsSpan(lastColon + 1), out int port))
        {
            return (address[..lastColon], port);
        }

        return (address, defaultPort);
    }

    private static byte[] XorEncrypt(byte[] data)
    {
        byte key = 171;
        byte[] result = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            result[i] = (byte)(data[i] ^ key);
            key = result[i];
        }

        return result;
    }

    private static byte[] XorDecrypt(byte[] data)
    {
        byte key = 171;
        byte[] result = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            result[i] = (byte)(data[i] ^ key);
            key = data[i];
        }

        return result;
    }

    private PowerReading? ParseEmeterRealtime(byte[] data)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(data);
            JsonElement root = doc.RootElement;

            // Navigate: {"emeter":{"get_realtime":{"voltage_mv":...,"current_ma":...,"power_mw":...,"total_wh":...}}}
            if (!root.TryGetProperty("emeter", out JsonElement emeter))
            {
                return null;
            }

            if (!emeter.TryGetProperty("get_realtime", out JsonElement rt))
            {
                return null;
            }

            double watts = 0;
            double? kwh = null;
            double? volts = null;
            double? amps = null;

            if (rt.TryGetProperty("power_mw", out JsonElement pw))
            {
                watts = pw.GetDouble() / 1000.0;
            }
            else if (rt.TryGetProperty("power", out JsonElement pw2))
            {
                watts = pw2.GetDouble();
            }

            if (rt.TryGetProperty("total_wh", out JsonElement tw))
            {
                kwh = tw.GetDouble() / 1000.0;
            }
            else if (rt.TryGetProperty("total", out JsonElement tw2))
            {
                kwh = tw2.GetDouble();
            }

            if (rt.TryGetProperty("voltage_mv", out JsonElement vm))
            {
                volts = vm.GetDouble() / 1000.0;
            }
            else if (rt.TryGetProperty("voltage", out JsonElement vm2))
            {
                volts = vm2.GetDouble();
            }

            if (rt.TryGetProperty("current_ma", out JsonElement cm))
            {
                amps = cm.GetDouble() / 1000.0;
            }
            else if (rt.TryGetProperty("current", out JsonElement cm2))
            {
                amps = cm2.GetDouble();
            }

            return new PowerReading(watts, kwh, volts, amps);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Kasa emeter response");
            return null;
        }
    }

    private static class KasaCommands
    {
        // {"emeter":{"get_realtime":{}}}
        public static readonly byte[] EmeterRealtime =
            """{"emeter":{"get_realtime":{}}}"""u8.ToArray();

        // {"system":{"get_sysinfo":{}}}
        public static readonly byte[] SysInfo =
            """{"system":{"get_sysinfo":{}}}"""u8.ToArray();
    }

    /// <summary>
    /// Establishes the byte stream to a Kasa device. The returned stream owns its transport and is
    /// disposed by the caller. The production implementation (<see cref="TcpKasaConnector"/>) opens a
    /// real TCP socket; tests inject a fake to exercise the read-timeout path without any wall-clock
    /// dependency or real socket.
    /// </summary>
    internal interface IKasaConnector
    {
        Task<System.IO.Stream> ConnectAsync(string host, int port, CancellationToken ct);
    }

    /// <summary>Default connector: a real loopback/LAN TCP socket on the Kasa port.</summary>
    private sealed class TcpKasaConnector : IKasaConnector
    {
        public static readonly TcpKasaConnector Instance = new();

        public async Task<System.IO.Stream> ConnectAsync(string host, int port, CancellationToken ct)
        {
            System.Net.Sockets.Socket socket = new(
                System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);

            try
            {
                await socket.ConnectAsync(host, port, ct);

                // The NetworkStream owns the socket, so disposing the stream closes the connection.
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }
}
