using Farm.Infrastructure;
using Microsoft.AspNetCore.SignalR.Client;

namespace PrinterDiscovery.Services;

/// <summary>
/// Broadcasts discovery progress to the API's SignalR hub.
/// Used for manual discovery with real-time progress updates.
/// </summary>
public interface IDiscoveryProgressBroadcaster
{
    /// <summary>
    /// Broadcast discovery progress to the API's SignalR hub.
    /// </summary>
    /// <param name="progress">The discovery progress data to broadcast.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    Task BroadcastProgressAsync(DiscoveryProgressDto progress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Broadcast when a printer is found during discovery.
    /// </summary>
    /// <param name="found">The discovered printer data to broadcast.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    Task BroadcastPrinterFoundAsync(DiscoveryPrinterFoundDto found, CancellationToken cancellationToken = default);

    /// <summary>
    /// Broadcast discovery completion to the API's SignalR hub.
    /// </summary>
    /// <param name="completed">The discovery completion data to broadcast.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    Task BroadcastCompletedAsync(DiscoveryCompletedDto completed, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensure the SignalR connection is established.
    /// </summary>
    Task EnsureConnectedAsync(CancellationToken cancellationToken = default);
}

public sealed class DiscoveryProgressBroadcaster : IDiscoveryProgressBroadcaster, IAsyncDisposable
{
    private readonly ILogger<DiscoveryProgressBroadcaster> _logger;
    private readonly string _hubUrl;
    private HubConnection? _hubConnection;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    public DiscoveryProgressBroadcaster(IConfiguration config, ILogger<DiscoveryProgressBroadcaster> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        string apiBaseUrl = config["Discovery:ApiBaseUrl"] ?? "http://api:5245";
        _hubUrl = $"{apiBaseUrl.TrimEnd('/')}/hubs/printers";
    }

    public async Task EnsureConnectedAsync(CancellationToken cancellationToken = default)
    {
        if (_hubConnection?.State == HubConnectionState.Connected)
        {
            return;
        }

        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            if (_hubConnection?.State == HubConnectionState.Connected)
            {
                return;
            }

            // Dispose old connection if exists
            if (_hubConnection != null)
            {
                await _hubConnection.DisposeAsync();
            }

            _hubConnection = new HubConnectionBuilder()
                .WithUrl(_hubUrl)
                .WithAutomaticReconnect()
                .Build();

            _hubConnection.Closed += async (error) =>
            {
                _logger.LogWarning(error, "[DISCOVERY-BROADCASTER] SignalR connection closed");
                await Task.CompletedTask;
            };

            _hubConnection.Reconnected += async (connectionId) =>
            {
                _logger.LogInformation("[DISCOVERY-BROADCASTER] SignalR reconnected with connectionId: {ConnectionId}", connectionId);
                await Task.CompletedTask;
            };

            await _hubConnection.StartAsync(cancellationToken);
            _logger.LogInformation("[DISCOVERY-BROADCASTER] Connected to SignalR hub at {Url}", _hubUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DISCOVERY-BROADCASTER] Failed to connect to SignalR hub at {Url}", _hubUrl);
            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task BroadcastProgressAsync(DiscoveryProgressDto progress, CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureConnectedAsync(cancellationToken);

            if (_hubConnection?.State == HubConnectionState.Connected)
            {
                // Invoke the hub method to broadcast to the discovery group
                await _hubConnection.InvokeAsync("BroadcastDiscoveryProgressAsync", progress, cancellationToken);
                _logger.LogDebug(
                    "[DISCOVERY-BROADCASTER] Broadcasted progress for session {SessionId}: {Percentage}%",
                    progress.SessionId, progress.ProgressPercentage);
            }
            else
            {
                _logger.LogWarning("[DISCOVERY-BROADCASTER] Cannot broadcast - not connected");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DISCOVERY-BROADCASTER] Failed to broadcast progress");
        }
    }

    public async Task BroadcastPrinterFoundAsync(DiscoveryPrinterFoundDto found, CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureConnectedAsync(cancellationToken);

            if (_hubConnection?.State == HubConnectionState.Connected)
            {
                await _hubConnection.InvokeAsync("BroadcastDiscoveryPrinterFoundAsync", found, cancellationToken);
                _logger.LogInformation(
                    "[DISCOVERY-BROADCASTER] Broadcasted printer found for session {SessionId}: {Name} at {Ip}",
                    found.SessionId, found.Printer.Name, found.Printer.ServerUrl);
            }
            else
            {
                _logger.LogWarning("[DISCOVERY-BROADCASTER] Cannot broadcast printer found - not connected");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DISCOVERY-BROADCASTER] Failed to broadcast printer found");
        }
    }

    public async Task BroadcastCompletedAsync(DiscoveryCompletedDto completed, CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureConnectedAsync(cancellationToken);

            if (_hubConnection?.State == HubConnectionState.Connected)
            {
                await _hubConnection.InvokeAsync("BroadcastDiscoveryCompletedAsync", completed, cancellationToken);
                _logger.LogInformation(
                    "[DISCOVERY-BROADCASTER] Broadcasted completion for session {SessionId}: {Found} printers found",
                    completed.SessionId, completed.TotalPrintersFound);
            }
            else
            {
                _logger.LogWarning("[DISCOVERY-BROADCASTER] Cannot broadcast completion - not connected");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DISCOVERY-BROADCASTER] Failed to broadcast completion");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_hubConnection != null)
        {
            await _hubConnection.DisposeAsync();
        }

        _connectionLock.Dispose();
    }
}
