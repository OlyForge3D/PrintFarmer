using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Cameras;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Spoolman;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.Printers;

/// <summary>
/// Covers issue #2338: <see cref="PrintersService.GetAllCompleteDtosAsync"/>'s DB-based spool
/// fallback (previously <c>BuildDbSpoolInfoAsync</c>, called uncached and sequentially once per
/// printer) must instead (1) coalesce repeated spool IDs across printers/requests through
/// <see cref="ISpoolmanStatusCache"/>, and (2) bound both the fan-out concurrency and the
/// per-lookup timeout so a single unreachable Spoolman call degrades one request instead of
/// multiplying N x the 30s <c>HttpClient.Timeout</c> across every offline printer.
/// </summary>
public sealed class PrintersServiceSpoolmanFallbackConcurrencyTests
{
    [Fact]
    public async Task GetAllCompleteDtosAsync_PrintersSharingSpoolId_AcrossTwoCallsWithinTtl_FetchesSpoolOnlyOnce()
    {
        // Three printers, all pointing at the SAME spool ID (repeated ID case) and all lacking a
        // cached status (so all three trigger the DB fallback). Two full GetAllCompleteDtosAsync
        // calls within the cache's 30s TTL must still only hit Spoolman once for that ID.
        const int SharedSpoolId = 42;
        List<Printer> printers =
        [
            CreatePrinter(spoolId: SharedSpoolId),
            CreatePrinter(spoolId: SharedSpoolId),
            CreatePrinter(spoolId: SharedSpoolId),
        ];

        var spoolman = new Mock<ISpoolmanService>();
        _ = spoolman
            .Setup(s => s.GetSpoolByIdAsync(SharedSpoolId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpoolmanSpoolDto(SharedSpoolId, "Shared", "PLA", 900, null, false, FilamentName: "Shared"));

        SpoolmanStatusCache statusCache = CreateStatusCache(spoolman.Object, TimeProvider.System);
        await using AppDbContext db = CreateDbContext();
        PrintersService service = CreateService(db, printers, statusCache);

        CompletePrinterDto[] first = await service.GetAllCompleteDtosAsync(CancellationToken.None);
        CompletePrinterDto[] second = await service.GetAllCompleteDtosAsync(CancellationToken.None);

        first.Should().HaveCount(3);
        second.Should().HaveCount(3);
        first.Should().OnlyContain(dto => dto.SpoolInfo != null && dto.SpoolInfo.SpoolName == "Shared");
        second.Should().OnlyContain(dto => dto.SpoolInfo != null && dto.SpoolInfo.SpoolName == "Shared");

        // The whole point of #2338: distinct printers sharing a spool ID, across two separate
        // calls within the TTL, must be coalesced into a single upstream Spoolman call -- not
        // once per printer per call (which would be 6 calls here under the old behavior).
        spoolman.Verify(
            s => s.GetSpoolByIdAsync(SharedSpoolId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetAllCompleteDtosAsync_OneBlackHoledSpool_DoesNotDelayOtherPrintersOrExceedBoundedTimeout()
    {
        // Four printers with DISTINCT spool IDs (the realistic fleet case per #2338 -- ID
        // repetition alone does not help here). One spool's upstream lookup never completes
        // (simulating an unreachable/black-holed Spoolman); the other three resolve immediately.
        List<Printer> printers =
        [
            CreatePrinter(spoolId: 1),
            CreatePrinter(spoolId: 2),
            CreatePrinter(spoolId: 3), // black-holed
            CreatePrinter(spoolId: 4),
        ];

        var neverCompletes = new TaskCompletionSource<SpoolmanSpoolDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var spoolman = new Mock<ISpoolmanService>();
        _ = spoolman.Setup(s => s.GetSpoolByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpoolmanSpoolDto(1, "One", "PLA", 900, null, false, FilamentName: "One"));
        _ = spoolman.Setup(s => s.GetSpoolByIdAsync(2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpoolmanSpoolDto(2, "Two", "PETG", 800, null, false, FilamentName: "Two"));
#pragma warning disable VSTHRD003 // Intentional: this Task represents an upstream call that never
        // completes during the test, simulating a black-holed Spoolman.
        _ = spoolman.Setup(s => s.GetSpoolByIdAsync(3, It.IsAny<CancellationToken>()))
            .Returns(() => neverCompletes.Task);
#pragma warning restore VSTHRD003
        _ = spoolman.Setup(s => s.GetSpoolByIdAsync(4, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpoolmanSpoolDto(4, "Four", "ABS", 700, null, false, FilamentName: "Four"));

        SpoolmanStatusCache statusCache = CreateStatusCache(spoolman.Object, TimeProvider.System);
        await using AppDbContext db = CreateDbContext();
        PrintersService service = CreateService(db, printers, statusCache);

        var stopwatch = Stopwatch.StartNew();
        CompletePrinterDto[] dtos = await service.GetAllCompleteDtosAsync(CancellationToken.None);
        stopwatch.Stop();

        // Old sequential behavior would have blocked for up to 30s (the ISpoolmanService typed
        // client's HttpClient.Timeout) on printer #3 alone, on top of the other three. The fix's
        // bounded per-lookup timeout is a few seconds; 15s is a generous ceiling that still proves
        // this isn't anywhere near the old N x 30s (or even a single unbounded 30s) worst case.
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));

        dtos.Should().HaveCount(4);
        GetDto(dtos, printers[0]).SpoolInfo.Should().BeEquivalentTo(
            new { ActiveSpoolId = 1, SpoolName = "One", Material = "PLA" },
            options => options.ExcludingMissingMembers());
        GetDto(dtos, printers[1]).SpoolInfo.Should().BeEquivalentTo(
            new { ActiveSpoolId = 2, SpoolName = "Two", Material = "PETG" },
            options => options.ExcludingMissingMembers());
        GetDto(dtos, printers[3]).SpoolInfo.Should().BeEquivalentTo(
            new { ActiveSpoolId = 4, SpoolName = "Four", Material = "ABS" },
            options => options.ExcludingMissingMembers());

        // The black-holed printer degrades to a "we know a spool is assigned but couldn't reach
        // Spoolman" placeholder instead of hanging the whole request -- it must not be null and
        // must not carry the still-pending upstream's data.
        PrinterSpoolInfoDto? blackHoled = GetDto(dtos, printers[2]).SpoolInfo;
        blackHoled.Should().NotBeNull();
        blackHoled!.ActiveSpoolId.Should().Be(3);
        blackHoled.HasActiveSpool.Should().BeTrue();
        blackHoled.SpoolName.Should().BeNull();
    }

    private static CompletePrinterDto GetDto(IReadOnlyCollection<CompletePrinterDto> dtos, Printer printer) =>
        dtos.Single(d => d.Id == printer.Id);

    private static Printer CreatePrinter(int spoolId) => new()
    {
        Id = Guid.NewGuid(),
        Name = $"Printer-{spoolId}",
        ServerUrl = $"http://printer-{spoolId}.local",
        BackendPort = 7125,
        Backend = (int)PrinterBackend.Moonraker,
        CurrentSpoolId = spoolId,
    };

    private static AppDbContext CreateDbContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"PrintersServiceSpoolmanFallbackConcurrencyTests_{Guid.NewGuid():N}")
            .Options;

        return new AppDbContext(options);
    }

    private static SpoolmanStatusCache CreateStatusCache(ISpoolmanService spoolmanService, TimeProvider timeProvider)
    {
        var services = new ServiceCollection();
        _ = services.AddScoped(_ => spoolmanService);
        ServiceProvider provider = services.BuildServiceProvider();
        return new SpoolmanStatusCache(
            new MemoryCache(new MemoryCacheOptions()),
            timeProvider,
            provider.GetRequiredService<IServiceScopeFactory>());
    }

    private static PrintersService CreateService(AppDbContext db, List<Printer> printers, ISpoolmanStatusCache statusCache)
    {
        var printersRepository = new Mock<IPrintersRepository>();
        _ = printersRepository
            .Setup(r => r.GetAllWithIncludesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(printers);

        var camerasRepository = new Mock<ICameraRepository>();
        _ = camerasRepository
            .Setup(r => r.GetEnabledAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var unitOfWork = new Mock<IUnitOfWork>();
        _ = unitOfWork.Setup(u => u.Printers).Returns(printersRepository.Object);
        _ = unitOfWork.Setup(u => u.Cameras).Returns(camerasRepository.Object);

        var statusReader = new Mock<IPrinterStatusCacheReader>();
        _ = statusReader
            .Setup(r => r.GetAllStatuses())
            .Returns(new Dictionary<Guid, PrinterStatusDto>());

        return new PrintersService(
            unitOfWork.Object,
            db,
            Mock.Of<IBackendClientFactory>(),
            Mock.Of<IBackendCapabilityFactory>(),
            Mock.Of<Farm.Infrastructure.Services.Catalog.ICatalogService>(),
            Mock.Of<IHttpClientFactory>(),
            NullLogger<PrintersService>.Instance,
            Mock.Of<IPrinterStatusBroadcaster>(),
            Mock.Of<IMultiPrinterStatusCoordinator>(),
            Mock.Of<IPrinterStatusClientFactory>(),
            statusReader.Object,
            Mock.Of<Farm.Infrastructure.Services.Locations.ILocationService>(),
            Mock.Of<Farm.Infrastructure.Services.Security.ISensitiveDataProtector>(),
            Mock.Of<ISpoolmanService>(MockBehavior.Strict),
            Mock.Of<Farm.Infrastructure.Services.Cameras.IGo2RtcService>(),
            Mock.Of<Farm.Infrastructure.Services.StorageManagement.IStoragePathService>(),
            Mock.Of<Farm.Infrastructure.Services.Spoolman.IFilamentCoverageSpoolResolver>(),
            coverageBroadcaster: null,
            activityAccumulator: null,
            configuration: null,
            membershipNotifier: null,
            spoolmanStatusCache: statusCache);
    }
}
