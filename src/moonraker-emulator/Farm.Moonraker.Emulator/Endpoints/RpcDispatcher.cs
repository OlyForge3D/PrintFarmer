using Farm.Moonraker.Emulator.Domain;
using Farm.Moonraker.Emulator.Json;

namespace Farm.Moonraker.Emulator.Endpoints;

/// <summary>The serialized JSON-RPC response payload, plus whether the caller should close the socket afterward (WsDisconnect fault).</summary>
public sealed record RpcResult(string Payload, bool ShouldDisconnect = false);

/// <summary>
/// Dispatches one JSON-RPC 2.0 request against a printer's live state. Shared between
/// the real <c>/websocket</c> WebSocket loop and the HTTP-POST-to-<c>/websocket</c>
/// fallback the backend plugin uses for <c>server.files.get_directory</c>.
/// </summary>
public static class RpcDispatcher
{
    public static async Task<RpcResult> DispatchAsync(
        PrinterAggregate printer,
        PrinterRegistry registry,
        JsonElement request,
        WsSubscription? subscription)
    {
        object? id = request.TryGetProperty("id", out JsonElement idEl)
            ? idEl.ValueKind switch
            {
                JsonValueKind.Number => idEl.GetInt64(),
                JsonValueKind.String => idEl.GetString(),
                _ => null,
            }
            : null;

        string method = request.TryGetProperty("method", out JsonElement methodEl) && methodEl.ValueKind == JsonValueKind.String
            ? methodEl.GetString() ?? string.Empty
            : string.Empty;

        bool disconnectAfter = false;
        FaultRule? rule = registry.Rules.MatchWebSocket(printer.Id, method);
        if (rule is not null)
        {
            if (rule.LatencyMs is > 0)
            {
                await Task.Delay(rule.LatencyMs.Value);
            }

            switch (rule.Effect)
            {
                case FaultEffect.RpcError:
                    return new RpcResult(MoonrakerJson.BuildRpcError(id, rule.RpcErrorCode ?? -32000, rule.RpcErrorMessage ?? "Injected fault"));
                case FaultEffect.MalformedJson:
                    return new RpcResult("{ this is not valid json ");
                case FaultEffect.KlippyUnavailable:
                    return new RpcResult(MoonrakerJson.BuildRpcError(id, -32000, "Klippy is not connected"));
                case FaultEffect.StaleNotifications when subscription is not null:
                    subscription.SuppressNotificationsUntil = printer.Clock.UtcNow.AddSeconds(rule.StaleSeconds ?? 60);
                    break;
                case FaultEffect.WsDisconnect:
                    disconnectAfter = true;
                    break;
                case FaultEffect.Latency:
                case FaultEffect.HttpStatus:
                default:
                    break;
            }
        }

        JsonElement paramsEl = request.TryGetProperty("params", out JsonElement p) ? p : default;

        string payload = method switch
        {
            "server.connection.identify" => MoonrakerJson.BuildRpcResult(id, new Dictionary<string, object?> { ["connection_id"] = 1 }),
            "server.info" => BuildServerInfo(printer, id),
            "printer.objects.list" => MoonrakerJson.BuildRpcResult(id, new Dictionary<string, object?> { ["objects"] = printer.BuildObjectsSnapshot().Keys.ToArray() }),
            "printer.objects.subscribe" => HandleSubscribe(printer, subscription, id, paramsEl),
            "printer.objects.query" => HandleQuery(printer, id, paramsEl),
            "camera.start_monitor" => HandleCameraMonitor(subscription, id, monitoring: true),
            "camera.stop_monitor" => HandleCameraMonitor(subscription, id, monitoring: false),
            "server.files.get_directory" => HandleGetDirectory(printer, id, paramsEl),
            string m when string.IsNullOrEmpty(m) => MoonrakerJson.BuildRpcError(id, -32600, "Invalid Request"),
            _ => MoonrakerJson.BuildRpcError(id, -32601, $"Method not found: {method}"),
        };

        return new RpcResult(payload, disconnectAfter);
    }

    private static string HandleSubscribe(PrinterAggregate printer, WsSubscription? subscription, object? id, JsonElement paramsEl)
    {
        Dictionary<string, string[]?> requested = ParseObjectsParam(paramsEl);
        if (subscription is not null)
        {
            subscription.Objects.Clear();
            foreach ((string name, string[]? fields) in requested)
            {
                subscription.Objects[name] = fields;
            }
        }

        Dictionary<string, object> snapshot = printer.BuildObjectsSnapshot();
        var status = new Dictionary<string, object?>(StringComparer.Ordinal);
        IEnumerable<string> names = requested.Count > 0 ? requested.Keys : snapshot.Keys;
        foreach (string name in names)
        {
            if (!snapshot.TryGetValue(name, out object? value))
            {
                continue;
            }

            status[name] = FilterFields(value, requested.GetValueOrDefault(name));
        }

        if (subscription is not null)
        {
            BroadcastService.CaptureBaseline(subscription, status);
        }

        return MoonrakerJson.BuildRpcResult(id, new Dictionary<string, object?>
        {
            ["status"] = status,
            ["eventtime"] = printer.Clock.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
        });
    }

    private static string BuildServerInfo(PrinterAggregate printer, object? id) =>
        MoonrakerJson.BuildRpcResult(id, new Dictionary<string, object?>
        {
            ["klippy_connected"] = printer.KlippyState != "disconnected",
            ["klippy_state"] = printer.KlippyState,
            ["moonraker_version"] = "v0.9.2-emulator",
        });

    private static string HandleQuery(PrinterAggregate printer, object? id, JsonElement paramsEl)
    {
        Dictionary<string, string[]?> requested = ParseObjectsParam(paramsEl);
        Dictionary<string, object> snapshot = printer.BuildObjectsSnapshot();
        var status = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach ((string name, string[]? fields) in requested)
        {
            if (snapshot.TryGetValue(name, out object? value))
            {
                status[name] = FilterFields(value, fields);
            }
        }

        return MoonrakerJson.BuildRpcResult(id, new Dictionary<string, object?>
        {
            ["status"] = status,
            ["eventtime"] = printer.Clock.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
        });
    }

    private static object? FilterFields(object? value, string[]? fields)
    {
        if (fields is null || value is not Dictionary<string, object?> all)
        {
            return value;
        }

        return fields.Where(all.ContainsKey).ToDictionary(f => f, f => all[f], StringComparer.Ordinal);
    }

    private static Dictionary<string, string[]?> ParseObjectsParam(JsonElement paramsEl)
    {
        var result = new Dictionary<string, string[]?>(StringComparer.Ordinal);
        if (paramsEl.ValueKind != JsonValueKind.Object || !paramsEl.TryGetProperty("objects", out JsonElement objects) ||
            objects.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (JsonProperty prop in objects.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Array)
            {
                result[prop.Name] = prop.Value.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToArray();
            }
            else
            {
                result[prop.Name] = null;
            }
        }

        return result;
    }

    private static string HandleCameraMonitor(WsSubscription? subscription, object? id, bool monitoring)
    {
        if (subscription is not null)
        {
            subscription.CameraMonitoring = monitoring;
        }

        return MoonrakerJson.BuildRpcResult(id, new Dictionary<string, object?>());
    }

    private static string HandleGetDirectory(PrinterAggregate printer, object? id, JsonElement paramsEl)
    {
        string path = paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("path", out JsonElement pathEl) && pathEl.ValueKind == JsonValueKind.String
            ? pathEl.GetString() ?? string.Empty
            : string.Empty;

        (string root, string relativePath) = SplitRootPath(path);
        (IReadOnlyList<string> dirs, IReadOnlyList<Domain.VirtualFile> files) = printer.Files.ListDirectory(root, relativePath);

        return MoonrakerJson.BuildRpcResult(id, new Dictionary<string, object?>
        {
            ["path"] = path,
            ["dirname"] = relativePath,
            ["modified"] = (double)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["size"] = 0,
            ["permissions"] = "rw",
            ["dirs"] = dirs.Select(d => new Dictionary<string, object?> { ["dirname"] = d, ["permissions"] = "rw" }).ToArray(),
            ["files"] = files.Select(f => new Dictionary<string, object?>
            {
                ["filename"] = f.Path,
                ["modified"] = (double)f.Modified.ToUnixTimeSeconds(),
                ["size"] = f.Content.LongLength,
                ["permissions"] = "rw",
            }).ToArray(),
        });
    }

    private static (string Root, string Path) SplitRootPath(string value)
    {
        string normalized = Domain.VirtualFileSystem.NormalizePath(value);
        int slash = normalized.IndexOf('/');
        return slash < 0 ? (string.IsNullOrEmpty(normalized) ? "gcodes" : normalized, string.Empty) : (normalized[..slash], normalized[(slash + 1)..]);
    }
}
