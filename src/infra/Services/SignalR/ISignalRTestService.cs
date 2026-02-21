using System.Threading;
using System.Threading.Tasks;

namespace Farm.Infrastructure.Services.SignalR;

/// <summary>
/// Service for testing SignalR hub connectivity and message delivery.
/// </summary>
public interface ISignalRTestService
{
    /// <summary>Sends a test message to a specific connection or group.</summary>
    Task SendTestMessageAsync(string? connectionId, string? groupName, string? message, CancellationToken ct = default);

    /// <summary>Tests discovery group message delivery with optional delays.</summary>
    Task TestDiscoveryGroupAsync(string? sessionId, bool delayBetweenMessages, CancellationToken ct = default);

    /// <summary>Gets current connection statistics.</summary>
    object GetConnectionStats();
}
