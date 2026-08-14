using System.Net.WebSockets;
using System.Text;
using Farm.Moonraker.Emulator.Domain;

namespace Farm.Moonraker.Emulator.Endpoints;

/// <summary>
/// Maps <c>/websocket</c>: a real WebSocket upgrade for the JSON-RPC 2.0 subscription
/// protocol, plus a plain HTTP POST fallback (used by the backend plugin for
/// <c>server.files.get_directory</c> when the WebSocket path is unavailable).
/// </summary>
public static class MoonrakerWebSocketEndpoint
{
    private const int MaxMessageBytes = 256 * 1024;

    public static IEndpointRouteBuilder MapMoonrakerWebSocket(this IEndpointRouteBuilder app)
    {
        app.Map("/websocket", async (HttpContext ctx, PrinterRegistry registry) =>
        {
            var printer = (PrinterAggregate)ctx.Items["printer"]!;

            if (ctx.WebSockets.IsWebSocketRequest)
            {
                await HandleWebSocketAsync(ctx, printer, registry);
                return;
            }

            if (HttpMethods.IsPost(ctx.Request.Method))
            {
                await HandleHttpFallbackAsync(ctx, printer, registry);
                return;
            }

            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        });

        return app;
    }

    private static async Task HandleHttpFallbackAsync(HttpContext ctx, PrinterAggregate printer, PrinterRegistry registry)
    {
        using var reader = new StreamReader(ctx.Request.Body);
        string body = await reader.ReadToEndAsync();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            RpcResult response = await RpcDispatcher.DispatchAsync(printer, registry, doc.RootElement, subscription: null);
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(response.Payload);
        }
        catch (JsonException)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        }
    }

    private static async Task HandleWebSocketAsync(HttpContext ctx, PrinterAggregate printer, PrinterRegistry registry)
    {
        using WebSocket socket = await ctx.WebSockets.AcceptWebSocketAsync();
        var subscription = new WsSubscription { Socket = socket };

        // Intentionally non-deterministic: this id is purely an internal dictionary key
        // for printer.Connections (removed again in the `finally` block below) and is
        // never surfaced in any Moonraker wire response or control-API payload, so it has
        // no observable determinism requirement.
        Guid connectionId = Guid.NewGuid();
        printer.Connections[connectionId] = subscription;

        try
        {
            byte[] buffer = new byte[16 * 1024];
            while (socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                bool tooLarge = false;
                do
                {
                    result = await socket.ReceiveAsync(buffer, ctx.RequestAborted);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, ctx.RequestAborted);
                        return;
                    }

                    if (!tooLarge && message.Length + result.Count <= MaxMessageBytes)
                    {
                        await message.WriteAsync(buffer.AsMemory(0, result.Count), ctx.RequestAborted);
                    }
                    else
                    {
                        tooLarge = true;
                    }
                }
                while (!result.EndOfMessage);

                if (tooLarge)
                {
                    await socket.CloseAsync(
                        WebSocketCloseStatus.MessageTooBig,
                        $"Maximum JSON-RPC message size is {MaxMessageBytes} bytes.",
                        ctx.RequestAborted);
                    return;
                }

                string requestText = Encoding.UTF8.GetString(message.ToArray());
                if (string.IsNullOrWhiteSpace(requestText))
                {
                    continue;
                }

                string responseText;
                bool shouldDisconnect = false;
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(requestText);
                    RpcResult dispatched = await RpcDispatcher.DispatchAsync(printer, registry, doc.RootElement, subscription);
                    responseText = dispatched.Payload;
                    shouldDisconnect = dispatched.ShouldDisconnect;
                }
                catch (JsonException)
                {
                    responseText = Json.MoonrakerJson.BuildRpcError(null, -32700, "Parse error");
                }

                await SendAsync(subscription, responseText, ctx.RequestAborted);

                if (shouldDisconnect)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Emulator fault: forced disconnect", ctx.RequestAborted);
                    return;
                }
            }
        }
        catch (WebSocketException)
        {
            // Client disconnected abruptly; nothing further to do.
        }
        catch (OperationCanceledException)
        {
            // Request aborted (e.g. test teardown); nothing further to do.
        }
        finally
        {
            printer.Connections.TryRemove(connectionId, out _);
        }
    }

    private static async Task SendAsync(WsSubscription subscription, string payload, CancellationToken ct)
    {
        await subscription.SendGate.WaitAsync(ct);
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            await subscription.Socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        finally
        {
            subscription.SendGate.Release();
        }
    }
}
