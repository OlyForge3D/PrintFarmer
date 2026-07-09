using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Domain;

namespace Farm.Backend.Plugin.Moonraker;

public sealed class MoonrakerJsonRpcClient : IMoonrakerJsonRpcClient
{
    private static int nextRpcId;

    public async Task SendMethodAsync(Uri baseUrl, string method, PrinterCredential? credential, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        if (string.IsNullOrWhiteSpace(method))
        {
            throw new ArgumentException("JSON-RPC method is required.", nameof(method));
        }

        Uri websocketUri = BuildWebSocketUri(baseUrl, credential);
        using ClientWebSocket socket = new();
        if (!string.IsNullOrWhiteSpace(credential?.ApiKey))
        {
            socket.Options.SetRequestHeader("X-Api-Key", credential.ApiKey);
        }

        await socket.ConnectAsync(websocketUri, ct).ConfigureAwait(false);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0",
            method,
            id = Interlocked.Increment(ref nextRpcId)
        });

        await socket.SendAsync(payload, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

        byte[] buffer = new byte[4096];
        WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
        string response = Encoding.UTF8.GetString(buffer, 0, result.Count);
        if (response.Contains("\"error\"", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Moonraker JSON-RPC {method} returned an error.");
        }
    }

    private static Uri BuildWebSocketUri(Uri baseUrl, PrinterCredential? credential)
    {
        UriBuilder builder = new(baseUrl)
        {
            Scheme = baseUrl.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
            Path = "websocket",
            Query = string.Empty
        };

        if (!string.IsNullOrWhiteSpace(credential?.ApiKey))
        {
            // SnapCon uses token= on the U1 websocket. Keep X-Api-Key too for Moonraker variants.
            builder.Query = $"token={Uri.EscapeDataString(credential.ApiKey)}";
        }

        return builder.Uri;
    }
}
