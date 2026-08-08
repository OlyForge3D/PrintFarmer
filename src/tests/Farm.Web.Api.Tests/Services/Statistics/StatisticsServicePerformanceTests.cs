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

namespace Farm.Web.Api.Tests.Services.Statistics;

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
