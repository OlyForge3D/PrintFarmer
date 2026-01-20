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

internal static class MoonrakerErrors
{
    public static bool IsTransientError(Exception ex) => ex switch
    {
        OperationCanceledException => false, // Don't retry on cancellation
        WebSocketException wsEx => wsEx.WebSocketErrorCode switch
        {
            WebSocketError.ConnectionClosedPrematurely => true,
            WebSocketError.Faulted => true,
            _ => false
        },
        HttpRequestException => true, // Network connectivity issues
        TimeoutException => true,
        _ => false
    };

    public static bool IsFatalError(Exception ex) => ex switch
    {
        ArgumentException => true, // Configuration issues
        UriFormatException => true, // Invalid URLs
        UnauthorizedAccessException => true, // Auth failures
        _ => false
    };
}
