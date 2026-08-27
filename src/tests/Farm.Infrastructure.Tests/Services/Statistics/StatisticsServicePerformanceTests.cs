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
using Xunit.Sdk;

namespace Farm.Infrastructure.Tests.Services.Statistics;

public class StatisticsServicePerformanceTests
{
    [Fact]
    public async Task GetSummaryAsync_UsesTwoDatabaseCommandsAndPreservesAggregateMetrics()
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
        db.PrintJobs.AddRange(
            new PrintJob
            {
                Id = Guid.NewGuid(),
                Name = "completed-1.gcode",
                Status = PrintJobStatus.Completed,
                QueuedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
                ActualCost = 0.1m,
                ActualFilamentUsage = 2.5d,
                ActualPrintTime = TimeSpan.FromMinutes(30),
            },
            new PrintJob
            {
                Id = Guid.NewGuid(),
                Name = "completed-2.gcode",
                Status = PrintJobStatus.Completed,
                QueuedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
                ActualCost = 0.1m,
                ActualFilamentUsage = 2.5d,
                ActualPrintTime = TimeSpan.FromMinutes(30),
            },
            new PrintJob
            {
                Id = Guid.NewGuid(),
                Name = "completed-3.gcode",
                Status = PrintJobStatus.Completed,
                QueuedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
                ActualCost = 0.1m,
                ActualFilamentUsage = 2.5d,
                ActualPrintTime = TimeSpan.FromMinutes(30),
            },
            new PrintJob
            {
                Id = Guid.NewGuid(),
                Name = "failed-1.gcode",
                Status = PrintJobStatus.Failed,
                QueuedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
                ActualCost = 0.1m,
                ActualFilamentUsage = 2.5d,
                ActualPrintTime = TimeSpan.FromMinutes(30),
            },
            new PrintJob
            {
                Id = Guid.NewGuid(),
                Name = "failed-2.gcode",
                Status = PrintJobStatus.Failed,
                QueuedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
                ActualCost = 0.1m,
                ActualFilamentUsage = 2.5d,
                ActualPrintTime = TimeSpan.FromMinutes(30),
            },
            new PrintJob
            {
                Id = Guid.NewGuid(),
                Name = "cancelled.gcode",
                Status = PrintJobStatus.Cancelled,
                QueuedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
                ActualCost = 0.1m,
                ActualFilamentUsage = 2.5d,
                ActualPrintTime = TimeSpan.FromMinutes(30),
            },
            new PrintJob
            {
                Id = Guid.NewGuid(),
                Name = "queued.gcode",
                Status = PrintJobStatus.Queued,
                QueuedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
                ActualCost = 0.1m,
                ActualFilamentUsage = 2.5d,
                ActualPrintTime = null,
            });
        await db.SaveChangesAsync();
        interceptor.Reset();

        StatisticsSummaryDto result = await new StatisticsService(db).GetSummaryAsync(null);

        Assert.Equal(7, result.TotalJobs);
        Assert.Equal(3, result.CompletedJobs);
        Assert.Equal(2, result.FailedJobs);
        Assert.Equal(1, result.CancelledJobs);
        Assert.Equal(50d, result.SuccessRate);
        Assert.Equal(0.7m, result.TotalCost);
        Assert.Equal(17.5d, result.TotalFilamentGrams);
        Assert.Equal(3d, result.TotalPrintHours);
        Assert.Equal(2, interceptor.CommandCount);
    }

    [Fact]
    public async Task GetSummaryAsync_WithNoJobs_ReturnsEmptyMetricsUsingTwoDatabaseCommands()
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
        interceptor.Reset();

        StatisticsSummaryDto result = await new StatisticsService(db).GetSummaryAsync(null);

        Assert.Equal(0, result.TotalJobs);
        Assert.Equal(0, result.CompletedJobs);
        Assert.Equal(0, result.FailedJobs);
        Assert.Equal(0, result.CancelledJobs);
        Assert.Equal(0d, result.SuccessRate);
        Assert.Equal(0m, result.TotalCost);
        Assert.Equal(0d, result.TotalFilamentGrams);
        Assert.Equal(0d, result.TotalPrintHours);
        Assert.Equal(2, interceptor.CommandCount);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    [InlineData("sqlserver")]
    public void SummaryAggregate_TranslatesAcrossSupportedProviders(string provider)
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
            default:
                throw new XunitException($"Unsupported provider test case: {provider}");
        }

        using var db = new AppDbContext(optionsBuilder.Options);
        string sql = StatisticsService.BuildSummaryAggregateQuery(db.Set<PrintJob>()).ToQueryString();
        string ticksSql = db.Set<PrintJob>()
            .Where(j => j.ActualPrintTime.HasValue)
            .Select(j => j.ActualPrintTime!.Value.Ticks)
            .ToQueryString();

        Assert.Contains("GROUP BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COUNT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ActualPrintTime", ticksSql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    [InlineData("sqlserver")]
    public void CostsSummaryAggregate_TranslatesAcrossSupportedProviders(string provider)
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
            default:
                throw new XunitException($"Unsupported provider test case: {provider}");
        }

        using var db = new AppDbContext(optionsBuilder.Options);
        string sql = StatisticsService.BuildCostsSummaryAggregateQuery(db.Set<PrintJob>()).ToQueryString();

        Assert.Contains("GROUP BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SUM", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COUNT", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetCostsSummaryAsync_UsesTwoDatabaseCommandsAndMatchesInMemoryAggregation()
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

        var completedJobs = new[]
        {
            new PrintJob
            {
                Id = Guid.NewGuid(),
                Name = "cost-1.gcode",
                Status = PrintJobStatus.Completed,
                QueuedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
                ActualEndTime = now,
                TotalCostUsd = 10.111m,
                MaterialCostUsd = 5.001m,
                EnergyCostUsd = 2.002m,
                MachineTimeCostUsd = 2.003m,
                LaborCostUsd = 1.105m,
                FilamentName = "PLA-Red",
            },
            new PrintJob
            {
                Id = Guid.NewGuid(),
                Name = "cost-2.gcode",
                Status = PrintJobStatus.Completed,
                QueuedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
                ActualEndTime = now,
                TotalCostUsd = 4.220m,
                MaterialCostUsd = 1.500m,
                EnergyCostUsd = 1.000m,
                MachineTimeCostUsd = 1.000m,
                LaborCostUsd = 0.720m,
                FilamentName = "PETG-Black",
            },
            new PrintJob
            {
                Id = Guid.NewGuid(),
                Name = "cost-3-no-cost-data.gcode",
                Status = PrintJobStatus.Completed,
                QueuedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
                ActualEndTime = now,
                TotalCostUsd = null,
            },
            new PrintJob
            {
                Id = Guid.NewGuid(),
                Name = "cost-4-failed.gcode",
                Status = PrintJobStatus.Failed,
                QueuedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
                ActualEndTime = now,
                TotalCostUsd = 99.0m,
                MaterialCostUsd = 50.0m,
                EnergyCostUsd = 20.0m,
            },
        };
        db.PrintJobs.AddRange(completedJobs);
        await db.SaveChangesAsync();
        interceptor.Reset();

        CostStatisticsSummaryDto result = await new StatisticsService(db).GetCostsSummaryAsync(null);

        // Two round trips: the GroupBy(_ => 1)-equivalent aggregate, plus the
        // most-expensive-material lookup. No per-row entity materialization.
        Assert.Equal(2, interceptor.CommandCount);

        // Exact decimal equality against values manually summed from only the
        // two completed jobs that have cost data (matches pre-change semantics).
        Assert.Equal(14.331m, result.TotalCostUsd);
        Assert.Equal(2, result.JobsWithCostData);
        Assert.Equal(6.501m, result.TotalMaterialCostUsd);
        Assert.Equal(3.002m, result.TotalEnergyCostUsd);
        Assert.Equal(3.003m, result.TotalMachineTimeCostUsd);
        Assert.Equal(1.825m, result.TotalLaborCostUsd);
        Assert.Equal(14.331m / 2, result.AverageCostPerJobUsd);
        Assert.Equal("PLA-Red", result.MostExpensiveMaterial);
        Assert.Equal(10.111m, result.MostExpensiveMaterialCost);
    }

    [Fact]
    public async Task GetCostsSummaryAsync_WithEmptyDateRange_ReturnsAllZerosNotNull()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        DateTime now = DateTime.UtcNow;

        db.PrintJobs.Add(new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "outside-range.gcode",
            Status = PrintJobStatus.Completed,
            QueuedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            ActualEndTime = now.AddDays(-100),
            TotalCostUsd = 42.0m,
            MaterialCostUsd = 20.0m,
            EnergyCostUsd = 10.0m,
            MachineTimeCostUsd = 8.0m,
            LaborCostUsd = 4.0m,
            FilamentName = "PLA",
        });
        await db.SaveChangesAsync();

        // A date range that excludes every job, exercising the empty-aggregate path.
        CostStatisticsSummaryDto result = await new StatisticsService(db)
            .GetCostsSummaryAsync(null, startDate: now.AddDays(-1), endDate: now);

        Assert.Equal(0m, result.TotalCostUsd);
        Assert.Equal(0, result.JobsWithCostData);
        Assert.Equal(0m, result.TotalMaterialCostUsd);
        Assert.Equal(0m, result.TotalEnergyCostUsd);
        Assert.Equal(0m, result.TotalMachineTimeCostUsd);
        Assert.Equal(0m, result.TotalLaborCostUsd);
        Assert.Equal(0m, result.AverageCostPerJobUsd);
        Assert.Null(result.MostExpensiveMaterial);
        Assert.Equal(0m, result.MostExpensiveMaterialCost);
    }

    private sealed class CommandCountingInterceptor : DbCommandInterceptor
    {
        public int CommandCount { get; private set; }

        public void Reset()
        {
            CommandCount = 0;
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            CommandCount++;

            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            CommandCount++;

            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result)
        {
            CommandCount++;
            return result;
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            CommandCount++;
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            CommandCount++;
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            CommandCount++;
            return ValueTask.FromResult(result);
        }
    }
}
