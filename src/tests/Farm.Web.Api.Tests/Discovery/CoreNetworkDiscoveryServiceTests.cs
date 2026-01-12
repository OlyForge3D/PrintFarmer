using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Discovery;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Discovery;

public class CoreNetworkDiscoveryServiceTests
{
    [Fact]
    public async Task DiscoverPrinterAsync_ReturnsNull_WhenNoProbeMatches()
    {
        var probe = new StubProbe(PrinterBackend.Moonraker, _ => null, "moonraker");
        var service = new CoreNetworkDiscoveryService(new[] { probe });

        DiscoveredPrinterDto? result = await service.DiscoverPrinterAsync("", 500);

        result.Should().BeNull();
        probe.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task DiscoverPrinterAsync_UsesBackendFilter()
    {
        var moonrakerProbe = new StubProbe(PrinterBackend.Moonraker, ip => MakeResult(ip, PrinterBackend.Moonraker, 50), "moonraker");
        var prusaProbe = new StubProbe(PrinterBackend.PrusaLink, ip => MakeResult(ip, PrinterBackend.PrusaLink, 90), "prusa");
        var service = new CoreNetworkDiscoveryService(new INetworkDiscoveryProbe[] { moonrakerProbe, prusaProbe });

        DiscoveredPrinterDto? result = await service.DiscoverPrinterAsync(
            "192.168.1.50",
            timeoutMs: 500,
            backendFilter: new[] { PrinterBackend.PrusaLink });

        result.Should().NotBeNull();
        result!.Backend.Should().Be(PrinterBackend.PrusaLink);
        prusaProbe.CallCount.Should().Be(1);
        moonrakerProbe.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task DiscoverPrinterAsync_SelectsHighestConfidence()
    {
        var low = new StubProbe(PrinterBackend.Moonraker, ip => MakeResult(ip, PrinterBackend.Moonraker, 50), "low");
        var high = new StubProbe(PrinterBackend.PrusaLink, ip => MakeResult(ip, PrinterBackend.PrusaLink, 90), "high");
        var service = new CoreNetworkDiscoveryService(new INetworkDiscoveryProbe[] { low, high });

        DiscoveredPrinterDto? result = await service.DiscoverPrinterAsync("10.0.0.5", 500);

        result.Should().NotBeNull();
        result!.Backend.Should().Be(PrinterBackend.PrusaLink);
        low.CallCount.Should().Be(1);
        high.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task DiscoverMultipleAsync_ReturnsAllMatches()
    {
        var probe = new StubProbe(PrinterBackend.Moonraker, ip => MakeResult(ip, PrinterBackend.Moonraker, 80, name: $"Printer-{ip}"), "moonraker");
        var service = new CoreNetworkDiscoveryService(new[] { probe });
        var ips = new[] { "10.0.0.1", "10.0.0.2", "10.0.0.3" };

        List<DiscoveredPrinterDto> results = await service.DiscoverMultipleAsync(ips, timeoutMs: 500, maxConcurrent: 2);

        results.Should().HaveCount(3);
        results.Select(r => r.IpAddress).Should().BeEquivalentTo(ips);
        probe.CallCount.Should().Be(3);
    }

    private static ProbeResult MakeResult(string ip, PrinterBackend backend, int confidence, string name = "Printer") =>
        new(
            new DiscoveredPrinterDto
            {
                IpAddress = ip,
                ServerUrl = $"http://{ip}",
                Name = name,
                Backend = backend,
                BackendPort = PrinterBackendHelpers.GetDefaultPort(backend),
                DiscoveredAt = DateTime.UtcNow,
                IsReachable = true
            },
            confidence,
            "stub"
        );

    private sealed class StubProbe : INetworkDiscoveryProbe
    {
        private readonly Func<string, ProbeResult?> _resultFactory;

        public StubProbe(PrinterBackend backend, Func<string, ProbeResult?> resultFactory, string displayName)
        {
            Backend = backend;
            _resultFactory = resultFactory;
            DisplayName = displayName;
        }

        public string DisplayName { get; }
        public PrinterBackend Backend { get; }
        public int CallCount { get; private set; }

        public Task<ProbeResult?> ProbeAsync(string ipAddress, int timeoutMs, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_resultFactory(ipAddress));
        }
    }
}
