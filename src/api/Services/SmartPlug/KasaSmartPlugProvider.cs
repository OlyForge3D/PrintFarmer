using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Services.SmartPlug;

/// <summary>
/// Smart plug provider for TP-Link Kasa devices using the local LAN API (no cloud required).
/// Targets the EM-enabled Kasa devices (KP115, EP25, etc.) that expose a JSON-over-TCP protocol
/// on port 9999, wrapped in a simple XOR obfuscation layer.
/// </summary>
public sealed class KasaSmartPlugProvider(
    ILogger<KasaSmartPlugProvider> logger) : ISmartPlugProvider
{
    private const int KasaTcpPort = 9999;

    public string ProviderType => "Kasa";

    /// <inheritdoc/>
    public async Task<PowerReading?> GetCurrentReadingAsync(string deviceAddress, CancellationToken ct)
    {
        try
        {
            byte[] response = await SendKasaCommandAsync(deviceAddress, KasaCommands.EmeterRealtime, ct);
            return ParseEmeterRealtime(response);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Kasa GetCurrentReading failed for {DeviceAddress}", deviceAddress);
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Kasa TestConnection failed for {DeviceAddress}", deviceAddress);
            return false;
        }
    }

    private static async Task<byte[]> SendKasaCommandAsync(string host, byte[] command, CancellationToken ct)
    {
        using System.Net.Sockets.TcpClient tcp = new();
        tcp.ReceiveTimeout = 5000;
        tcp.SendTimeout = 5000;

        await tcp.ConnectAsync(host, KasaTcpPort, ct);
        System.Net.Sockets.NetworkStream stream = tcp.GetStream();

        // Kasa protocol: 4-byte big-endian length prefix followed by XOR-obfuscated payload.
        byte[] encrypted = XorEncrypt(command);
        byte[] header = BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(encrypted.Length));
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(encrypted, ct);

        byte[] lenBuf = new byte[4];
        await ReadExactAsync(stream, lenBuf, ct);
        int len = System.Net.IPAddress.NetworkToHostOrder(BitConverter.ToInt32(lenBuf, 0));

        byte[] payload = new byte[len];
        await ReadExactAsync(stream, payload, ct);
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
            logger.LogWarning(ex, "Failed to parse Kasa emeter response");
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
}
