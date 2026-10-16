using Farm.Backend.Plugin.Core;
using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Security;
using Farm.Infrastructure.Services.Spoolman;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.Printers;

/// <summary>
/// Deterministic concurrency regression for issue #3323: the MMU gate-row repair now runs
/// from a startup backfill as well as on demand, so two repairs (backfill vs. an explicit
/// ensure request, or two API replicas) can race to insert the same missing
/// (PrinterId, Index) gate. The unique index must win without duplicate rows, and the
/// losing repair must report success and leave its scoped context usable.
///
/// The race is made deterministic with a one-shot <see cref="ISaveChangesInterceptor"/>
/// that inserts the conflicting gate over a second connection to the same shared-cache
/// in-memory SQLite database, after the service has read the gate set but before its own
/// INSERT commits.
/// </summary>
public sealed class PrintersServiceMmuGateRepairConcurrencyTests : IAsyncLifetime, IDisposable
{
    private readonly string _dataSource = $"Data Source=mmugate_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private readonly List<SqliteConnection> _connectionsToDispose = [];
    private SqliteConnection _keepAlive = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(_dataSource);
        await _keepAlive.OpenAsync();

        await using AppDbContext setup = CreateContext();
        _ = await setup.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _keepAlive.DisposeAsync();

        foreach (SqliteConnection connection in _connectionsToDispose)
        {
            await connection.DisposeAsync();
        }
    }

    public void Dispose()
    {
        _keepAlive.Dispose();

        foreach (SqliteConnection connection in _connectionsToDispose)
        {
            connection.Dispose();
        }
    }

    [Fact]
    public async Task EnsureMmuToolheadsAsync_RacingRepair_ReturnsSuccessWithoutDuplicateGates()
    {
        Guid printerId = await SeedThreeGatePrinterAsync();
        OneShotInterceptor interceptor = new(() => InsertRacingGate(printerId, index: 4));

        await using AppDbContext serviceDb = CreateContext(interceptor);
        PrintersService service = CreateService(serviceDb);

        CommandResult result = await service.EnsureMmuToolheadsAsync(printerId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Message.Should().Contain("concurrently");
        await AssertSingleGateSetAsync(printerId);

        // The losing inserts were detached, so the same scoped context must still save cleanly
        // and now observe the racer's committed gate instead of replaying the duplicate.
        CommandResult second = await service.EnsureMmuToolheadsAsync(printerId, CancellationToken.None);
        second.Success.Should().BeTrue();
        second.Message.Should().Contain("already has 4");
        serviceDb.ChangeTracker.Entries<Toolhead>()
            .Should().NotContain(e => e.State == EntityState.Added);
        await AssertSingleGateSetAsync(printerId);
    }

    [Fact]
    public async Task RepairMissingMmuGatesAsync_RacingRepair_CountsPrinterAsRepaired()
    {
        Guid printerId = await SeedThreeGatePrinterAsync();
        OneShotInterceptor interceptor = new(() => InsertRacingGate(printerId, index: 4));

        await using AppDbContext serviceDb = CreateContext(interceptor);
        PrintersService service = CreateService(serviceDb);

        int repaired = await service.RepairMissingMmuGatesAsync(CancellationToken.None);

        repaired.Should().Be(1);
        await AssertSingleGateSetAsync(printerId);
    }

    [Fact]
    public async Task EnsureMmuToolheadsAsync_UnrelatedPrinterEditWinsRevisionRace_RetriesAndCreatesGate()
    {
        Guid printerId = await SeedThreeGatePrinterAsync();
        OneShotInterceptor interceptor = new(() => RenamePrinter(printerId, "adx5-1-renamed"));

        await using AppDbContext serviceDb = CreateContext(interceptor);
        PrintersService service = CreateService(serviceDb);

        CommandResult result = await service.EnsureMmuToolheadsAsync(printerId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Message.Should().Contain("Created 1 MMU gate");
        await AssertSingleGateSetAsync(printerId);

        await using AppDbContext verify = CreateContext();
        Printer persisted = await verify.Printers.AsNoTracking().SingleAsync(p => p.Id == printerId);
        persisted.Name.Should().Be("adx5-1-renamed", "the concurrent edit must not be overwritten by the retry");
    }

    private void RenamePrinter(Guid printerId, string name)
    {
        using AppDbContext racer = CreateContext();
        Printer printer = racer.Printers.Single(p => p.Id == printerId);
        printer.Name = name;
        _ = racer.SaveChanges();
    }

    private void InsertRacingGate(Guid printerId, int index)
    {
        using AppDbContext racer = CreateContext();
        _ = racer.Toolheads.Add(new Toolhead
        {
            Id = Guid.NewGuid(),
            PrinterId = printerId,
            Name = $"Gate {index}",
            Index = index,
            ToolheadType = ToolheadType.MmuGate,
            IsPrimary = false,
            UpdatedAt = DateTime.UtcNow,
        });
        _ = racer.SaveChanges();
    }

    private async Task AssertSingleGateSetAsync(Guid printerId)
    {
        await using AppDbContext verify = CreateContext();
        List<int> gateIndexes = await verify.Toolheads.AsNoTracking()
            .Where(t => t.PrinterId == printerId && t.ToolheadType == ToolheadType.MmuGate)
            .Select(t => t.Index)
            .OrderBy(i => i)
            .ToListAsync();
        gateIndexes.Should().Equal(1, 2, 3, 4);
    }

    private AppDbContext CreateContext(IInterceptor? interceptor = null)
    {
        SqliteConnection conn = new(_dataSource);
        _connectionsToDispose.Add(conn);
        DbContextOptionsBuilder<AppDbContext> builder = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(conn);
        if (interceptor is not null)
        {
            _ = builder.AddInterceptors(interceptor);
        }

        return new AppDbContext(builder.Options);
    }

    private async Task<Guid> SeedThreeGatePrinterAsync()
    {
        await using AppDbContext db = CreateContext();
        string suffix = Guid.NewGuid().ToString("N")[..8];
        Manufacturer mfg = new() { Id = Guid.NewGuid(), Name = $"Flashforge-{suffix}" };
        PrinterModel model = new() { Id = Guid.NewGuid(), ManufacturerId = mfg.Id, Name = $"AD5X-{suffix}" };
        Printer printer = new()
        {
            Id = Guid.NewGuid(),
            Name = "adx5-1",
            ManufacturerId = mfg.Id,
            ModelId = model.Id,
            ServerUrl = "http://10.0.0.20",
            BackendPort = 7125,
            Backend = (int)PrinterBackend.Moonraker,
            MultiMaterial = true,
            IsEnabled = true,
        };

        _ = db.Manufacturers.Add(mfg);
        _ = db.PrinterModels.Add(model);
        _ = db.Printers.Add(printer);
        db.Toolheads.Add(new Toolhead
        {
            Id = Guid.NewGuid(),
            PrinterId = printer.Id,
            Name = "Extruder",
            Index = 0,
            ToolheadType = ToolheadType.Physical,
            IsPrimary = true,
            UpdatedAt = DateTime.UtcNow,
        });
        for (int i = 1; i <= 3; i++)
        {
            db.Toolheads.Add(new Toolhead
            {
                Id = Guid.NewGuid(),
                PrinterId = printer.Id,
                Name = $"Gate {i}",
                Index = i,
                ToolheadType = ToolheadType.MmuGate,
                IsPrimary = false,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        _ = await db.SaveChangesAsync();
        return printer.Id;
    }

    private static PrintersService CreateService(AppDbContext db)
    {
        var uow = new AppUnitOfWork(db, Mock.Of<ISensitiveDataProtector>());
        return new PrintersService(
            uow,
            db,
            Mock.Of<IBackendClientFactory>(),
            Mock.Of<IBackendCapabilityFactory>(),
            Mock.Of<Farm.Infrastructure.Services.Catalog.ICatalogService>(),
            Mock.Of<IHttpClientFactory>(),
            NullLogger<PrintersService>.Instance,
            Mock.Of<IPrinterStatusBroadcaster>(),
            Mock.Of<IMultiPrinterStatusCoordinator>(),
            Mock.Of<IPrinterStatusClientFactory>(),
            Mock.Of<IPrinterStatusCacheReader>(),
            Mock.Of<Farm.Infrastructure.Services.Locations.ILocationService>(),
            Mock.Of<ISensitiveDataProtector>(),
            Mock.Of<ISpoolmanService>(),
            Mock.Of<Farm.Infrastructure.Services.Cameras.IGo2RtcService>(),
            Mock.Of<Farm.Infrastructure.Services.StorageManagement.IStoragePathService>(),
            Mock.Of<IFilamentCoverageSpoolResolver>(),
            null);
    }

    private sealed class OneShotInterceptor(Action onFirstSave) : SaveChangesInterceptor
    {
        private bool _fired;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!_fired)
            {
                _fired = true;
                onFirstSave();
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
