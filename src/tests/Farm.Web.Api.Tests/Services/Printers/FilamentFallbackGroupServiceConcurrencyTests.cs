using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Printers;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Printers;

/// <summary>
/// Deterministic concurrency regression for issue #711 (F6 remediation, FIX 3): a
/// unique-constraint violation that slips past the service's check-then-write guard —
/// a racing create/update committing between the in-memory duplicate check and
/// <c>SaveChanges</c> — must surface as <see cref="FilamentFallbackGroupValidationException"/>
/// (HTTP 4xx), not an unhandled <see cref="DbUpdateException"/> (HTTP 500).
///
/// The race is made deterministic with a one-shot <see cref="ISaveChangesInterceptor"/>
/// that inserts the conflicting row over a second connection to the same shared-cache
/// in-memory SQLite database, after the service's duplicate check has already read
/// (and found nothing) but before the service's own INSERT commits.
/// </summary>
public sealed class FilamentFallbackGroupServiceConcurrencyTests : IAsyncLifetime
{
    private readonly string _dataSource = $"Data Source=fbgroup_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;

    public async Task InitializeAsync()
    {
        // Keep one connection open for the lifetime of the test so the shared-cache
        // in-memory database is not torn down between context instances.
        _keepAlive = new SqliteConnection(_dataSource);
        await _keepAlive.OpenAsync();

        await using AppDbContext setup = CreateContext();
        _ = await setup.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _keepAlive.DisposeAsync();
    }

    private AppDbContext CreateContext(IInterceptor? interceptor = null)
    {
        SqliteConnection conn = new(_dataSource);
        DbContextOptionsBuilder<AppDbContext> builder = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(conn);
        if (interceptor is not null)
        {
            _ = builder.AddInterceptors(interceptor);
        }

        return new AppDbContext(builder.Options);
    }

    [Fact]
    public async Task CreateAsync_RacingDuplicateName_TranslatesToValidationException()
    {
        (Guid printerId, Guid t0, Guid t1) = await SeedPrinterAsync();

        OneShotInterceptor interceptor = new(() =>
        {
            using AppDbContext racer = CreateContext();
            _ = racer.FilamentFallbackGroups.Add(new FilamentFallbackGroup
            {
                Id = Guid.NewGuid(),
                PrinterId = printerId,
                Name = "PLA Chain",
                MaterialType = "PLA",
                DisplayOrder = 0,
            });
            _ = racer.SaveChanges();
        });

        await using AppDbContext serviceDb = CreateContext(interceptor);
        FilamentFallbackGroupService service = new(serviceDb, NullLogger<FilamentFallbackGroupService>.Instance);

        Func<Task> act = () => service.CreateAsync(
            printerId,
            new CreateFilamentFallbackGroupRequest("PLA Chain", "PLA", null, [t0, t1]),
            CancellationToken.None);

        // The racing insert bypasses the case-insensitive pre-check, so the DB unique index
        // is the last line of defense. FIX 3 must translate the resulting DbUpdateException.
        _ = await act.Should().ThrowAsync<FilamentFallbackGroupValidationException>()
            .WithMessage("*already exists*");
    }

    private async Task<(Guid PrinterId, Guid T0, Guid T1)> SeedPrinterAsync()
    {
        await using AppDbContext db = CreateContext();
        string suffix = Guid.NewGuid().ToString("N")[..8];
        Manufacturer mfg = new() { Id = Guid.NewGuid(), Name = $"Mfg-{suffix}" };
        PrinterModel model = new() { Id = Guid.NewGuid(), ManufacturerId = mfg.Id, Name = $"Model-{suffix}" };
        Printer printer = new()
        {
            Id = Guid.NewGuid(),
            Name = $"Printer-{suffix}",
            ManufacturerId = mfg.Id,
            ModelId = model.Id,
            ServerUrl = "http://10.0.0.10",
            IsEnabled = true,
        };
        Toolhead t0 = new() { Id = Guid.NewGuid(), PrinterId = printer.Id, Index = 0, Name = "T0", ToolheadType = ToolheadType.Physical };
        Toolhead t1 = new() { Id = Guid.NewGuid(), PrinterId = printer.Id, Index = 1, Name = "T1", ToolheadType = ToolheadType.Physical };

        _ = db.Manufacturers.Add(mfg);
        _ = db.PrinterModels.Add(model);
        _ = db.Printers.Add(printer);
        db.Toolheads.AddRange(t0, t1);
        _ = await db.SaveChangesAsync();

        return (printer.Id, t0.Id, t1.Id);
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
