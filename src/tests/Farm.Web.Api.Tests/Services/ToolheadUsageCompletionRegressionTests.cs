using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

/// <summary>
/// Regression tests for P1 bugs in multi-toolhead filament tracking completion path.
///
/// Bug #1: Duplicate PrintJobToolheadUsage records — completion must UPDATE existing
///          snapshot rows (created at dispatch) instead of inserting duplicates.
/// Bug #2: Wrong spool debited — completion must use the SNAPSHOTTED SpoolmanSpoolId,
///          not the live CurrentSpoolId from the toolhead.
///
/// These tests exercise the database-level behavior that PrintJobCompletionService's
/// FetchAndRecordFilamentUsageAsync must satisfy. The unique composite index on
/// (PrintJobId, ToolheadIndex) enforces Bug #1 at the schema level.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public class ToolheadUsageCompletionRegressionTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private Printer _testPrinter = null!;
    private Toolhead _toolheadT0 = null!;
    private Toolhead _toolheadT1 = null!;

    public ToolheadUsageCompletionRegressionTests()
    {
        _factory = new CustomWebApplicationFactory();
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
        await SeedPrinterWithToolheadsAsync();
    }

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
    }

    private async Task SeedPrinterWithToolheadsAsync()
    {
        using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "Test Mfg" };
        context.Manufacturers.Add(manufacturer);
        await context.SaveChangesAsync();

        var model = new PrinterModel
        {
            Id = Guid.NewGuid(),
            Name = "Dual-Extruder Test Model",
            ManufacturerId = manufacturer.Id
        };
        context.PrinterModels.Add(model);
        await context.SaveChangesAsync();

        _testPrinter = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "regression-test-printer",
            ServerUrl = "http://test.local",
            BackendPort = 7125,
            Backend = (int)PrinterBackend.Moonraker,
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id
        };
        context.Printers.Add(_testPrinter);
        await context.SaveChangesAsync();

        _toolheadT0 = new Toolhead
        {
            Id = Guid.NewGuid(),
            PrinterId = _testPrinter.Id,
            Name = "Extruder 1",
            Index = 0,
            IsPrimary = true,
            CurrentSpoolId = 100,
            CurrentMaterial = "PLA",
            CurrentFilamentColor = "#FF0000"
        };

        _toolheadT1 = new Toolhead
        {
            Id = Guid.NewGuid(),
            PrinterId = _testPrinter.Id,
            Name = "Extruder 2",
            Index = 1,
            IsPrimary = false,
            CurrentSpoolId = 200,
            CurrentMaterial = "PETG",
            CurrentFilamentColor = "#00FF00"
        };

        context.Toolheads.Add(_toolheadT0);
        context.Toolheads.Add(_toolheadT1);
        await context.SaveChangesAsync();
    }

    #region Bug #1 — Duplicate PrintJobToolheadUsage records

    [Fact]
    public async Task CompletionWithExistingSnapshots_UpdatesRowsInsteadOfCreatingDuplicates()
    {
        // Arrange: Simulate dispatch snapshot — create PrintJobToolheadUsage rows
        // with SlicerEstimateGrams but no FilamentUsageGrams yet (as SnapshotSlicerEstimatesAsync does)
        using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "dual-extruder-job.gcode",
            AssignedPrinterId = _testPrinter.Id,
            Status = PrintJobStatus.Printing,
            ActualStartTime = DateTime.UtcNow.AddHours(-1)
        };
        context.PrintJobs.Add(job);
        await context.SaveChangesAsync();

        // Dispatch snapshot rows — these exist before completion runs
        var snapshotT0 = new PrintJobToolheadUsage
        {
            Id = Guid.NewGuid(),
            PrintJobId = job.Id,
            ToolheadIndex = 0,
            SlicerEstimateGrams = 15.0,
            SpoolmanSpoolId = 100,
            FilamentName = "PLA",
            FilamentColor = "#FF0000",
            FilamentUsageGrams = null // Not yet populated — set at completion
        };

        var snapshotT1 = new PrintJobToolheadUsage
        {
            Id = Guid.NewGuid(),
            PrintJobId = job.Id,
            ToolheadIndex = 1,
            SlicerEstimateGrams = 8.5,
            SpoolmanSpoolId = 200,
            FilamentName = "PETG",
            FilamentColor = "#00FF00",
            FilamentUsageGrams = null
        };

        context.PrintJobToolheadUsages.Add(snapshotT0);
        context.PrintJobToolheadUsages.Add(snapshotT1);
        await context.SaveChangesAsync();

        // Act: Simulate what FetchAndRecordFilamentUsageAsync does — load existing,
        // update instead of inserting duplicates
        var existingUsages = await context.PrintJobToolheadUsages
            .Where(u => u.PrintJobId == job.Id)
            .ToListAsync();
        var existingByIndex = existingUsages.ToDictionary(u => u.ToolheadIndex);

        var perExtruderUsage = new Dictionary<int, double> { { 0, 12.3 }, { 1, 6.7 } };

        foreach (var (toolIndex, grams) in perExtruderUsage)
        {
            if (existingByIndex.TryGetValue(toolIndex, out var existing))
            {
                // Correct behavior: UPDATE existing row
                existing.FilamentUsageGrams = grams;
            }
            else
            {
                // Fallback: create new row (should NOT happen here)
                context.PrintJobToolheadUsages.Add(new PrintJobToolheadUsage
                {
                    Id = Guid.NewGuid(),
                    PrintJobId = job.Id,
                    ToolheadIndex = toolIndex,
                    FilamentUsageGrams = grams
                });
            }
        }

        // This must NOT throw DbUpdateException from duplicate index violation
        var saveAction = () => context.SaveChangesAsync();
        await saveAction.Should().NotThrowAsync<DbUpdateException>(
            "completion must update existing snapshot rows, not insert duplicates");

        // Assert: Still exactly 2 rows — no duplicates created
        var finalRows = await context.PrintJobToolheadUsages
            .Where(u => u.PrintJobId == job.Id)
            .OrderBy(u => u.ToolheadIndex)
            .ToListAsync();

        finalRows.Should().HaveCount(2, "row count must stay the same after completion update");

        // Assert: Both SlicerEstimateGrams AND FilamentUsageGrams populated on same row
        var t0 = finalRows.First(r => r.ToolheadIndex == 0);
        t0.SlicerEstimateGrams.Should().Be(15.0, "slicer estimate from dispatch must be preserved");
        t0.FilamentUsageGrams.Should().Be(12.3, "actual usage from completion must be recorded");
        t0.SpoolmanSpoolId.Should().Be(100, "spool ID from dispatch snapshot must be preserved");

        var t1 = finalRows.First(r => r.ToolheadIndex == 1);
        t1.SlicerEstimateGrams.Should().Be(8.5, "slicer estimate from dispatch must be preserved");
        t1.FilamentUsageGrams.Should().Be(6.7, "actual usage from completion must be recorded");
        t1.SpoolmanSpoolId.Should().Be(200, "spool ID from dispatch snapshot must be preserved");
    }

    [Fact]
    public async Task UniqueCompositeIndex_PreventsRawDuplicateInsertion()
    {
        // This test proves the schema-level guard: the unique composite index on
        // (PrintJobId, ToolheadIndex) will throw if code attempts to insert a duplicate.
        using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "duplicate-guard-test.gcode",
            AssignedPrinterId = _testPrinter.Id,
            Status = PrintJobStatus.Printing,
            ActualStartTime = DateTime.UtcNow
        };
        context.PrintJobs.Add(job);
        await context.SaveChangesAsync();

        // First row — this should succeed
        context.PrintJobToolheadUsages.Add(new PrintJobToolheadUsage
        {
            Id = Guid.NewGuid(),
            PrintJobId = job.Id,
            ToolheadIndex = 0,
            SlicerEstimateGrams = 10.0,
            SpoolmanSpoolId = 100
        });
        await context.SaveChangesAsync();

        // Second row with same (PrintJobId, ToolheadIndex) — this MUST throw
        context.PrintJobToolheadUsages.Add(new PrintJobToolheadUsage
        {
            Id = Guid.NewGuid(),
            PrintJobId = job.Id,
            ToolheadIndex = 0, // Same toolhead index — duplicate!
            FilamentUsageGrams = 12.0,
            SpoolmanSpoolId = 100
        });

        var insertDuplicate = () => context.SaveChangesAsync();
        await insertDuplicate.Should().ThrowAsync<DbUpdateException>(
            "the unique composite index on (PrintJobId, ToolheadIndex) must reject duplicates");
    }

    #endregion

    #region Bug #2 — Wrong spool debited (must use snapshotted spool, not live)

    [Fact]
    public async Task CompletionUsesSnapshotSpoolId_EvenWhenLiveToolheadSpoolChanged()
    {
        // Arrange: Dispatch snapshot captures spool 100 on T0 and spool 200 on T1
        using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "spool-swap-mid-print.gcode",
            AssignedPrinterId = _testPrinter.Id,
            Status = PrintJobStatus.Printing,
            ActualStartTime = DateTime.UtcNow.AddHours(-2)
        };
        context.PrintJobs.Add(job);
        await context.SaveChangesAsync();

        // Dispatch snapshot — captures spool IDs at dispatch time
        context.PrintJobToolheadUsages.Add(new PrintJobToolheadUsage
        {
            Id = Guid.NewGuid(),
            PrintJobId = job.Id,
            ToolheadIndex = 0,
            SlicerEstimateGrams = 20.0,
            SpoolmanSpoolId = 100, // <-- Snapshotted at dispatch: spool 100
            FilamentName = "PLA",
            FilamentColor = "#FF0000"
        });
        context.PrintJobToolheadUsages.Add(new PrintJobToolheadUsage
        {
            Id = Guid.NewGuid(),
            PrintJobId = job.Id,
            ToolheadIndex = 1,
            SlicerEstimateGrams = 10.0,
            SpoolmanSpoolId = 200, // <-- Snapshotted at dispatch: spool 200
            FilamentName = "PETG",
            FilamentColor = "#00FF00"
        });
        await context.SaveChangesAsync();

        // Simulate mid-print spool reassignment — user swaps spools while printing
        var liveT0 = await context.Toolheads.FirstAsync(t => t.PrinterId == _testPrinter.Id && t.Index == 0);
        liveT0.CurrentSpoolId = 999; // Changed to spool 999 mid-print!
        liveT0.CurrentMaterial = "ASA";
        liveT0.CurrentFilamentColor = "#0000FF";

        var liveT1 = await context.Toolheads.FirstAsync(t => t.PrinterId == _testPrinter.Id && t.Index == 1);
        liveT1.CurrentSpoolId = 888; // Changed to spool 888 mid-print!
        liveT1.CurrentMaterial = "TPU";
        liveT1.CurrentFilamentColor = "#FFFF00";
        await context.SaveChangesAsync();

        // Act: Simulate completion — load existing snapshots and update FilamentUsageGrams
        // The critical behavior: use SNAPSHOTTED SpoolmanSpoolId, not live toolhead data
        var existingUsages = await context.PrintJobToolheadUsages
            .Where(u => u.PrintJobId == job.Id)
            .ToListAsync();
        var existingByIndex = existingUsages.ToDictionary(u => u.ToolheadIndex);

        var perExtruderUsage = new Dictionary<int, double> { { 0, 18.5 }, { 1, 9.2 } };

        var consumptions = new List<(int spoolId, double grams)>();
        foreach (var (toolIndex, grams) in perExtruderUsage)
        {
            if (existingByIndex.TryGetValue(toolIndex, out var existing))
            {
                // Correct: only update FilamentUsageGrams, preserve SpoolmanSpoolId
                existing.FilamentUsageGrams = grams;

                // Build consumption list from the SNAPSHOTTED spool (NOT live toolhead)
                if (existing.SpoolmanSpoolId.HasValue && grams > 0)
                {
                    consumptions.Add((existing.SpoolmanSpoolId.Value, grams));
                }
            }
        }
        await context.SaveChangesAsync();

        // Assert: Snapshot rows still reference ORIGINAL spool IDs (100 and 200)
        var finalRows = await context.PrintJobToolheadUsages
            .Where(u => u.PrintJobId == job.Id)
            .OrderBy(u => u.ToolheadIndex)
            .ToListAsync();

        var t0 = finalRows.First(r => r.ToolheadIndex == 0);
        t0.SpoolmanSpoolId.Should().Be(100,
            "completion must use snapshotted spool ID (100), not live toolhead spool (999)");
        t0.FilamentUsageGrams.Should().Be(18.5);

        var t1 = finalRows.First(r => r.ToolheadIndex == 1);
        t1.SpoolmanSpoolId.Should().Be(200,
            "completion must use snapshotted spool ID (200), not live toolhead spool (888)");
        t1.FilamentUsageGrams.Should().Be(9.2);

        // Assert: Consumption list references snapshotted spools, not live spools
        consumptions.Should().HaveCount(2);
        consumptions.Should().Contain((100, 18.5),
            "filament debit must target original spool 100, not swapped spool 999");
        consumptions.Should().Contain((200, 9.2),
            "filament debit must target original spool 200, not swapped spool 888");
        consumptions.Should().NotContain(c => c.spoolId == 999,
            "live toolhead spool 999 must NOT be debited");
        consumptions.Should().NotContain(c => c.spoolId == 888,
            "live toolhead spool 888 must NOT be debited");
    }

    #endregion

    #region Fallback — Legacy jobs with no dispatch snapshot

    [Fact]
    public async Task CompletionWithNoSnapshots_CreatesNewRowsFromLiveToolheadData()
    {
        // Arrange: Legacy job — no SnapshotSlicerEstimatesAsync was called at dispatch
        // (e.g., gcode had no per-extruder metadata, or job predates the snapshot feature)
        using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "legacy-no-snapshot.gcode",
            AssignedPrinterId = _testPrinter.Id,
            Status = PrintJobStatus.Printing,
            ActualStartTime = DateTime.UtcNow.AddHours(-1)
        };
        context.PrintJobs.Add(job);
        await context.SaveChangesAsync();

        // No snapshot rows exist — verify this precondition
        var preExisting = await context.PrintJobToolheadUsages
            .Where(u => u.PrintJobId == job.Id)
            .CountAsync();
        preExisting.Should().Be(0, "legacy jobs have no dispatch snapshot rows");

        // Act: Simulate completion creating new rows from live toolhead data
        var existingUsages = await context.PrintJobToolheadUsages
            .Where(u => u.PrintJobId == job.Id)
            .ToListAsync();
        var existingByIndex = existingUsages.ToDictionary(u => u.ToolheadIndex);

        var toolheads = await context.Toolheads
            .Where(t => t.PrinterId == _testPrinter.Id)
            .OrderBy(t => t.Index)
            .ToListAsync();

        var perExtruderUsage = new Dictionary<int, double> { { 0, 14.0 }, { 1, 7.5 } };

        foreach (var (toolIndex, grams) in perExtruderUsage)
        {
            if (existingByIndex.TryGetValue(toolIndex, out var existing))
            {
                existing.FilamentUsageGrams = grams;
            }
            else
            {
                // No snapshot — create from live toolhead data (fallback path)
                var toolhead = toolheads.FirstOrDefault(t => t.Index == toolIndex);
                context.PrintJobToolheadUsages.Add(new PrintJobToolheadUsage
                {
                    Id = Guid.NewGuid(),
                    PrintJobId = job.Id,
                    ToolheadIndex = toolIndex,
                    SpoolmanSpoolId = toolhead?.CurrentSpoolId,
                    FilamentUsageGrams = grams,
                    FilamentName = toolhead?.CurrentMaterial,
                    FilamentColor = toolhead?.CurrentFilamentColor
                });
            }
        }
        await context.SaveChangesAsync();

        // Assert: New rows created from live toolhead data
        var finalRows = await context.PrintJobToolheadUsages
            .Where(u => u.PrintJobId == job.Id)
            .OrderBy(u => u.ToolheadIndex)
            .ToListAsync();

        finalRows.Should().HaveCount(2, "fallback path creates rows for each extruder");

        var t0 = finalRows.First(r => r.ToolheadIndex == 0);
        t0.SpoolmanSpoolId.Should().Be(100, "uses live toolhead spool when no snapshot exists");
        t0.FilamentUsageGrams.Should().Be(14.0);
        t0.FilamentName.Should().Be("PLA");
        t0.SlicerEstimateGrams.Should().BeNull("no slicer estimate snapshot for legacy jobs");

        var t1 = finalRows.First(r => r.ToolheadIndex == 1);
        t1.SpoolmanSpoolId.Should().Be(200, "uses live toolhead spool when no snapshot exists");
        t1.FilamentUsageGrams.Should().Be(7.5);
        t1.FilamentName.Should().Be("PETG");
        t1.SlicerEstimateGrams.Should().BeNull("no slicer estimate snapshot for legacy jobs");
    }

    [Fact]
    public async Task CompletionWithPartialSnapshots_UpdatesExistingAndCreatesForMissing()
    {
        // Arrange: Only T0 has a dispatch snapshot, T1 does not (e.g., slicer estimate
        // was zero for T1 so SnapshotSlicerEstimatesAsync skipped it)
        using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "partial-snapshot.gcode",
            AssignedPrinterId = _testPrinter.Id,
            Status = PrintJobStatus.Printing,
            ActualStartTime = DateTime.UtcNow.AddHours(-1)
        };
        context.PrintJobs.Add(job);
        await context.SaveChangesAsync();

        // Only T0 has a snapshot (T1 had zero slicer estimate so was skipped)
        context.PrintJobToolheadUsages.Add(new PrintJobToolheadUsage
        {
            Id = Guid.NewGuid(),
            PrintJobId = job.Id,
            ToolheadIndex = 0,
            SlicerEstimateGrams = 12.0,
            SpoolmanSpoolId = 100,
            FilamentName = "PLA",
            FilamentColor = "#FF0000"
        });
        await context.SaveChangesAsync();

        // Mid-print: user swaps T0 spool
        var liveT0 = await context.Toolheads.FirstAsync(t => t.PrinterId == _testPrinter.Id && t.Index == 0);
        liveT0.CurrentSpoolId = 777;
        await context.SaveChangesAsync();

        // Act: Completion reports usage on both T0 and T1
        var existingUsages = await context.PrintJobToolheadUsages
            .Where(u => u.PrintJobId == job.Id)
            .ToListAsync();
        var existingByIndex = existingUsages.ToDictionary(u => u.ToolheadIndex);

        var toolheads = await context.Toolheads
            .Where(t => t.PrinterId == _testPrinter.Id)
            .OrderBy(t => t.Index)
            .ToListAsync();

        var perExtruderUsage = new Dictionary<int, double> { { 0, 11.0 }, { 1, 3.5 } };

        foreach (var (toolIndex, grams) in perExtruderUsage)
        {
            if (existingByIndex.TryGetValue(toolIndex, out var existing))
            {
                existing.FilamentUsageGrams = grams;
            }
            else
            {
                var toolhead = toolheads.FirstOrDefault(t => t.Index == toolIndex);
                context.PrintJobToolheadUsages.Add(new PrintJobToolheadUsage
                {
                    Id = Guid.NewGuid(),
                    PrintJobId = job.Id,
                    ToolheadIndex = toolIndex,
                    SpoolmanSpoolId = toolhead?.CurrentSpoolId,
                    FilamentUsageGrams = grams,
                    FilamentName = toolhead?.CurrentMaterial,
                    FilamentColor = toolhead?.CurrentFilamentColor
                });
            }
        }
        await context.SaveChangesAsync();

        // Assert
        var finalRows = await context.PrintJobToolheadUsages
            .Where(u => u.PrintJobId == job.Id)
            .OrderBy(u => u.ToolheadIndex)
            .ToListAsync();

        finalRows.Should().HaveCount(2);

        // T0: Updated existing snapshot — uses SNAPSHOTTED spool 100, not live 777
        var t0 = finalRows.First(r => r.ToolheadIndex == 0);
        t0.SpoolmanSpoolId.Should().Be(100,
            "T0 must use snapshotted spool 100, not live spool 777");
        t0.SlicerEstimateGrams.Should().Be(12.0, "slicer estimate preserved");
        t0.FilamentUsageGrams.Should().Be(11.0, "actual usage recorded");

        // T1: No snapshot existed — uses live toolhead data (spool 200, unchanged)
        var t1 = finalRows.First(r => r.ToolheadIndex == 1);
        t1.SpoolmanSpoolId.Should().Be(200,
            "T1 uses live toolhead spool since no snapshot existed");
        t1.SlicerEstimateGrams.Should().BeNull("no slicer estimate for T1");
        t1.FilamentUsageGrams.Should().Be(3.5, "actual usage recorded");
    }

    #endregion
}
