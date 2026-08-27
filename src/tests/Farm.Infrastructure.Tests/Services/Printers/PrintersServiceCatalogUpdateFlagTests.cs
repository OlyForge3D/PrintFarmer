using Farm.Backend.Plugin.Core;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Printers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.Printers;

/// <summary>
/// Regression coverage for issue #2061 round-2 review (Bishop, BLOCKING): the rotation fix makes
/// <c>PrintStatsSyncHostedService</c>/<c>MaintenanceAlertHostedService</c> call
/// <c>MarkStatsSyncAttemptedAsync</c>/<c>MarkMaintenanceAlertEvaluatedAsync</c> unconditionally on
/// every printer, which auto-creates a <see cref="PrinterServiceState"/> row (with
/// <see cref="PrinterServiceState.LastModelSyncAt"/> left <c>null</c>) purely as a rotation-cursor
/// side effect. <see cref="PrintersService.GetAllSummaryDtosAsync"/>'s "catalog update available"
/// predicate used to rely on <c>ServiceState != null</c> as a proxy for "never synced, so don't
/// flag" — coalescing a null <c>LastModelSyncAt</c> to <see cref="DateTime.MinValue"/>. Once every
/// printer eventually gets a <see cref="PrinterServiceState"/> row from the rotation cursor alone,
/// that coalesce made the flag true for essentially every printer with a model, fleet-wide,
/// regardless of whether the model was ever actually synced. The fix checks the VALUE of
/// <see cref="PrinterServiceState.LastModelSyncAt"/> directly instead of using row-existence as a
/// proxy, so a rotation-cursor-only row (no real model sync yet) must never flip the flag.
/// </summary>
public class PrintersServiceCatalogUpdateFlagTests
{
    [Fact]
    public async Task GetAllSummaryDtosAsync_ServiceStateExistsOnlyFromRotationCursor_HasCatalogUpdateStaysFalse()
    {
        // The printer's ServiceState row exists (as it eventually will for every printer once the
        // rotation cursor has touched it at least once) but LastModelSyncAt is still null — i.e.
        // the row was created purely as a side effect of MarkStatsSyncAttemptedAsync /
        // MarkMaintenanceAlertEvaluatedAsync, never by an actual model sync.
        (AppDbContext db, Guid printerId) = await SeedAsync(
            modelUpdatedAt: DateTime.UtcNow.AddDays(-1),
            serviceStateExists: true,
            lastModelSyncAt: null);

        PrintersService service = CreateService(db);

        PrinterSummaryDto[] summaries = await service.GetAllSummaryDtosAsync(CancellationToken.None);

        summaries.Should().ContainSingle(s => s.Id == printerId)
            .Which.HasCatalogUpdate.Should().BeFalse(
                "a PrinterServiceState row created only as a rotation-cursor side effect (issue #2061) " +
                "must not be mistaken for 'catalog update available'");
    }

    [Fact]
    public async Task GetAllSummaryDtosAsync_NoServiceStateAtAll_HasCatalogUpdateStaysFalse()
    {
        // Sanity check: a printer that has never been touched by rotation or model sync at all
        // (no PrinterServiceState row whatsoever) must behave exactly as it did before the fix.
        (AppDbContext db, Guid printerId) = await SeedAsync(
            modelUpdatedAt: DateTime.UtcNow.AddDays(-1),
            serviceStateExists: false,
            lastModelSyncAt: null);

        PrintersService service = CreateService(db);

        PrinterSummaryDto[] summaries = await service.GetAllSummaryDtosAsync(CancellationToken.None);

        summaries.Should().ContainSingle(s => s.Id == printerId)
            .Which.HasCatalogUpdate.Should().BeFalse();
    }

    [Fact]
    public async Task GetAllSummaryDtosAsync_ModelUpdatedAfterActualLastModelSync_HasCatalogUpdateIsTrue()
    {
        // True-positive case must still work: a real, prior model sync exists, but the model was
        // updated again afterwards, so a catalog update genuinely is available.
        (AppDbContext db, Guid printerId) = await SeedAsync(
            modelUpdatedAt: DateTime.UtcNow,
            serviceStateExists: true,
            lastModelSyncAt: DateTime.UtcNow.AddDays(-1));

        PrintersService service = CreateService(db);

        PrinterSummaryDto[] summaries = await service.GetAllSummaryDtosAsync(CancellationToken.None);

        summaries.Should().ContainSingle(s => s.Id == printerId)
            .Which.HasCatalogUpdate.Should().BeTrue(
                "the model was updated after the printer's last real model sync, so a catalog update genuinely is available");
    }

    [Fact]
    public async Task GetAllSummaryDtosAsync_ModelSyncedAfterModelUpdate_HasCatalogUpdateIsFalse()
    {
        // The printer's last real model sync happened AFTER the model's most recent update, so no
        // catalog update is pending.
        (AppDbContext db, Guid printerId) = await SeedAsync(
            modelUpdatedAt: DateTime.UtcNow.AddDays(-1),
            serviceStateExists: true,
            lastModelSyncAt: DateTime.UtcNow);

        PrintersService service = CreateService(db);

        PrinterSummaryDto[] summaries = await service.GetAllSummaryDtosAsync(CancellationToken.None);

        summaries.Should().ContainSingle(s => s.Id == printerId)
            .Which.HasCatalogUpdate.Should().BeFalse();
    }

    private static async Task<(AppDbContext Db, Guid PrinterId)> SeedAsync(
        DateTime modelUpdatedAt,
        bool serviceStateExists,
        DateTime? lastModelSyncAt)
    {
        AppDbContext db = CreateDbContext();

        Guid modelId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();

        db.PrinterModels.Add(new PrinterModel
        {
            Id = modelId,
            Name = "Test model",
            UpdatedAt = modelUpdatedAt
        });

        db.Printers.Add(new Printer
        {
            Id = printerId,
            Name = "Catalog flag test printer",
            Backend = (int)PrinterBackend.Moonraker,
            ModelId = modelId,
            ServerUrl = "http://catalog-flag-test.local"
        });

        if (serviceStateExists)
        {
            db.PrinterServiceStates.Add(new PrinterServiceState
            {
                PrinterId = printerId,
                LastModelSyncAt = lastModelSyncAt
            });
        }

        await db.SaveChangesAsync();
        return (db, printerId);
    }

    private static AppDbContext CreateDbContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"PrintersServiceCatalogUpdateFlagTests_{Guid.NewGuid():N}")
            .Options;

        return new AppDbContext(options);
    }

    private static PrintersService CreateService(AppDbContext db)
    {
        var statusCache = new Mock<IPrinterStatusCacheReader>();
        statusCache.Setup(cache => cache.GetAllStatuses())
            .Returns(new Dictionary<Guid, PrinterStatusDto>());

        return new PrintersService(
            Mock.Of<IUnitOfWork>(),
            db,
            Mock.Of<IBackendClientFactory>(),
            Mock.Of<IBackendCapabilityFactory>(),
            Mock.Of<Farm.Infrastructure.Services.Catalog.ICatalogService>(),
            Mock.Of<IHttpClientFactory>(),
            NullLogger<PrintersService>.Instance,
            Mock.Of<IPrinterStatusBroadcaster>(),
            Mock.Of<IMultiPrinterStatusCoordinator>(),
            Mock.Of<IPrinterStatusClientFactory>(),
            statusCache.Object,
            Mock.Of<Farm.Infrastructure.Services.Locations.ILocationService>(),
            Mock.Of<Farm.Infrastructure.Services.Security.ISensitiveDataProtector>(),
            Mock.Of<Farm.Infrastructure.Services.Interfaces.ISpoolmanService>(),
            Mock.Of<Farm.Infrastructure.Services.Cameras.IGo2RtcService>(),
            Mock.Of<Farm.Infrastructure.Services.StorageManagement.IStoragePathService>(),
            Mock.Of<Farm.Infrastructure.Services.Spoolman.IFilamentCoverageSpoolResolver>(),
            coverageBroadcaster: null,
            activityAccumulator: null,
            configuration: null,
            membershipNotifier: null);
    }
}
