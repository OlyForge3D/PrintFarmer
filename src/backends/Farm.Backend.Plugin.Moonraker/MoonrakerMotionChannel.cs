using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Network;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Security;

namespace Farm.Backend.Plugin.Moonraker;

/// <summary>Creates a command-only connection pinned to the vetted destination, with normal TLS verification.</summary>
public sealed class MoonrakerMotionChannelFactory(IEgressGuard egress, ISensitiveDataProtector protector) : IMoonrakerMotionChannelFactory
{
    public async Task<IMoonrakerMotionChannel> ConnectAsync(Printer printer, CancellationToken ct)
    {
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
        EgressCheckResult target = await egress.CheckAsync(printer.BackendUrl, connectTimeout.Token);
        if (!target.IsAllowed || target.Uri is null || target.ResolvedAddress is null)
        {
            throw new InvalidOperationException("The command destination was not approved.");
        }

        Uri origin = target.Uri;
        IPAddress address = target.ResolvedAddress;
        string? apiKey = printer.ApiKey;
        if (apiKey?.StartsWith("CfDJ", StringComparison.Ordinal) == true && apiKey.Length > 100)
        {
            apiKey = protector.Unprotect(apiKey) ?? throw new InvalidOperationException("Printer credentials cannot be decrypted.");
        }

        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, origin.Port), token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        var invoker = new HttpMessageInvoker(handler, disposeHandler: true);
        var websocket = new ClientWebSocket();
        websocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
        websocket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(20);
        websocket.Options.HttpVersion = HttpVersion.Version11;
        websocket.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        Uri uri = BuildWebSocketUri(origin);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            websocket.Options.SetRequestHeader("X-Api-Key", apiKey);
        }

        try
        {
            // Keep the original hostname for Host/SNI and certificate validation; the
            // handler connects only to the exact IP approved above, without re-resolving.
            await websocket.ConnectAsync(uri, invoker, connectTimeout.Token);
            return new MoonrakerMotionChannel(websocket, invoker);
        }
        catch
        {
            websocket.Dispose();
            invoker.Dispose();
            throw;
        }
    }

    internal static Uri BuildWebSocketUri(Uri origin) =>
        new UriBuilder(origin)
        {
            Scheme = origin.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Path = "websocket",
            Query = string.Empty,
            UserName = string.Empty,
            Password = string.Empty,
            Fragment = string.Empty,
        }.Uri;
}

/// <summary>
/// One persistent receive loop routes exact JSON-RPC IDs. Command silence is not failure:
/// only write/connect deadlines and actual WebSocket PING/PONG liveness are bounded.
/// </summary>
public sealed class MoonrakerMotionChannel : IMoonrakerMotionChannel
{
    private const int MaxMessageBytes = 1024 * 1024;
    private readonly WebSocket socket;
    private readonly IDisposable? connection;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim writes = new(1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pending = new();
    private readonly Task receiver;
    private int disposed;

    /// <summary>Takes exclusive ownership of the socket and its optional HTTP connection.</summary>
    public MoonrakerMotionChannel(WebSocket socket, IDisposable? connection = null)
    {
        this.socket = socket;
        this.connection = connection;
        receiver = ReceiveAsync();
    }

    public async Task<bool> IsIdleAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        JsonElement result = await CallAsync(Guid.NewGuid(), "printer.objects.query",
            new { objects = new { print_stats = (string[]?)null, webhooks = (string[]?)null } }, timeout.Token);
        return IsIdle(result);
    }

    public async Task<PrinterStatusDto> ReadMotionStateAsync(Guid printerId, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        JsonElement result = await CallAsync(
            Guid.NewGuid(),
            "printer.objects.query",
            new
            {
                objects = new
                {
                    print_stats = (string[]?)null,
                    webhooks = (string[]?)null,
                    toolhead = new[] { "homed_axes" },
                    gcode_move = new[] { "gcode_position", "position" },
                },
            },
            timeout.Token);
        DateTime observed = DateTime.UtcNow;
        if (!IsIdle(result))
        {
            throw new PrinterControlException(409, "printer_busy", "The backend is not ready and idle.");
        }

        JsonElement status = result.GetProperty("status");
        if (!status.TryGetProperty("gcode_move", out JsonElement move) ||
            !TryVector(move, "gcode_position", out SafetyVector3Dto? position) ||
            !TryVector(move, "position", out SafetyVector3Dto? machinePosition) ||
            !status.TryGetProperty("toolhead", out JsonElement toolhead) ||
            !toolhead.TryGetProperty("homed_axes", out JsonElement axes) || axes.ValueKind != JsonValueKind.String)
        {
            throw new PrinterControlException(503, "printer_safety_evidence_unknown", "Fresh position, frame and homing evidence are required.");
        }

        // homing_origin alone omits G92 offsets. Both positions are from this exact
        // controller response; their difference is the effective G-code base frame.
        var offset = new SafetyVector3Dto(machinePosition!.X - position!.X, machinePosition.Y - position.Y, machinePosition.Z - position.Z);
        return new PrinterStatusDto(printerId, true, "Idle", X: position.X, Y: position.Y, Z: position.Z,
            SafetyTelemetry: PrinterSafetyTelemetryDto.Empty with
            {
                HomedAxes = new(
                    axes.GetString()!.Select(axis => char.ToLowerInvariant(axis).ToString()).Distinct(StringComparer.Ordinal).ToArray(),
                    observed, 15, "moonraker:toolhead.homed_axes"),
                CoordinateOriginOffsetMm = new(offset, observed, 15, "moonraker:gcode_move.position-gcode_position"),
            });
    }

    private static bool TryVector(JsonElement parent, string name, out SafetyVector3Dto? vector)
    {
        vector = null;
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out JsonElement values) ||
            values.ValueKind != JsonValueKind.Array || values.GetArrayLength() < 3 ||
            values[0].ValueKind != JsonValueKind.Number || values[1].ValueKind != JsonValueKind.Number || values[2].ValueKind != JsonValueKind.Number ||
            !values[0].TryGetDouble(out double x) || !values[1].TryGetDouble(out double y) || !values[2].TryGetDouble(out double z) ||
            !double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
        {
            return false;
        }

        vector = new(x, y, z);
        return true;
    }

    private static bool IsIdle(JsonElement result) =>
        result.TryGetProperty("status", out JsonElement status) &&
            status.TryGetProperty("webhooks", out JsonElement webhooks) &&
            webhooks.TryGetProperty("state", out JsonElement ready) && ready.GetString() == "ready" &&
            status.TryGetProperty("print_stats", out JsonElement print) &&
            print.TryGetProperty("state", out JsonElement state) &&
            state.GetString() is "standby" or "complete" or "cancelled" or "error";

    public async Task ExecuteAsync(Guid correlationId, string script, CancellationToken ct)
    {
        _ = await CallAsync(correlationId, "printer.gcode.script", new { script }, ct);
    }

    private async Task<JsonElement> CallAsync(Guid id, string method, object parameters, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        string key = id.ToString("D");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(key, completion))
        {
            throw new InvalidOperationException("Duplicate JSON-RPC correlation.");
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        try
        {
            await writes.WaitAsync(cancellation.Token);
            try
            {
                using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                writeTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id = key, method, @params = parameters });
                await socket.SendAsync(payload.AsMemory(), WebSocketMessageType.Text, true, writeTimeout.Token);
            }
            finally
            {
                writes.Release();
            }

            // This wait intentionally has no execution deadline (G28 can take minutes).
            return await completion.Task.WaitAsync(cancellation.Token);
        }
        finally
        {
            pending.TryRemove(key, out _);
        }
    }

    private async Task ReceiveAsync()
    {
        byte[] buffer = new byte[8192];
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                ValueWebSocketReceiveResult part;
                do
                {
                    part = await socket.ReceiveAsync(buffer.AsMemory(), lifetime.Token);
                    if (part.MessageType != WebSocketMessageType.Text)
                    {
                        throw new WebSocketException("Command connection closed or returned a non-text frame.");
                    }

                    if (message.Length + part.Count > MaxMessageBytes)
                    {
                        throw new WebSocketException("Command response exceeded the message bound.");
                    }

                    await message.WriteAsync(buffer.AsMemory(0, part.Count), lifetime.Token);
                }
                while (!part.EndOfMessage);

                using JsonDocument json = JsonDocument.Parse(message.ToArray());
                JsonElement root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("id", out JsonElement id) || id.ValueKind != JsonValueKind.String ||
                    !pending.TryGetValue(id.GetString()!, out TaskCompletionSource<JsonElement>? completion))
                {
                    continue;
                }

                if (root.TryGetProperty("error", out JsonElement error) && error.ValueKind != JsonValueKind.Null)
                {
                    // Do not expose arbitrary firmware/macro error text or call partial motion a rejection.
                    completion.TrySetException(new InvalidOperationException("Moonraker returned a command error."));
                }
                else if (root.TryGetProperty("result", out JsonElement result))
                {
                    completion.TrySetResult(result.Clone());
                }
                else
                {
                    completion.TrySetException(new InvalidOperationException("Moonraker returned an invalid response."));
                }
            }
        }
        catch (Exception)
        {
            foreach (TaskCompletionSource<JsonElement> completion in pending.Values)
            {
                completion.TrySetException(new WebSocketException("Command connection lost."));
            }

            await lifetime.CancelAsync();
        }
    }

    [SuppressMessage("Usage", "IDISP007:Don't dispose injected", Justification = "Constructor explicitly transfers exclusive transport ownership; this is not a DI-injected shared socket.")]
    [SuppressMessage("Usage", "VSTHRD003:Avoid awaiting foreign Tasks", Justification = "Joins this instance's background receive loop after cancellation and Abort; no synchronization context is required.")]
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await lifetime.CancelAsync();
        socket.Abort();
        await receiver;

        // Join a possible write before acknowledging sender isolation.
        await writes.WaitAsync();
        writes.Release();
        socket.Dispose();
        connection?.Dispose();
        lifetime.Dispose();
        writes.Dispose();
    }
}
