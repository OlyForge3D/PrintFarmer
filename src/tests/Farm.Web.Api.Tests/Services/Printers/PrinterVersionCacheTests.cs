using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Moq;

namespace Farm.Web.Api.Tests.Services.Printers;

/// <summary>
/// Regression coverage for #1651: an explicit "Refresh version info" request must bypass the
/// ~10-minute partial-result cache and observe a recovered Backend/API version immediately,
/// while automatic polling (forceRefresh omitted/false) must keep the normal cache policy.
/// </summary>
public sealed class PrinterVersionCacheTests
{
    [Fact]
    public async Task GetAsync_WithoutForceRefresh_ReturnsCachedValueWithoutQueryingBackendAgain()
    {
        Printer printer = CreatePrinter();
        var infoClientMock = new Mock<IBackendClient>();
        infoClientMock.As<ISupportsPrinterInformation>()
            .Setup(c => c.GetPrinterInformationAsync(printer.BackendUrl, printer.Credential, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StandardPrinterInfo { Firmware = "v1.0.0", BackendVersion = "v0.9.0", ApiVersion = "1.0.0" });

        PrinterVersionCache cache = CreateCache(printer, infoClientMock);

        PrinterVersionInfoDto? first = await cache.GetAsync(printer.Id, CancellationToken.None);
        PrinterVersionInfoDto? second = await cache.GetAsync(printer.Id, CancellationToken.None);

        first.Should().NotBeNull();
        second.Should().BeSameAs(first);
        infoClientMock.As<ISupportsPrinterInformation>().Verify(
            c => c.GetPrinterInformationAsync(printer.BackendUrl, printer.Credential, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetAsync_ForceRefresh_BypassesCachedPartialResultAndRecoversAfterFaultClears()
    {
        // Reproduces the issue: a transient Klippy-unavailable fault causes GetPrinterInformationAsync
        // to return a partial result (Firmware present, Backend/API null) which the cache would
        // normally hold onto for the full TTL. An explicit forceRefresh must see the recovered
        // full result immediately, even though the previous cache entry has not expired.
        Printer printer = CreatePrinter();
        var infoClientMock = new Mock<IBackendClient>();
        var partial = new StandardPrinterInfo { Firmware = "v1.0.0", BackendVersion = null, ApiVersion = null };
        var recovered = new StandardPrinterInfo { Firmware = "v1.0.0", BackendVersion = "v0.9.2-emulator", ApiVersion = "1.5.0" };

        infoClientMock.As<ISupportsPrinterInformation>()
            .SetupSequence(c => c.GetPrinterInformationAsync(printer.BackendUrl, printer.Credential, It.IsAny<CancellationToken>()))
            .ReturnsAsync(partial)
            .ReturnsAsync(recovered);

        PrinterVersionCache cache = CreateCache(printer, infoClientMock);

        PrinterVersionInfoDto? duringFault = await cache.GetAsync(printer.Id, CancellationToken.None);
        duringFault!.BackendVersion.Should().BeNull();
        duringFault.ApiVersion.Should().BeNull();

        // Automatic polling (forceRefresh=false, the default) must keep returning the stale
        // cached partial result — its cache policy must not change.
        PrinterVersionInfoDto? polled = await cache.GetAsync(printer.Id, CancellationToken.None);
        polled.Should().BeSameAs(duringFault);

        // The explicit refresh bypasses the cache and observes recovery immediately.
        PrinterVersionInfoDto? refreshed = await cache.GetAsync(printer.Id, CancellationToken.None, forceRefresh: true);
        refreshed!.BackendVersion.Should().Be("v0.9.2-emulator");
        refreshed.ApiVersion.Should().Be("1.5.0");

        // The refreshed value is re-cached under the normal policy, so a subsequent automatic
        // poll now benefits from the recovered value without needing another forced refresh.
        PrinterVersionInfoDto? polledAfterRefresh = await cache.GetAsync(printer.Id, CancellationToken.None);
        polledAfterRefresh.Should().BeSameAs(refreshed);

        infoClientMock.As<ISupportsPrinterInformation>().Verify(
            c => c.GetPrinterInformationAsync(printer.BackendUrl, printer.Credential, It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task GetAsync_ConsecutiveForceRefreshCalls_AreThrottledToASingleLiveBackendCall()
    {
        // Regression coverage for the amplification concern raised in review: an operator (or a
        // buggy client) mashing the "Refresh version info" button repeatedly must not be able to
        // force unbounded live backend round-trips. A second forceRefresh request arriving within
        // the throttle window must downgrade to the normal cache-read path instead.
        Printer printer = CreatePrinter();
        var infoClientMock = new Mock<IBackendClient>();
        infoClientMock.As<ISupportsPrinterInformation>()
            .Setup(c => c.GetPrinterInformationAsync(printer.BackendUrl, printer.Credential, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StandardPrinterInfo { Firmware = "v1.0.0", BackendVersion = "v0.9.0", ApiVersion = "1.0.0" });

        PrinterVersionCache cache = CreateCache(printer, infoClientMock);

        PrinterVersionInfoDto? first = await cache.GetAsync(printer.Id, CancellationToken.None, forceRefresh: true);
        PrinterVersionInfoDto? second = await cache.GetAsync(printer.Id, CancellationToken.None, forceRefresh: true);

        first.Should().NotBeNull();
        second.Should().BeSameAs(first);
        infoClientMock.As<ISupportsPrinterInformation>().Verify(
            c => c.GetPrinterInformationAsync(printer.BackendUrl, printer.Credential, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static PrinterVersionCache CreateCache(Printer printer, Mock<IBackendClient> infoClientMock)
    {
        IMemoryCache memoryCache = new MemoryCache(new MemoryCacheOptions());
        var printersService = new Mock<IPrintersService>();
        printersService
            .Setup(s => s.FindByIdAsync(printer.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);

        var backendClientFactory = new Mock<IBackendClientFactory>();
        backendClientFactory
            .Setup(f => f.GetClient((PrinterBackend)printer.Backend))
            .Returns(infoClientMock.Object);

        var options = Options.Create(new PrinterVersionCacheOptions { Ttl = TimeSpan.FromMinutes(10) });

        return new PrinterVersionCache(memoryCache, options, printersService.Object, backendClientFactory.Object);
    }

    private static Printer CreatePrinter() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Moonraker Ready",
        ServerUrl = "http://moonraker-ready.local",
        ManufacturerId = Guid.NewGuid(),
        ModelId = Guid.NewGuid(),
        Backend = (int)PrinterBackend.Moonraker,
    };
}
