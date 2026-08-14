using System.Net.WebSockets;
using Farm.Moonraker.Emulator.Domain;
using Farm.Moonraker.Emulator.Json;

namespace Farm.Moonraker.Emulator.Endpoints;

/// <summary>
/// Sends <c>notify_status_update</c> / <c>notify_klippy_*</c> broadcasts to every
/// WebSocket subscriber of a printer whenever its state changes (print control,
/// gcode execution, scenario switch, or virtual-time advance).
/// </summary>
public static class BroadcastService
{
    public static async Task NotifyStatusUpdateAsync(PrinterAggregate printer)
    {
        Dictionary<string, object> snapshot = printer.BuildObjectsSnapshot();
        double eventTime = printer.Clock.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

        foreach (WsSubscription sub in printer.Connections.Values.ToArray())
        {
            if (sub.Objects.Count == 0)
            {
                continue;
            }

            if (sub.SuppressNotificationsUntil is { } until && printer.Clock.UtcNow < until)
            {
                continue;
            }

            var delta = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach ((string name, string[]? fields) in sub.Objects)
            {
                if (!snapshot.TryGetValue(name, out object? value))
                {
                    continue;
                }

                if (value is not Dictionary<string, object?> allFields)
                {
                    if (RecordChangedValue(sub, name, "$value", value))
                    {
                        delta[name] = value;
                    }

                    continue;
                }

                IEnumerable<string> requestedFields = fields is null ? allFields.Keys : fields.Where(allFields.ContainsKey);
                var changedFields = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (string field in requestedFields)
                {
                    object? fieldValue = allFields[field];
                    if (RecordChangedValue(sub, name, field, fieldValue))
                    {
                        changedFields[field] = fieldValue;
                    }
                }

                if (changedFields.Count > 0)
                {
                    delta[name] = changedFields;
                }
            }

            if (delta.Count == 0)
            {
                continue;
            }

            string payload = MoonrakerJson.BuildNotification("notify_status_update", new object?[] { delta, eventTime });
            await SendAsync(sub, payload);
        }
    }

    public static void CaptureBaseline(WsSubscription subscription, IReadOnlyDictionary<string, object?> status)
    {
        lock (subscription.LastFieldValues)
        {
            subscription.LastFieldValues.Clear();
            foreach ((string objectName, object? value) in status)
            {
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                if (value is Dictionary<string, object?> fields)
                {
                    foreach ((string field, object? fieldValue) in fields)
                    {
                        values[field] = SerializeValue(fieldValue);
                    }
                }
                else
                {
                    values["$value"] = SerializeValue(value);
                }

                subscription.LastFieldValues[objectName] = values;
            }
        }
    }

    public static async Task NotifyKlippyAsync(PrinterAggregate printer, string method)
    {
        string payload = MoonrakerJson.BuildNotification(method, Array.Empty<object>());
        foreach (WsSubscription sub in printer.Connections.Values.ToArray())
        {
            await SendAsync(sub, payload);
        }
    }

    /// <summary>
    /// Compares a printer's Klippy state against the value captured before some mutation
    /// (scenario switch, or a gcode command such as <c>M112</c>/<c>FIRMWARE_RESTART</c>
    /// that changes it directly) and, if it changed, sends the matching
    /// <c>notify_klippy_ready</c> / <c>notify_klippy_shutdown</c> / <c>notify_klippy_disconnected</c>
    /// broadcast before always sending the trailing <c>notify_status_update</c>. Shared by
    /// every endpoint that can move Klippy's connection state so the broadcast behavior is
    /// identical regardless of which one triggered the transition.
    /// </summary>
    public static async Task NotifyKlippyTransitionIfChangedAsync(PrinterAggregate printer, string previousKlippyState)
    {
        if (previousKlippyState != printer.KlippyState)
        {
            string method = printer.KlippyState switch
            {
                "ready" => "notify_klippy_ready",
                "shutdown" => "notify_klippy_shutdown",
                _ => "notify_klippy_disconnected",
            };
            await NotifyKlippyAsync(printer, method);
        }

        await NotifyStatusUpdateAsync(printer);
    }

    private static async Task SendAsync(WsSubscription sub, string payload)
    {
        if (sub.Socket.State != WebSocketState.Open)
        {
            return;
        }

        await sub.SendGate.WaitAsync();
        try
        {
            if (sub.Socket.State != WebSocketState.Open)
            {
                return;
            }

            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(payload);
            await sub.Socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        }
        catch (WebSocketException)
        {
            // Best-effort: a broken/closing socket simply misses this notification.
        }
        finally
        {
            sub.SendGate.Release();
        }
    }

    private static bool RecordChangedValue(WsSubscription subscription, string objectName, string field, object? value)
    {
        string serialized = SerializeValue(value);
        lock (subscription.LastFieldValues)
        {
            if (!subscription.LastFieldValues.TryGetValue(objectName, out Dictionary<string, string>? values))
            {
                values = new Dictionary<string, string>(StringComparer.Ordinal);
                subscription.LastFieldValues[objectName] = values;
            }

            if (values.TryGetValue(field, out string? previous) &&
                string.Equals(previous, serialized, StringComparison.Ordinal))
            {
                return false;
            }

            values[field] = serialized;
            return true;
        }
    }

    private static string SerializeValue(object? value) =>
        System.Text.Json.JsonSerializer.Serialize(value, MoonrakerJson.Options);
}
