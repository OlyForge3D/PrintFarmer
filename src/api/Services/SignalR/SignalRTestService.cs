using Farm.Infrastructure.Services.SignalR;
using Microsoft.AspNetCore.SignalR;

namespace Farm.Web.Api.Services.SignalR;

public class SignalRTestService(IHubContext<PrinterHub> hubContext) : ISignalRTestService
{
    private readonly IHubContext<PrinterHub> _hubContext = hubContext;

    public async Task SendTestMessageAsync(string? connectionId, string? groupName, string? message, CancellationToken ct = default)
    {
        var testMessage = new
        {
            Timestamp = DateTime.UtcNow,
            TestId = Guid.NewGuid().ToString(),
            Message = message ?? "SignalR connectivity test",
            Source = "API Health Check"
        };

        if (!string.IsNullOrEmpty(connectionId))
        {
            await _hubContext.Clients.Client(connectionId).SendAsync("TestMessage", testMessage, ct);
            return;
        }

        if (!string.IsNullOrEmpty(groupName))
        {
            await _hubContext.Clients.Group(groupName).SendAsync("TestMessage", testMessage, ct);
            return;
        }

        await _hubContext.Clients.All.SendAsync("TestMessage", testMessage, ct);
    }

    public async Task TestDiscoveryGroupAsync(string? sessionId, bool delayBetweenMessages, CancellationToken ct = default)
    {
        string session = sessionId ?? Guid.NewGuid().ToString();
        string groupName = $"discovery-{session}";

        var testMessages = new[]
        {
            new { SessionId = session, CurrentIP = "10.0.0.1", ScannedCount = 1, TotalCount = 254, Progress = 0.4 },
            new { SessionId = session, CurrentIP = "10.0.0.10", ScannedCount = 10, TotalCount = 254, Progress = 3.9 },
            new { SessionId = session, CurrentIP = "10.0.0.50", ScannedCount = 50, TotalCount = 254, Progress = 19.7 },
            new { SessionId = session, CurrentIP = "10.0.0.100", ScannedCount = 100, TotalCount = 254, Progress = 39.4 }
        };

        foreach (var m in testMessages)
        {
            await _hubContext.Clients.Group(groupName).SendAsync("DiscoveryProgress", m, ct);
            if (delayBetweenMessages)
            {
                await Task.Delay(100, ct);
            }
        }

        var testPrinter = new { SessionId = session, IpAddress = "10.0.0.123", Name = "Test Printer", Backend = "Moonraker", ServerUrl = "http://10.0.0.123" };
        await _hubContext.Clients.Group(groupName).SendAsync("DiscoveryPrinterFound", testPrinter, ct);

        var completion = new { SessionId = session, TotalScanned = 254, PrintersFound = 1, Duration = TimeSpan.FromSeconds(10.5) };
        await _hubContext.Clients.Group(groupName).SendAsync("DiscoveryCompleted", completion, ct);
    }

    public object GetConnectionStats()
    {
        return new
        {
            Timestamp = DateTime.UtcNow,
            HubName = nameof(PrinterHub),
            AvailableMethods = new[] { "PrinterStatusUpdated", "HarvestProgress", "JobQueueUpdated", "DiscoveryProgress", "DiscoveryPrinterFound", "DiscoveryCompleted", "TestMessage" },
            HealthStatus = "Hub context available and functional"
        };
    }
}
