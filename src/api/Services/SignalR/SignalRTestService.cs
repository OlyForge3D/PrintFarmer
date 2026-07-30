using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
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
            await _hubContext.Clients.Client(connectionId).SendAsync("testmessage", testMessage, ct);
            return;
        }

        if (!string.IsNullOrEmpty(groupName))
        {
            await _hubContext.Clients.Group(groupName).SendAsync("testmessage", testMessage, ct);
            return;
        }

        await _hubContext.Clients.Group(Farm.Infrastructure.Security.AuthorizedHubGroups.Farm)
            .SendAsync("testmessage", testMessage, ct);
    }

    public async Task TestDiscoveryGroupAsync(string? sessionId, bool delayBetweenMessages, CancellationToken ct = default)
    {
        string session = sessionId ?? Guid.NewGuid().ToString();
        string groupName = $"discovery-{session}";

        DiscoveryProgressDto[] testMessages =
        {
            CreateProgress(session, scannedIps: 1, progressPercentage: 0.4),
            CreateProgress(session, scannedIps: 10, progressPercentage: 3.9),
            CreateProgress(session, scannedIps: 50, progressPercentage: 19.7),
            CreateProgress(session, scannedIps: 100, progressPercentage: 39.4),
        };

        foreach (DiscoveryProgressDto progress in testMessages)
        {
            await _hubContext.Clients.Group(groupName).SendAsync("discoveryprogress", progress, ct);
            if (delayBetweenMessages)
            {
                await Task.Delay(100, ct);
            }
        }

        DiscoveryPrinterFoundDto testPrinter = new(
            session,
            new DiscoveredPrinterSummaryDto(
                Guid.NewGuid(),
                "Test Printer",
                PrinterBackend.Moonraker,
                Manufacturer: null,
                Model: null,
                DiscoveredAt: DateTime.UtcNow,
                IsReachable: true));
        await _hubContext.Clients.Group(groupName).SendAsync("discoveryprinterfound", testPrinter, ct);

        DiscoveryCompletedDto completion = new(
            session,
            TotalPrintersFound: 1,
            TotalPrintersExcluded: 0,
            Duration: TimeSpan.FromSeconds(10.5));
        await _hubContext.Clients.Group(groupName).SendAsync("discoverycompleted", completion, ct);
    }

    public object GetConnectionStats()
    {
        return new
        {
            Timestamp = DateTime.UtcNow,
            HubName = nameof(PrinterHub),
            AvailableMethods = new[] { "printerupdated", "harvestprogress", "jobqueueupdated", "discoveryprogress", "discoveryprinterfound", "discoverycompleted", "testmessage" },
            HealthStatus = "Hub context available and functional"
        };
    }

    private static DiscoveryProgressDto CreateProgress(
        string sessionId,
        int scannedIps,
        double progressPercentage) =>
        new(
            sessionId,
            CurrentNetwork: string.Empty,
            CurrentIp: string.Empty,
            TotalIps: 254,
            ScannedIps: scannedIps,
            PrintersFound: 0,
            PrintersExcluded: 0,
            ProgressPercentage: progressPercentage,
            Status: DiscoveryStatus.Scanning);
}
