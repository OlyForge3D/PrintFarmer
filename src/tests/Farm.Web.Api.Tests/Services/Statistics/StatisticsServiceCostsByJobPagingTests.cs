using System.Data.Common;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Statistics;
using Farm.Infrastructure.Telemetry;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using Xunit.Sdk;

namespace Farm.Web.Api.Tests.Services.Statistics;

/// <summary>
/// Tests for the keyset (seek) pagination added to <see cref="StatisticsService.GetCostsByJobAsync"/>
/// (issue #1734): flat per-page query cost regardless of traversal depth, no rows skipped or
/// duplicated across page boundaries when <c>ActualEndTime</c> values tie, and the server-side
/// page-size cap is enforced regardless of the requested page size.
/// </summary>
public class StatisticsServiceCostsByJobPagingTests
{
    [Fact]
    public async Task GetCostsByJobAsync_PageTraversal_UsesFlatQueryCostRegardlessOfDepth()
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

        const int totalJobs = 25;
        const int pageSize = 5;
        for (int i = 0; i < totalJobs; i++)
        {
            db.PrintJobs.Add(CreateCompletedJob($"job-{i:D2}.gcode", now.AddMinutes(-i), 1.0m + i));
        }
        await db.SaveChangesAsync();
        interceptor.Reset();

        var service = new StatisticsService(db);
        string? cursor = null;
        List<Guid> seenIds = [];
        int pagesFetched = 0;
        int[] commandCountsPerPage = new int[totalJobs / pageSize];

        do
        {
            interceptor.Reset();
            CostByJobPageDto page = await service.GetCostsByJobAsync(null, cursor: cursor, pageSize: pageSize);
            commandCountsPerPage[pagesFetched] = interceptor.CommandCount;
            seenIds.AddRange(page.Items.Select(i => i.JobId));
            cursor = page.NextCursor;
            pagesFetched++;
        }
        while (cursor is not null);

        Assert.Equal(totalJobs / pageSize, pagesFetched);
        Assert.Equal(totalJobs, seenIds.Count);
        Assert.Equal(totalJobs, seenIds.Distinct().Count());

        // Flat cost: every page — regardless of how deep into the result set it is — costs
        // exactly one database round trip, not a cost that grows with depth (issue #1734).
        Assert.All(commandCountsPerPage, count => Assert.Equal(1, count));
    }

    [Fact]
    public async Task GetCostsByJobAsync_TiedCompletedAt_NoRowsSkippedOrDuplicatedAcrossPageBoundary()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        DateTime tiedCompletedAt = DateTime.UtcNow;

        const int tiedJobCount = 7;
        const int pageSize = 3;
        for (int i = 0; i < tiedJobCount; i++)
        {
            // All rows share the exact same ActualEndTime, forcing the Id tiebreak to be
            // exercised across every page boundary.
            db.PrintJobs.Add(CreateCompletedJob($"tied-{i}.gcode", tiedCompletedAt, 1.0m + i));
        }
        await db.SaveChangesAsync();

        var service = new StatisticsService(db);
        string? cursor = null;
        List<Guid> seenIds = [];
        int safetyLimit = tiedJobCount + 2;

        do
        {
            CostByJobPageDto page = await service.GetCostsByJobAsync(null, cursor: cursor, pageSize: pageSize);
            seenIds.AddRange(page.Items.Select(i => i.JobId));
            cursor = page.NextCursor;
        }
        while (cursor is not null && --safetyLimit > 0);

        Assert.Equal(tiedJobCount, seenIds.Count);
        Assert.Equal(tiedJobCount, seenIds.Distinct().Count());
    }

    [Fact]
    public async Task GetCostsByJobAsync_RequestedPageSizeExceedsMax_IsClampedServerSide()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        DateTime now = DateTime.UtcNow;

        int totalJobs = StatisticsService.MaxCostsByJobPageSize + 5;
        for (int i = 0; i < totalJobs; i++)
        {
            db.PrintJobs.Add(CreateCompletedJob($"cap-{i}.gcode", now.AddSeconds(-i), 1.0m));
        }
        await db.SaveChangesAsync();

        var fakeTelemetry = new FakeTelemetryService();
        var service = new StatisticsService(db, fakeTelemetry);

        // A hand-crafted request for far more than the server-side maximum must still be
        // clamped down — the endpoint cannot be made unbounded by caller input (issue #1734).
        CostByJobPageDto page = await service.GetCostsByJobAsync(null, pageSize: 1_000_000);

        Assert.Equal(StatisticsService.MaxCostsByJobPageSize, page.Items.Count);
        Assert.NotNull(page.NextCursor);

        FakeTelemetryService.PagedQueryCall call = Assert.Single(fakeTelemetry.PagedQueryCalls);
        Assert.Equal("costs/by-job", call.Endpoint);
        Assert.Equal(StatisticsService.MaxCostsByJobPageSize, call.RowCount);
        Assert.True(call.CappedToMaxPageSize);
        Assert.True(call.PayloadBytes > 0);
    }

    [Fact]
    public async Task GetCostsByJobAsync_SmallRequest_RecordsTelemetryWithoutCapping()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        DateTime now = DateTime.UtcNow;

        db.PrintJobs.Add(CreateCompletedJob("small.gcode", now, 1.0m));
        await db.SaveChangesAsync();

        var fakeTelemetry = new FakeTelemetryService();
        var service = new StatisticsService(db, fakeTelemetry);

        CostByJobPageDto page = await service.GetCostsByJobAsync(null, pageSize: 50);

        Assert.Single(page.Items);
        Assert.Null(page.NextCursor);

        FakeTelemetryService.PagedQueryCall call = Assert.Single(fakeTelemetry.PagedQueryCalls);
        Assert.Equal(1, call.RowCount);
        Assert.False(call.CappedToMaxPageSize);
    }

    [Fact]
    public async Task GetCostsByJobAsync_InvalidCursor_ThrowsArgumentException()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var service = new StatisticsService(db);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.GetCostsByJobAsync(null, cursor: "not-a-valid-cursor!!"));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    [InlineData("sqlserver")]
    public void CostsByJobSeekPredicate_TranslatesAcrossSupportedProviders(string provider)
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
        DateTime cursorCompletedAt = DateTime.UtcNow;
        Guid cursorJobId = Guid.NewGuid();

        string sql = db.Set<PrintJob>()
            .Where(j => j.Status == PrintJobStatus.Completed && j.TotalCostUsd.HasValue)
            .Where(j =>
                (j.ActualEndTime ?? DateTime.MinValue) < cursorCompletedAt ||
                ((j.ActualEndTime ?? DateTime.MinValue) == cursorCompletedAt && j.Id.CompareTo(cursorJobId) < 0))
            .OrderByDescending(j => j.ActualEndTime ?? DateTime.MinValue)
            .ThenByDescending(j => j.Id)
            .Take(StatisticsService.DefaultCostsByJobPageSize + 1)
            .ToQueryString();

        Assert.Contains("ORDER BY", sql, StringComparison.OrdinalIgnoreCase);
    }

    private static PrintJob CreateCompletedJob(string name, DateTime actualEndTime, decimal totalCostUsd)
    {
        DateTime now = DateTime.UtcNow;
        return new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = name,
            Status = PrintJobStatus.Completed,
            QueuedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            ActualEndTime = actualEndTime,
            TotalCostUsd = totalCostUsd,
            MaterialCostUsd = totalCostUsd * 0.5m,
            EnergyCostUsd = totalCostUsd * 0.2m,
            MachineTimeCostUsd = totalCostUsd * 0.2m,
            LaborCostUsd = totalCostUsd * 0.1m,
            FilamentName = "PLA",
        };
    }

    private sealed class FakeTelemetryService : IPrintFarmerTelemetryService
    {
        public sealed record PagedQueryCall(string Endpoint, int RowCount, long PayloadBytes, bool CappedToMaxPageSize);

        public List<PagedQueryCall> PagedQueryCalls { get; } = [];

        public System.Diagnostics.Activity? StartActivity(string name, System.Diagnostics.ActivityKind kind = System.Diagnostics.ActivityKind.Internal) => null;

        public void RecordApiCall(string endpoint, string method, int statusCode, TimeSpan duration)
        {
        }

        public void RecordPrinterOperation(string operation, string printerId, bool success)
        {
        }

        public void RecordSlicerOperation(string operation, string engine, bool success, TimeSpan? duration = null)
        {
        }

        public void RecordFileOperation(string operation, string fileType, long? fileSize = null)
        {
        }

        public void RecordDatabaseOperation(string table, string operation, int recordCount)
        {
        }

        public void RecordPagedQuery(string endpoint, int rowCount, long payloadBytes, bool cappedToMaxPageSize)
        {
            PagedQueryCalls.Add(new PagedQueryCall(endpoint, rowCount, payloadBytes, cappedToMaxPageSize));
        }
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
    }
}
