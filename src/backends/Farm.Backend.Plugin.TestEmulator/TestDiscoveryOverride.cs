using Farm.Infrastructure;
using Farm.Infrastructure.Discovery;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Farm.Backend.Plugin.TestEmulator;

/// <summary>
/// Provides mock discovery results when TestEmulator:MockDiscovery is enabled.
/// Simulates a 2-second network scan returning 3 fake printers with SignalR progress events.
/// </summary>
public sealed class TestDiscoveryOverride(
    IHubContext<PrinterHub> hub,
    ILogger<TestDiscoveryOverride> logger)
{
    /// <summary>
    /// Runs a simulated discovery scan with progress events.
    /// </summary>
    public async Task<List<DiscoveredPrinterDto>> RunMockDiscoveryAsync(string sessionId, CancellationToken ct)
    {
        logger.LogInformation("TestDiscoveryOverride: starting mock discovery for session {SessionId}", sessionId);

        // Phase 1: Starting (0%)
        await BroadcastProgressAsync(sessionId, 0, DiscoveryStatus.Scanning, "Scanning test network...", ct);
        await Task.Delay(700, ct);

        // Phase 2: Scanning (50%)
        await BroadcastProgressAsync(sessionId, 50, DiscoveryStatus.Scanning, "Probing test printers...", ct);
        await Task.Delay(700, ct);

        // Phase 3: Generate discovered printers
        var discovered = new List<DiscoveredPrinterDto>
        {
            DiscoveredPrinterDto.FromProbe(
                ipAddress: "192.168.1.200",
                serverUrl: "http://192.168.1.200",
                name: "Discovered Test Printer 1",
                backend: PrinterBackend.PrusaLink,
                backendPort: 80,
                manufacturer: "Prusa Research",
                model: "MK4S"),

            DiscoveredPrinterDto.FromProbe(
                ipAddress: "192.168.1.201",
                serverUrl: "http://192.168.1.201:7125",
                name: "Discovered Test Printer 2",
                backend: PrinterBackend.Moonraker,
                backendPort: 7125,
                frontendPort: 80,
                manufacturer: "Voron Design",
                model: "V2.4"),

            DiscoveredPrinterDto.FromProbe(
                ipAddress: "192.168.1.202",
                serverUrl: "http://192.168.1.202",
                name: "Discovered Test Printer 3",
                backend: PrinterBackend.OctoPrint,
                backendPort: 5000,
                manufacturer: "Creality",
                model: "Ender 3 V3"),
        };

        // Broadcast each found printer
        foreach (DiscoveredPrinterDto printer in discovered)
        {
            var found = new DiscoveryPrinterFoundDto(sessionId, printer);
            await hub.Clients.Group($"discovery-{sessionId}").SendAsync("discoveryprinterfound", found, ct);
        }

        await Task.Delay(600, ct);

        // Phase 4: Complete (100%)
        await BroadcastProgressAsync(sessionId, 100, DiscoveryStatus.Completed, "Mock discovery complete", ct);

        var completed = new DiscoveryCompletedDto(
            SessionId: sessionId,
            TotalPrintersFound: discovered.Count,
            TotalPrintersExcluded: 0,
            Duration: TimeSpan.FromSeconds(2),
            NetworkRanges: ["192.168.1.0/24"],
            AutoDetectedNetworks: true);

        await hub.Clients.Group($"discovery-{sessionId}").SendAsync("discoverycompleted", completed, ct);

        logger.LogInformation("TestDiscoveryOverride: mock discovery complete — {Count} printers found", discovered.Count);
        return discovered;
    }

    private async Task BroadcastProgressAsync(string sessionId, double pct, DiscoveryStatus status, string message, CancellationToken ct)
    {
        var progress = new DiscoveryProgressDto(
            SessionId: sessionId,
            CurrentNetwork: "192.168.1.0/24",
            CurrentIp: $"192.168.1.{(int)(pct * 2.55)}",
            TotalIps: 254,
            ScannedIps: (int)(pct / 100.0 * 254),
            PrintersFound: pct >= 100 ? 3 : (int)(pct / 50),
            PrintersExcluded: 0,
            ProgressPercentage: pct,
            Status: status,
            Message: message,
            NetworkRanges: ["192.168.1.0/24"],
            AutoDetectedNetworks: true);

        await hub.Clients.Group($"discovery-{sessionId}").SendAsync("discoveryprogress", progress, ct);
    }
}
