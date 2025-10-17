using System.Threading;
using System.Threading.Tasks;

namespace Farm.Web.Api.Services.SignalR;

public interface ISignalRTestService
{
    Task SendTestMessageAsync(string? connectionId, string? groupName, string? message, CancellationToken ct = default);
    Task TestDiscoveryGroupAsync(string? sessionId, bool delayBetweenMessages, CancellationToken ct = default);
    object GetConnectionStats();
}
