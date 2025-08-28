using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Farm.Web.Server.Data;
using Farm.Web.Server.Domain;
using Farm.Web.Server.Hubs;
using Farm.Web.Shared;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Server.Services;

public class MoonrakerSubscriptionService(IHubContext<PrinterHub> hub, IServiceScopeFactory scopeFactory, MoonrakerClient moonrakerClient, ILogger<MoonrakerSubscriptionService> logger) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Guid, Task> _loops = new();
    private Task? _mainLoop;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _mainLoop = Task.Run(() => RunAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Signal cancellation to background loops
        _cts.Cancel();
        var tasks = new List<Task>(_loops.Values);
        if (_mainLoop is not null) tasks.Add(_mainLoop);

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
        catch (AggregateException aex) when (aex.InnerExceptions.All(e => e is OperationCanceledException))
        {
            // Also fine during shutdown
        }
        catch (Exception ex)
        {
            // Don't fail stop on background task errors
            logger.LogDebug(ex, "Ignoring background task error during StopAsync");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                // Only subscribe to Moonraker-backed printers (Backend == 0)
                var printers = await db.Printers.AsNoTracking()
                    .Where(p => p.Backend == 0)
                    .ToListAsync(ct);
                foreach (var p in printers)
                {
                    _ = _loops.GetOrAdd(p.Id, _ => Task.Run(() => SubscribePrinterLoop(p, ct), ct));
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error enumerating printers for subscription");
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private static Uri BuildWsUri(string httpBase)
    {
        if (string.IsNullOrWhiteSpace(httpBase)) throw new ArgumentException("Missing base URL", nameof(httpBase));
        var trimmed = httpBase.TrimEnd('/');
        if (!trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            trimmed = "http://" + trimmed;
        var ub = new UriBuilder(trimmed);
        if (ub.Port == -1) ub.Port = 7125;
        ub.Scheme = ub.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        ub.Path = "/websocket";
        return ub.Uri;
    }

    private async Task SubscribePrinterLoop(Printer printer, CancellationToken ct)
    {
        var id = printer.Id;
        while (!ct.IsCancellationRequested)
        {
            ClientWebSocket? ws = null;
            try
            {
                // Re-check backend on each iteration in case it changed
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var current = await db.Printers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
                    if (current is null)
                    {
                        // Printer removed; stop loop
                        return;
                    }
                    if (current.Backend != 0)
                    {
                        // Not Moonraker anymore; back off and retry later without connecting
                        await Task.Delay(TimeSpan.FromSeconds(10), ct);
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Backend check failed for printer {Printer}", printer.Name);
                }
                var uri = BuildWsUri(printer.ServerUrl);
                ws = new ClientWebSocket();
                await ws.ConnectAsync(uri, ct);

                // Subscribe to objects of interest
                var sub = new
                {
                    jsonrpc = "2.0",
                    method = "printer.objects.subscribe",
                    @params = new
                    {
                        objects = new Dictionary<string, object>
                        {
                            ["toolhead"] = new { position = Array.Empty<object>() },
                            ["display_status"] = new[] { "progress" },
                            ["print_stats"] = new[] { "state", "filename" },
                            ["extruder"] = new[] { "temperature", "target" },
                            ["heater_bed"] = new[] { "temperature", "target" },
                        }
                    },
                    id = 1
                };
                var subJson = JsonSerializer.Serialize(sub);
                await ws.SendAsync(Encoding.UTF8.GetBytes(subJson), WebSocketMessageType.Text, endOfMessage: true, ct);

                var buffer = new byte[64 * 1024];
                var sb = new StringBuilder();
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    sb.Clear();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", ct);
                            break;
                        }
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    } while (!result.EndOfMessage);

                    if (sb.Length == 0) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(sb.ToString());
                        var root = doc.RootElement;
                        // Moonraker sends {"method":"notify_status_update","params":[{"toolhead":{"position":[x,y,z,...]}...}]}
                        if (root.TryGetProperty("method", out var m) && m.GetString() == "notify_status_update" &&
                            root.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Array && p.GetArrayLength() > 0)
                        {
                            var statusObj = p[0];
                            double? x = null, y = null, z = null; double? progress = null; string? state = null; string? jobName = null;
                            double? hotend = null, bed = null, hotendTarget = null, bedTarget = null;

                            if (statusObj.TryGetProperty("toolhead", out var th) && th.TryGetProperty("position", out var pos) && pos.ValueKind == JsonValueKind.Array && pos.GetArrayLength() >= 3)
                            {
                                try { x = pos[0].GetDouble(); } catch { }
                                try { y = pos[1].GetDouble(); } catch { }
                                try { z = pos[2].GetDouble(); } catch { }
                            }
                            if (statusObj.TryGetProperty("display_status", out var ds))
                            {
                                if (ds.TryGetProperty("progress", out var prog))
                                {
                                    try
                                    {
                                        var pv = prog.GetDouble();
                                        progress = pv > 1 ? pv : pv * 100.0;
                                    }
                                    catch { }
                                }
                            }
                            if (statusObj.TryGetProperty("print_stats", out var ps))
                            {
                                if (ps.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.String) state = st.GetString();
                                if (ps.TryGetProperty("filename", out var fn) && fn.ValueKind == JsonValueKind.String) jobName = fn.GetString();
                            }
                            if (statusObj.TryGetProperty("extruder", out var ex))
                            {
                                if (ex.TryGetProperty("temperature", out var t) && t.ValueKind is JsonValueKind.Number) { try { hotend = t.GetDouble(); } catch { } }
                                if (ex.TryGetProperty("target", out var tt) && tt.ValueKind is JsonValueKind.Number) { try { hotendTarget = tt.GetDouble(); } catch { } }
                            }
                            if (statusObj.TryGetProperty("heater_bed", out var hb))
                            {
                                if (hb.TryGetProperty("temperature", out var t) && t.ValueKind is JsonValueKind.Number) { try { bed = t.GetDouble(); } catch { } }
                                if (hb.TryGetProperty("target", out var tt) && tt.ValueKind is JsonValueKind.Number) { try { bedTarget = tt.GetDouble(); } catch { } }
                            }

                            var spoolInfo = await GetSpoolInfoAsync(printer.ServerUrl, ct);
                            var update = new PrinterStatusUpdate(id, true, state, progress, jobName, ThumbnailUrl: null, CameraStreamUrl: null, X: x, Y: y, Z: z, HotendTemp: hotend, BedTemp: bed, HotendTarget: hotendTarget, BedTarget: bedTarget, SpoolInfo: spoolInfo);
                            await hub.Clients.All.SendAsync("PrinterUpdated", update, ct);
                        }
                        // Also handle the subscribe acknowledgement which carries current state: { id: 1, result: { status: { ... } } }
                        else if (root.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.Object && res.TryGetProperty("status", out var statusObj))
                        {
                            double? x = null, y = null, z = null; double? progress = null; string? state = null; string? jobName = null;
                            double? hotend = null, bed = null, hotendTarget = null, bedTarget = null;

                            if (statusObj.TryGetProperty("toolhead", out var th) && th.TryGetProperty("position", out var pos) && pos.ValueKind == JsonValueKind.Array && pos.GetArrayLength() >= 3)
                            {
                                try { x = pos[0].GetDouble(); } catch { }
                                try { y = pos[1].GetDouble(); } catch { }
                                try { z = pos[2].GetDouble(); } catch { }
                            }
                            if (statusObj.TryGetProperty("display_status", out var ds))
                            {
                                if (ds.TryGetProperty("progress", out var prog))
                                {
                                    try { var pv = prog.GetDouble(); progress = pv > 1 ? pv : pv * 100.0; } catch { }
                                }
                            }
                            if (statusObj.TryGetProperty("print_stats", out var ps))
                            {
                                if (ps.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.String) state = st.GetString();
                                if (ps.TryGetProperty("filename", out var fn) && fn.ValueKind == JsonValueKind.String) jobName = fn.GetString();
                            }
                            if (statusObj.TryGetProperty("extruder", out var ex))
                            {
                                if (ex.TryGetProperty("temperature", out var t) && t.ValueKind is JsonValueKind.Number) { try { hotend = t.GetDouble(); } catch { } }
                                if (ex.TryGetProperty("target", out var tt) && tt.ValueKind is JsonValueKind.Number) { try { hotendTarget = tt.GetDouble(); } catch { } }
                            }
                            if (statusObj.TryGetProperty("heater_bed", out var hb))
                            {
                                if (hb.TryGetProperty("temperature", out var t) && t.ValueKind is JsonValueKind.Number) { try { bed = t.GetDouble(); } catch { } }
                                if (hb.TryGetProperty("target", out var tt) && tt.ValueKind is JsonValueKind.Number) { try { bedTarget = tt.GetDouble(); } catch { } }
                            }

                            var spoolInfo = await GetSpoolInfoAsync(printer.ServerUrl, ct);
                            var update = new PrinterStatusUpdate(id, true, state, progress, jobName, null, null, x, y, z, hotend, bed, hotendTarget, bedTarget, SpoolInfo: spoolInfo);
                            await hub.Clients.All.SendAsync("PrinterUpdated", update, ct);
                        }
                        else if (root.TryGetProperty("method", out var m2) && m2.GetString() == "notify_klippy_disconnected")
                        {
                            var update = new PrinterStatusUpdate(id, false, "Offline", null, null, null, null, null, null, null, null, null, null, null, SpoolInfo: null);
                            await hub.Clients.All.SendAsync("PrinterUpdated", update, ct);
                        }
                    }
                    catch
                    {
                        // ignore parse errors
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Graceful shutdown
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Moonraker WS loop error for printer {Printer}", printer.Name);
            }
            finally
            {
                try { ws?.Dispose(); } catch { }
            }

            // Backoff before reconnect
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    // Helper method to get spool information for Moonraker printers
    private async Task<PrinterSpoolInfoDto?> GetSpoolInfoAsync(string serverUrl, CancellationToken ct)
    {
        try
        {
            // Get the active spool ID from Moonraker
            var activeSpoolId = await moonrakerClient.GetSpoolmanActiveSpoolAsync(serverUrl, ct);
            if (activeSpoolId == null)
            {
                return new PrinterSpoolInfoDto(HasActiveSpool: false);
            }

            // Get spool details from Spoolman via Moonraker
            var spoolDetailsJson = await moonrakerClient.GetSpoolmanSpoolByIdAsync(serverUrl, activeSpoolId.Value, ct);
            if (string.IsNullOrWhiteSpace(spoolDetailsJson))
            {
                return new PrinterSpoolInfoDto(
                    HasActiveSpool: true,
                    ActiveSpoolId: activeSpoolId
                );
            }

            // Parse the JSON response to extract spool information
            try
            {
                using var doc = JsonDocument.Parse(spoolDetailsJson);
                var root = doc.RootElement;
                
                var spoolName = root.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                var material = root.TryGetProperty("material", out var matEl) ? matEl.GetString() : null;
                var colorHex = root.TryGetProperty("color_hex", out var colorEl) ? colorEl.GetString() : null;
                var remainingWeight = root.TryGetProperty("remaining_weight", out var weightEl) && weightEl.ValueKind == JsonValueKind.Number 
                    ? weightEl.GetDouble() : (double?)null;
                
                // Check if filament information is nested
                string? filamentName = null;
                string? vendor = null;
                if (root.TryGetProperty("filament", out var filamentEl) && filamentEl.ValueKind == JsonValueKind.Object)
                {
                    filamentName = filamentEl.TryGetProperty("name", out var fnameEl) ? fnameEl.GetString() : null;
                    if (filamentEl.TryGetProperty("vendor", out var vendorEl) && vendorEl.ValueKind == JsonValueKind.Object)
                    {
                        vendor = vendorEl.TryGetProperty("name", out var vNameEl) ? vNameEl.GetString() : null;
                    }
                }

                return new PrinterSpoolInfoDto(
                    HasActiveSpool: true,
                    ActiveSpoolId: activeSpoolId,
                    SpoolName: spoolName,
                    Material: material,
                    ColorHex: colorHex,
                    FilamentName: filamentName,
                    Vendor: vendor,
                    RemainingWeightG: remainingWeight,
                    SpoolInUse: true
                );
            }
            catch
            {
                // If JSON parsing fails, return basic info
                return new PrinterSpoolInfoDto(
                    HasActiveSpool: true,
                    ActiveSpoolId: activeSpoolId
                );
            }
        }
        catch
        {
            // If any Spoolman operations fail, just return no spool info
            return new PrinterSpoolInfoDto(HasActiveSpool: false);
        }
    }
}
