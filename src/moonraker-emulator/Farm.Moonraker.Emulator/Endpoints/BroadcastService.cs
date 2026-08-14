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

            if (sub.SuppressNotificationsUntil is { } until && DateTimeOffset.UtcNow < until)
            {
                continue;
            }

            var filtered = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach ((string name, string[]? fields) in sub.Objects)
            {
                if (!snapshot.TryGetValue(name, out object? value))
                {
                    continue;
                }

                if (fields is null || value is not Dictionary<string, object?> allFields)
                {
                    filtered[name] = value;
                    continue;
                }

                filtered[name] = fields
                    .Where(allFields.ContainsKey)
                    .ToDictionary(f => f, f => allFields[f], StringComparer.Ordinal);
            }

            string payload = MoonrakerJson.BuildNotification("notify_status_update", new object?[] { filtered, eventTime });
            await SendAsync(sub, payload);
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
}
