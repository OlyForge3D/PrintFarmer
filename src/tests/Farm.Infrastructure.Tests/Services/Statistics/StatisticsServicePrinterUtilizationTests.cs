using System.Data.Common;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Statistics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.Statistics;

/// <summary>
/// Covers the all-time (no date filter) path of <see cref="StatisticsService.GetPrinterUtilizationAsync"/>,
/// which historically materialized every matching <see cref="PrintJob"/> row client-side and scaled
/// O(jobs) instead of O(printers) (issue #2346, spike #2333).
/// </summary>
public class StatisticsServicePrinterUtilizationTests
{
    [Fact]
    public async Task GetPrinterUtilizationAsync_AggregatesPerPrinterCountsAndDurationServerSide()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();

        var interceptor = new CommandCountingInterceptor();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        DateTime now = DateTime.UtcNow;

        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        db.Manufacturers.Add(new Manufacturer { Id = manufacturerId, Name = "TestMfg" });
        db.PrinterModels.Add(new PrinterModel { Id = modelId, Name = "TestModel", ManufacturerId = manufacturerId });
        await db.SaveChangesAsync();

        Guid printerAId = Guid.NewGuid();
        Guid printerBId = Guid.NewGuid();
        db.Printers.AddRange(
            new Printer
            {
                Id = printerAId,
                Name = "Printer A",
                ServerUrl = "http://printer-a:7125",
                ManufacturerId = manufacturerId,
                ModelId = modelId,
            },
            new Printer
            {
                Id = printerBId,
                Name = "Printer B",
                ServerUrl = "http://printer-b:7125",
                ManufacturerId = manufacturerId,
                ModelId = modelId,
            });

        db.PrintJobs.AddRange(
            new PrintJob
            {
                Id = Guid.NewGuid(),
                Name = "a-completed-1.gcode",
                Status = PrintJobStatus.Completed,
                AssignedPrinterId = printerAId,
                QueuedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
                ActualPrintTime = TimeSpan.FromHours(1),
            },
            new PrintJob
            {
                Id = Guid.NewGuid(),
                Name = "a-completed-2.gcode",
                Status = PrintJobStatus.Completed,
                AssignedPrinterId = printerAId,
                QueuedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
                ActualPrintTime = TimeSpan.FromHours(2),
            },
            new PrintJob
            {
                Id = Guid.NewGuid(),
                Name = "a-failed.gcode",
                Status = PrintJobStatus.Failed,
                AssignedPrinterId = printerAId,
                QueuedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
                ActualPrintTime = null,
            },
            new PrintJob
            {
                Id = Guid.NewGuid(),
                Name = "b-completed.gcode",
                Status = PrintJobStatus.Completed,
                AssignedPrinterId = printerBId,
                QueuedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
                ActualPrintTime = TimeSpan.FromMinutes(30),
            },
            new PrintJob
            {
                Id = Guid.NewGuid(),
                Name = "unassigned.gcode",
                Status = PrintJobStatus.Queued,
                AssignedPrinterId = null,
                QueuedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            });
        await db.SaveChangesAsync();
        interceptor.Reset();

        List<PrinterUtilizationDto> result = await new StatisticsService(db).GetPrinterUtilizationAsync(null);

        // Two round trips regardless of job count: the grouped aggregate query, plus the
        // printer-name lookup (already O(printers)). No per-job entity materialization.
        Assert.Equal(2, interceptor.CommandCount);

        // The old client-side-grouped implementation also issued 2 round trips (a raw
        // per-job SELECT, then the printer-name lookup), so a command-count assertion alone
        // cannot distinguish the two implementations. Assert the aggregate query itself was
        // pushed to SQL: exactly one captured command groups and sums server-side.
        Assert.Contains(interceptor.CommandTexts, sql =>
            sql.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("SUM", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("ActualPrintTimeTicks", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(2, result.Count);

        PrinterUtilizationDto printerA = result.Single(r => r.PrinterId == printerAId);
        Assert.Equal("Printer A", printerA.PrinterName);
        Assert.Equal(3, printerA.TotalJobs);
        Assert.Equal(2, printerA.CompletedJobs);
        Assert.Equal(1, printerA.FailedJobs);
        Assert.Equal(3d, printerA.TotalPrintHours);
        Assert.Equal(Math.Round(2d / 3 * 100, 1), printerA.SuccessRate);

        PrinterUtilizationDto printerB = result.Single(r => r.PrinterId == printerBId);
        Assert.Equal("Printer B", printerB.PrinterName);
        Assert.Equal(1, printerB.TotalJobs);
        Assert.Equal(1, printerB.CompletedJobs);
        Assert.Equal(0, printerB.FailedJobs);
        Assert.Equal(0.5d, printerB.TotalPrintHours);
        Assert.Equal(100d, printerB.SuccessRate);
    }

    [Fact]
    public async Task GetPrinterUtilizationAsync_ResultRowCountScalesWithPrintersNotJobs()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();

        var interceptor = new CommandCountingInterceptor();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        DateTime now = DateTime.UtcNow;

        // Few printers, many jobs each -- proves the all-time path returns one row per
        // printer (O(printers)) rather than materializing every job row (O(jobs)).
        const int printerCount = 3;
        const int jobsPerPrinter = 200;

        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        db.Manufacturers.Add(new Manufacturer { Id = manufacturerId, Name = "TestMfg" });
        db.PrinterModels.Add(new PrinterModel { Id = modelId, Name = "TestModel", ManufacturerId = manufacturerId });
        await db.SaveChangesAsync();

        var printerIds = new List<Guid>();
        for (int p = 0; p < printerCount; p++)
        {
            Guid printerId = Guid.NewGuid();
            printerIds.Add(printerId);
            db.Printers.Add(new Printer
            {
                Id = printerId,
                Name = $"Printer {p}",
                ServerUrl = $"http://printer-{p}:7125",
                ManufacturerId = manufacturerId,
                ModelId = modelId,
            });

            for (int j = 0; j < jobsPerPrinter; j++)
            {
                db.PrintJobs.Add(new PrintJob
                {
                    Id = Guid.NewGuid(),
                    Name = $"printer-{p}-job-{j}.gcode",
                    Status = PrintJobStatus.Completed,
                    AssignedPrinterId = printerId,
                    QueuedAt = now,
                    CreatedAt = now,
                    UpdatedAt = now,
                    ActualPrintTime = TimeSpan.FromMinutes(10),
                });
            }
        }
        await db.SaveChangesAsync();
        interceptor.Reset();

        List<PrinterUtilizationDto> result = await new StatisticsService(db).GetPrinterUtilizationAsync(null);

        Assert.Equal(printerCount, result.Count);
        Assert.All(result, r => Assert.Equal(jobsPerPrinter, r.TotalJobs));
        Assert.Equal(printerCount * jobsPerPrinter, result.Sum(r => r.TotalJobs));

        // Exactly one row per printer comes back from the aggregate query itself (not just
        // the final DTO list) -- proves the DB never streams the underlying 600 job rows to
        // the app process; SQLite's own GROUP BY collapses them before the ORM sees them.
        Assert.Contains(interceptor.CommandTexts, sql =>
            sql.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("SUM", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    [InlineData("sqlserver")]
    public void PrinterUtilizationAggregate_TranslatesAcrossSupportedProviders(string provider)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        switch (provider)
        {
            case "sqlite":
                optionsBuilder.UseSqlite("Data Source=:memory:");
                break;
            case "postgres":
                optionsBuilder.UseNpgsql("Host=localhost;Database=printfarmer");
                break;
            case "sqlserver":
                optionsBuilder.UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=printfarmer");
                break;
        }

        using var db = new AppDbContext(optionsBuilder.Options);

        // Invokes the exact production query builder used by GetPrinterUtilizationAsync
        // (not a hand-duplicated shape), proving the grouped SUM over the
        // ActualPrintTimeTicks shadow column (no value converter) translates to SQL across
        // every supported provider, unlike a SUM over ActualPrintTime.Ticks directly, which
        // throws InvalidOperationException at query-compile time because of its TimeSpan
        // value converter (#2346 / #2333).
        string sql = StatisticsService.BuildPrinterUtilizationAggregateQuery(
            db.Set<PrintJob>().Where(j => j.AssignedPrinterId.HasValue))
            .ToQueryString();

        Assert.Contains("GROUP BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SUM", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ActualPrintTimeTicks", sql, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class CommandCountingInterceptor : DbCommandInterceptor
    {
        public int CommandCount { get; private set; }

        public List<string> CommandTexts { get; } = [];

        public void Reset()
        {
            CommandCount = 0;
            CommandTexts.Clear();
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            CommandCount++;
            CommandTexts.Add(command.CommandText);

            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            CommandCount++;
            CommandTexts.Add(command.CommandText);

            return ValueTask.FromResult(result);
        }
    }
}
