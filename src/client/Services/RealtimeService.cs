using System.Collections.Concurrent;
using Farm.Web.Shared;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;

namespace Farm.Web.Client;

public class RealtimeService : IAsyncDisposable
{
    private readonly NavigationManager _nav;
    private readonly IConfiguration _config;
    private HubConnection? _hub;
    private readonly ConcurrentDictionary<Guid, Action<PrinterStatusUpdate>> _subs = new();

    public RealtimeService(NavigationManager nav, IConfiguration config)
    {
        _nav = nav;
        _config = config;
    }

    public async Task EnsureConnectedAsync()
    {
        if (_hub != null && _hub.State == HubConnectionState.Connected) return;
        if (_hub == null)
        {
            var apiBaseUrl = _config["ApiBaseUrl"] ?? _nav.BaseUri;
            var baseUri = new Uri(apiBaseUrl);
            var hubUri = new Uri(baseUri, "/hubs/printers");
            _hub = new HubConnectionBuilder()
                .WithUrl(hubUri)
                .WithAutomaticReconnect()
                .Build();
            _hub.On<PrinterStatusUpdate>("PrinterUpdated", update =>
            {
                if (_subs.TryGetValue(update.Id, out var handler))
                {
                    handler(update);
                }
                // Also broadcast to wildcard subscribers (Guid.Empty)
                if (_subs.TryGetValue(Guid.Empty, out var any))
                {
                    any(update);
                }
            });
        }
        if (_hub!.State != HubConnectionState.Connected)
        {
            await _hub.StartAsync();
        }
    }

    public void Subscribe(Guid id, Action<PrinterStatusUpdate> handler)
    {
        _subs[id] = handler;
    }

    public void Unsubscribe(Guid id)
    {
        _subs.TryRemove(id, out _);
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub != null)
        {
            await _hub.DisposeAsync();
        }
    }
}
