using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Farm.Backend.Plugin.Core;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers.Moonraker;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Telemetry;
using Farm.SignalR.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Farm.Backend.Plugin.Moonraker;

// Persistent state for a printer to avoid overwriting good values with nulls
internal sealed class PrinterState
{
    public double? X { get; set; }
    public double? Y { get; set; }
    public double? Z { get; set; }
    public double? HotendTemp { get; set; }
    public double? BedTemp { get; set; }
    public double? HotendTarget { get; set; }
    public double? BedTarget { get; set; }
    public string? State { get; set; }
    public double? Progress { get; set; }
    public string? JobName { get; set; }
    public string? HomedAxes { get; set; }
    public string? CameraStreamUrl { get; set; }
    public string? ThumbnailUrl { get; set; }
}

public sealed class MoonrakerSubscriptionService(
    IHubContext<PrinterHub> hub,
    IServiceScopeFactory scopeFactory,
    IUnifiedLoggingService logger,
    IHttpClientFactory httpClientFactory) : IHostedService, IDisposable
{
    private readonly IUnifiedLoggingService _logger = logger;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Guid, Task> _loops = new();
    private readonly ConcurrentDictionary<Guid, ConnectionMetrics> _connectionMetrics = new();

    // Persistent state tracking for each printer
    private readonly ConcurrentDictionary<Guid, PrinterState> _printerStates = new();
    private readonly ConcurrentDictionary<Guid, int> _parseErrorCounts = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _lastHttpPollTimes = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _lastStatusUpdateTimes = new();

    // Track polling strategy per printer based on Klippy state
    private readonly ConcurrentDictionary<Guid, PollingMode> _pollingModes = new();

    // Track Klippy ready state per printer (for IsOnline determination)
    private readonly ConcurrentDictionary<Guid, bool> _klippyReadyState = new();

    private enum PollingMode
    {
        WebSocketRealTime,  // Use WebSocket for real-time updates (normal operation)
        HttpPollingOnly,    // Use HTTP polling only (Klippy disconnected/shutdown)
        WebSocketWithFallback // Use WebSocket but ready to fallback (transition states)
    }
    private Task? _mainLoop;

    // Connection configuration constants
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    private const int MaxReconnectAttempts = 10;
    private const int MaxParseErrorsBeforeFallback = 5;
    private static readonly TimeSpan HttpPollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StaleConnectionThreshold = TimeSpan.FromSeconds(60); // Trigger fallback if no status updates for 60 seconds

    // Client identification for Moonraker
    private const string ClientName = "PrintFarmer";
    private const string ClientVersion = "1.0.0";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("MoonrakerSubscriptionService starting");
        _mainLoop = Task.Run(() => RunAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _cts.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed
        }

        if (_mainLoop != null)
        {
            await _mainLoop;
        }
    }

    public void Dispose()
    {
        _cts?.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("MoonrakerSubscriptionService run loop started");

        try
        {
            // Main loop will monitor printers and manage subscriptions
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(5000, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in MoonrakerSubscriptionService run loop");
        }
        finally
        {
            _logger.LogInformation("MoonrakerSubscriptionService run loop stopped");
        }
    }

    // Connection metrics tracking
    private sealed class ConnectionMetrics
    {
        public int ReconnectAttempts { get; set; }
        public DateTime LastReconnectAttempt { get; set; }
        public int ParseErrors { get; set; }
        public long BytesReceived { get; set; }
        public long MessagesReceived { get; set; }
    }
}
