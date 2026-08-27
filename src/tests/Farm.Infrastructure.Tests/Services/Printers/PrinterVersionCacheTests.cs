using System.Collections.Concurrent;
using System.Reflection;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Moq;

namespace Farm.Infrastructure.Tests.Services.Printers;

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

    [Fact]
    public async Task GetAsync_ConcurrentForceRefreshCalls_OnlyOneWinsTheThrottleWindowAgainstAWarmCache()
    {
        // Regression coverage for the throttle-atomicity finding from review: the throttle claim
        // must be a single atomic check-and-set so that two forceRefresh calls racing on separate
        // threads for the same printer cannot both observe "no active window" before either claims
        // it. This reproduces the realistic scenario the throttle exists for — an operator
        // double-mashing "Refresh version info" against an already-cached printer — by warming the
        // cache with an initial call before racing two forceRefresh calls against it. (Racing two
        // forceRefresh calls against a completely cold cache is a separate, pre-existing
        // first-fetch race unrelated to this throttle and is out of scope here.)
        Printer printer = CreatePrinter();
        var infoClientMock = new Mock<IBackendClient>();
        infoClientMock.As<ISupportsPrinterInformation>()
            .Setup(c => c.GetPrinterInformationAsync(printer.BackendUrl, printer.Credential, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50));
                return new StandardPrinterInfo { Firmware = "v1.0.0", BackendVersion = "v0.9.0", ApiVersion = "1.0.0" };
            });

        Mock<IPrintersService> printersService = CreatePrintersServiceMock(printer);
        PrinterVersionCache cache = CreateCache(printer, infoClientMock, printersService);

        // Warm the cache so the throttle loser's downgrade-to-normal-cache-read path has a value
        // to return instead of also needing a live fetch.
        await cache.GetAsync(printer.Id, CancellationToken.None);

        // Force genuine overlap at the atomic claim itself — not merely at an earlier step in
        // the method. Gating two threads at an earlier point (e.g. the printer lookup) only
        // guarantees they are *released* together; it does not guarantee they reach the
        // AddOrUpdate call at the same instant, so a test built that way could still pass
        // against a non-atomic implementation depending on scheduler luck. PrinterVersionCache
        // exposes a test-only hook (TestOnlyBeforeThrottleClaim) invoked immediately before the
        // claim, so both threads rendezvous on a two-party barrier right there, with essentially
        // nothing but the atomic call itself between release and contention. Assert the barrier's
        // return value explicitly: a missed rendezvous must be a loud, immediate test failure,
        // not a silent 5-second stall that could let a flaky/slow CI run pass without the
        // intended two-party gate ever having happened.
        using var claimBarrier = new Barrier(2);
        PrinterVersionCache.TestOnlyBeforeThrottleClaim = _ =>
        {
            bool bothArrived = claimBarrier.SignalAndWait(TimeSpan.FromSeconds(5));
            Assert.True(bothArrived, "both concurrent forceRefresh calls must reach the throttle claim together");
        };

        try
        {
            Task<PrinterVersionInfoDto?> call1 = Task.Run(() => cache.GetAsync(printer.Id, CancellationToken.None, forceRefresh: true));
            Task<PrinterVersionInfoDto?> call2 = Task.Run(() => cache.GetAsync(printer.Id, CancellationToken.None, forceRefresh: true));

            PrinterVersionInfoDto?[] results = await Task.WhenAll(call1, call2);

            results[0].Should().NotBeNull();
            results[1].Should().NotBeNull();

            // One backend call from warming the cache, plus exactly one more from whichever
            // forceRefresh call won the throttle window — the loser must not trigger a second.
            infoClientMock.As<ISupportsPrinterInformation>().Verify(
                c => c.GetPrinterInformationAsync(printer.BackendUrl, printer.Credential, It.IsAny<CancellationToken>()),
                Times.Exactly(2));
        }
        finally
        {
            PrinterVersionCache.TestOnlyBeforeThrottleClaim = null;
        }
    }

    [Fact]
    public async Task GetAsync_ForceRefreshForNonexistentPrinter_NeverClaimsAThrottleWindow()
    {
        // Regression coverage for the unbounded-growth finding from review: the throttle claim
        // must only ever be taken for a printer id that FindByIdAsync has confirmed to exist.
        // Otherwise, a forceRefresh request against an arbitrary/nonexistent printer id (e.g. a
        // stale id, a typo, or a malicious probe) would still add a permanent entry to the
        // process-wide throttle table — an unbounded memory-growth vector under the same
        // amplification/DoS threat model this throttle exists to close.
        Guid missingPrinterId = Guid.NewGuid();
        var infoClientMock = new Mock<IBackendClient>();
        var printersService = new Mock<IPrintersService>();
        printersService
            .Setup(s => s.FindByIdAsync(missingPrinterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Printer?)null);

        var backendClientFactory = new Mock<IBackendClientFactory>();
        var options = Options.Create(new PrinterVersionCacheOptions { Ttl = TimeSpan.FromMinutes(10) });
        IMemoryCache memoryCache = new MemoryCache(new MemoryCacheOptions());
        var cache = new PrinterVersionCache(memoryCache, options, printersService.Object, backendClientFactory.Object);

        FieldInfo throttleField = typeof(PrinterVersionCache).GetField("ForceRefreshWindows", BindingFlags.NonPublic | BindingFlags.Static)!;
        var throttleTable = (ConcurrentDictionary<Guid, (Guid Token, DateTime ExpiresAtUtc)>)throttleField.GetValue(null)!;

        PrinterVersionInfoDto? result = await cache.GetAsync(missingPrinterId, CancellationToken.None, forceRefresh: true);

        result.Should().BeNull();
        throttleTable.ContainsKey(missingPrinterId).Should().BeFalse(
            "a forceRefresh request for a printer that does not exist must never grow the process-wide throttle table");
    }

    private static Mock<IPrintersService> CreatePrintersServiceMock(Printer printer)
    {
        var printersService = new Mock<IPrintersService>();
        printersService
            .Setup(s => s.FindByIdAsync(printer.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);
        return printersService;
    }

    private static PrinterVersionCache CreateCache(Printer printer, Mock<IBackendClient> infoClientMock, Mock<IPrintersService>? printersService = null)
    {
        IMemoryCache memoryCache = new MemoryCache(new MemoryCacheOptions());
        printersService ??= CreatePrintersServiceMock(printer);

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
