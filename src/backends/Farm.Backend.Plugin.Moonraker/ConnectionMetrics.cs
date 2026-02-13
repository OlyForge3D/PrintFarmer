using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers.Moonraker;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Telemetry;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Farm.Backend.Plugin.Moonraker;

// Connection metrics and retry logic helpers
internal sealed class ConnectionMetrics
{
    public int ReconnectAttempts { get; set; }

    public int ConsecutiveFailures { get; set; }

    public int TotalReconnects { get; set; }

    public DateTime LastConnected { get; set; }

    public DateTime LastDisconnected { get; set; }

    public DateTime LastReconnectAttempt { get; set; }

    public TimeSpan GetNextBackoffDelay() => TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(ReconnectAttempts, 8))));

    public void Reset()
    {
        ReconnectAttempts = 0;
        ConsecutiveFailures = 0;
        LastConnected = DateTime.UtcNow;
    }

    public void IncrementAttempts()
    {
        ReconnectAttempts++;
        ConsecutiveFailures++;
        LastReconnectAttempt = DateTime.UtcNow;
    }

    public void RecordDisconnect()
    {
        LastDisconnected = DateTime.UtcNow;
    }
}
