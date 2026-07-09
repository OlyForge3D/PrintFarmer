using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Domain;

namespace Farm.Backend.Plugin.Moonraker;

public sealed class MoonrakerJsonRpcClient : IMoonrakerJsonRpcClient
{
    private const int MaxJsonRpcMessagesToRead = 8;
    private const int MaxJsonRpcMessageBytes = 64 * 1024;

    private static int nextRpcId;

    public async Task SendMethodAsync(Uri baseUrl, string method, PrinterCredential? credential, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        if (string.IsNullOrWhiteSpace(method))
        {
            throw new ArgumentException("JSON-RPC method is required.", nameof(method));
        }

        bool requestSent = false;
        try
        {
            Uri websocketUri = BuildWebSocketUri(baseUrl, credential);
            using ClientWebSocket socket = new();
            if (!string.IsNullOrWhiteSpace(credential?.ApiKey))
            {
                socket.Options.SetRequestHeader("X-Api-Key", credential.ApiKey);
            }

            await socket.ConnectAsync(websocketUri, ct).ConfigureAwait(false);
            int requestId = Interlocked.Increment(ref nextRpcId);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                jsonrpc = "2.0",
                method,
                id = requestId
            });

            // Hardware-inferred for stock Snapmaker U1 (#685): SnapCon wakes the monitor by sending
            // camera.start_monitor/camera.stop_monitor to Moonraker's /websocket endpoint.
            await socket.SendAsync(payload, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            requestSent = true;

            for (int i = 0; i < MaxJsonRpcMessagesToRead; i++)
            {
                string response = await ReceiveTextMessageAsync(socket, ct).ConfigureAwait(false);
                if (!TryGetMatchingResponse(response, requestId, out bool hasError))
                {
                    continue;
                }

                if (hasError)
                {
                    throw new MoonrakerJsonRpcException($"Moonraker JSON-RPC {method} returned an error.", requestSent);
                }

                return;
            }

            throw new MoonrakerJsonRpcException($"Moonraker JSON-RPC {method} reply was not received.", requestSent);
        }
        catch (MoonrakerJsonRpcException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new MoonrakerJsonRpcException($"Moonraker JSON-RPC {method} failed.", requestSent, ex);
        }
    }

    private static async Task<string> ReceiveTextMessageAsync(ClientWebSocket socket, CancellationToken ct)
    {
        byte[] buffer = new byte[4096];
        using MemoryStream message = new();
        WebSocketReceiveResult result;

        do
        {
            result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException("Moonraker websocket closed before the JSON-RPC reply was received.");
            }

            await message.WriteAsync(buffer.AsMemory(0, result.Count), ct).ConfigureAwait(false);
            if (message.Length > MaxJsonRpcMessageBytes)
            {
                throw new InvalidOperationException("Moonraker JSON-RPC reply exceeded the maximum supported size.");
            }
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(message.ToArray());
    }

    private static bool TryGetMatchingResponse(string response, int requestId, out bool hasError)
    {
        hasError = false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(response);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("id", out JsonElement idElement) ||
                idElement.ValueKind != JsonValueKind.Number ||
                idElement.GetInt32() != requestId)
            {
                return false;
            }

            hasError = root.TryGetProperty("error", out JsonElement errorElement) &&
                       errorElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static Uri BuildWebSocketUri(Uri baseUrl, PrinterCredential? credential)
    {
        UriBuilder builder = new(baseUrl)
        {
            // Hardware-inferred for stock U1 (#685): LAN cleartext ws://<ip>/websocket is expected.
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

public sealed class MoonrakerJsonRpcException : Exception
{
    public MoonrakerJsonRpcException()
    {
    }

    public MoonrakerJsonRpcException(string message)
        : base(message)
    {
    }

    public MoonrakerJsonRpcException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public MoonrakerJsonRpcException(string message, bool requestSent, Exception? innerException = null)
        : base(message, innerException)
    {
        RequestSent = requestSent;
    }

    public bool RequestSent { get; }
}
