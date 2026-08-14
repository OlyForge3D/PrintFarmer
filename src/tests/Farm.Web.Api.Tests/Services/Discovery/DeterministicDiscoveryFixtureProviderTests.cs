extern alias PrinterDiscoveryRef;
using Farm.Infrastructure;
using Farm.Infrastructure.Discovery;
using Farm.Infrastructure.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using DeterministicDiscoveryFixtureProvider =
    PrinterDiscoveryRef::PrinterDiscovery.Services.DeterministicDiscoveryFixtureProvider;
using DeterministicDiscoveryFixtureSettings =
    PrinterDiscoveryRef::PrinterDiscovery.Services.DeterministicDiscoveryFixtureSettings;
using DeterministicDiscoveryPrinter =
    PrinterDiscoveryRef::PrinterDiscovery.Services.DeterministicDiscoveryPrinter;
using IApiClient = PrinterDiscoveryRef::PrinterDiscovery.Services.IApiClient;
using IDeterministicDiscoveryFixtureProvider =
    PrinterDiscoveryRef::PrinterDiscovery.Services.IDeterministicDiscoveryFixtureProvider;
using IDiscoveryProgressBroadcaster =
    PrinterDiscoveryRef::PrinterDiscovery.Services.IDiscoveryProgressBroadcaster;
using IDiscoverySessionManager =
    PrinterDiscoveryRef::PrinterDiscovery.Services.IDiscoverySessionManager;
using StreamingDiscoveryService =
    PrinterDiscoveryRef::PrinterDiscovery.Services.StreamingDiscoveryService;

namespace Farm.Web.Api.Tests.Services.Discovery;

public sealed class DeterministicDiscoveryFixtureProviderTests
{
    [Fact]
    public void GetPrinters_MoonrakerFilter_ReturnsStableLocalCandidates()
    {
        var settings = new DeterministicDiscoveryFixtureSettings { Enabled = true };
        var provider = new DeterministicDiscoveryFixtureProvider(Options.Create(settings));

        var printers = provider.GetPrinters([PrinterBackend.Moonraker]);

        Assert.True(provider.IsEnabled);
        Assert.Equal(2, printers.Count);
        Assert.All(printers, printer =>
        {
            Assert.Equal(PrinterBackend.Moonraker, printer.Backend);
            Assert.Equal(7125, printer.BackendPort);
            Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), printer.DiscoveredAt);
            Assert.StartsWith("http://moonraker-discovery-", printer.ServerUrl, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void GetPrinters_NonMoonrakerFilter_ReturnsNoCandidates()
    {
        var provider = new DeterministicDiscoveryFixtureProvider(
            Options.Create(new DeterministicDiscoveryFixtureSettings { Enabled = true }));

        var printers = provider.GetPrinters([PrinterBackend.PrusaLink]);

        Assert.Empty(printers);
    }

    [Fact]
    public void GetPrinters_InvalidFixture_ThrowsConfigurationError()
    {
        var settings = new DeterministicDiscoveryFixtureSettings
        {
            Enabled = true,
            Printers =
            [
                new DeterministicDiscoveryPrinter(
                    "Invalid",
                    "invalid",
                    "not-a-url",
                    "Test",
                    "Invalid"),
            ],
        };
        var provider = new DeterministicDiscoveryFixtureProvider(Options.Create(settings));

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => provider.GetPrinters(null));

        Assert.Contains("absolute HTTP URL", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScanWithProgressAsync_FixturesEnabled_UsesRealDiscoveryEventBoundary()
    {
        var coreDiscovery = new Mock<ICoreNetworkDiscoveryService>(MockBehavior.Strict);
        var apiClient = new Mock<IApiClient>(MockBehavior.Strict);
        apiClient
            .Setup(client => client.GetRegisteredPrinterUrlsAsync(
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        List<InternalDiscoveryPrinterFoundDto> found = [];
        var broadcaster = new Mock<IDiscoveryProgressBroadcaster>(MockBehavior.Strict);
        broadcaster
            .Setup(value => value.BroadcastProgressAsync(
                It.IsAny<DiscoveryProgressDto>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        broadcaster
            .Setup(value => value.BroadcastPrinterFoundAsync(
                It.IsAny<InternalDiscoveryPrinterFoundDto>(),
                It.IsAny<CancellationToken>()))
            .Callback<InternalDiscoveryPrinterFoundDto, CancellationToken>(
                (printer, _) => found.Add(printer))
            .Returns(Task.CompletedTask);
        broadcaster
            .Setup(value => value.BroadcastCompletedAsync(
                It.IsAny<DiscoveryCompletedDto>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        IDeterministicDiscoveryFixtureProvider fixtureProvider =
            new DeterministicDiscoveryFixtureProvider(
                Options.Create(new DeterministicDiscoveryFixtureSettings { Enabled = true }));
        var sessions = new Mock<IDiscoverySessionManager>(MockBehavior.Strict);
        sessions
            .Setup(value => value.RegisterSession(
                "fixture-session",
                It.IsAny<CancellationTokenSource>()));
        sessions.Setup(value => value.RemoveSession("fixture-session"));

        IConfiguration configuration = new ConfigurationBuilder().Build();
        var service = new StreamingDiscoveryService(
            coreDiscovery.Object,
            apiClient.Object,
            broadcaster.Object,
            fixtureProvider,
            sessions.Object,
            NullLogger<StreamingDiscoveryService>.Instance,
            configuration);

        var printers = await service.ScanWithProgressAsync(
            "fixture-session",
            [PrinterBackend.Moonraker],
            autoRegister: false);

        Assert.Equal(2, printers.Count);
        Assert.Equal(2, found.Count);
        Assert.All(found, printer => Assert.Equal(PrinterBackend.Moonraker, printer.Backend));
        coreDiscovery.VerifyNoOtherCalls();
        broadcaster.Verify(
            value => value.BroadcastCompletedAsync(
                It.Is<DiscoveryCompletedDto>(completed =>
                    completed.TotalPrintersFound == 2 &&
                    completed.TotalPrintersExcluded == 0),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
