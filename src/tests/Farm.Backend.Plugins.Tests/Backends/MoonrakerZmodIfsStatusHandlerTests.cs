using System.Text.Json;
using Farm.Backend.Plugin.Moonraker;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers.Moonraker;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Backend.Plugins.Tests.Backends;

/// <summary>
/// Issue #3392: drives a ZMOD IFS status frame through the production Moonraker status-handling path
/// so removing the handler's parser call fails a test (finding R3389-H02 of PR #3389).
/// </summary>
public sealed class MoonrakerZmodIfsStatusHandlerTests
{
    private const string ZmodFrame =
        """
        {
          "zmod_ifs": { "available": true, "Ports": [true, false, false, true], "Chan": 4 },
          "zmod_color": {
            "channel": 4,
            "slots": [
              { "ID": 1, "Material": "PLA", "Color": "Blue", "HEX": "A03CF7", "hasFilament": true },
              { "ID": 2, "Material": "?", "Color": "", "HEX": "", "hasFilament": false },
              { "ID": 3, "Material": "?", "Color": "", "HEX": "", "hasFilament": false },
              { "ID": 4, "Material": "PLA", "Color": "White", "HEX": "FFFFFF", "hasFilament": true }
            ]
          }
        }
        """;

    [Fact]
    public async Task ProcessStatusUpdateAsync_ZmodFrame_PopulatesMmuGatesInCacheAndBroadcast()
    {
        var printerId = Guid.NewGuid();
        var cacheWriter = new Mock<IPrinterStatusCacheWriter>();
        PrinterStatusDto? cached = null;
        cacheWriter
            .Setup(w => w.UpdateStatus(It.IsAny<PrinterStatusDto>(), It.IsAny<long?>()))
            .Callback<PrinterStatusDto, long?>((status, _) => cached = status);

        var proxy = new Mock<IClientProxy>();
        object?[]? broadcastArgs = null;
        proxy
            .Setup(p => p.SendCoreAsync("printerupdated", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((_, args, _) => broadcastArgs = args)
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);
        var hub = new Mock<IHubContext<PrinterHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        var services = new ServiceCollection();
        services.AddScoped(_ => new Mock<IMoonrakerClient>().Object);
        await using ServiceProvider provider = services.BuildServiceProvider();

        using var service = new MoonrakerSubscriptionService(
            hub.Object,
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MoonrakerSubscriptionService>.Instance,
            new Mock<IHttpClientFactory>().Object,
            cacheWriter.Object);

        using JsonDocument doc = JsonDocument.Parse(ZmodFrame);
        await service.ProcessStatusUpdateAsync(
            doc.RootElement,
            printerId,
            "http://zmod.invalid",
            credential: null,
            cameraStreamUrl: null,
            thumbnailUrl: null,
            originWatermark: null,
            CancellationToken.None);

        cached.Should().NotBeNull();
        MmuStatusDto mmu = cached!.MmuStatus!;
        mmu.Should().NotBeNull();
        mmu.Enabled.Should().BeTrue();
        mmu.MmuType.Should().Be(MmuProtocol.ZmodIfs);
        mmu.NumGates.Should().Be(4);
        mmu.ActiveGate.Should().Be(3);
        mmu.Gates.Should().HaveCount(4);
        mmu.Gates[0].Material.Should().Be("PLA");
        mmu.Gates[0].Color.Should().NotBeNullOrEmpty();
        mmu.Gates[1].Material.Should().NotBe("PLA");
        mmu.Gates[3].Material.Should().Be("PLA");

        broadcastArgs.Should().NotBeNull();
        var update = broadcastArgs!.Single().Should().BeOfType<PrinterStatusUpdate>().Subject;
        update.Id.Should().Be(printerId);
        update.MmuStatus.Should().NotBeNull();
        update.MmuStatus!.MmuType.Should().Be(MmuProtocol.ZmodIfs);
        update.MmuStatus.ActiveGate.Should().Be(3);
        update.MmuStatus.Gates.Should().HaveCount(4);
    }
}
