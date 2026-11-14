using System.Diagnostics;
using System.Text;
using Farm.Infrastructure.Domain;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Tests.TestInfrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Farm.Slicer.Module.Tests.Data;

public sealed class SliceJobStatusProjectionTests(ITestOutputHelper output)
{
    private const int PageSize = 100;
    private const int WarmupRequests = 5;
    private const int MeasuredRequests = 20;

    [Fact]
    public async Task GetJobStatusProjectionsByUserIdAsync_FiltersOrdersPaginatesAndSelectsOnlyStatusFields()
    {
        await using SqliteConnection connection = new("DataSource=:memory:");
        await connection.OpenAsync();
        CountingDbCommandInterceptor interceptor = new();
        DbContextOptions<SlicerDbContext> options = new DbContextOptionsBuilder<SlicerDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;

        await using SlicerDbContext db = new(options);
        await db.Database.EnsureCreatedAsync();
        Guid ownerId = Guid.NewGuid();
        Guid otherOwnerId = Guid.NewGuid();
        DateTime queuedAt = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        SliceJob[] jobs = Enumerable.Range(0, 5)
            .Select(index => CreateJob(
                index,
                index == 4 ? otherOwnerId : ownerId,
                queuedAt.AddMinutes(index),
                profileSnapshotBytes: 64))
            .ToArray();
        db.SliceJobs.AddRange(jobs);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        interceptor.Reset();

        EfSliceJobRepository repository = new(db);
        IReadOnlyList<SliceJobStatusProjection> result =
            await repository.GetJobStatusProjectionsByUserIdAsync(ownerId, limit: 2, offset: 1);

        Assert.Equal(new[] { jobs[2].Id, jobs[1].Id }, result.Select(job => job.Id));
        Assert.Equal(jobs[2].Status, result[0].Status);
        Assert.Equal(jobs[2].PrinterId, result[0].PrinterId);
        Assert.Equal(jobs[2].ErrorMessage, result[0].ErrorMessage);
        Assert.Equal(jobs[2].LayoutDegradationReason, result[0].LayoutDegradationReason);
        Assert.Equal(jobs[2].FailureReason, result[0].FailureReason);
        Assert.Equal(jobs[2].ModelFileName, result[0].ModelFileName);
        Assert.Equal(jobs[2].SlicerEngineName, result[0].SlicerEngineName);
        Assert.Empty(db.ChangeTracker.Entries<SliceJob>());

        string sql = Assert.Single(interceptor.Commands);
        Assert.Contains("SELECT", sql, StringComparison.OrdinalIgnoreCase);
        foreach (string unusedColumn in UnusedJsonColumns)
        {
            Assert.DoesNotContain(unusedColumn, sql, StringComparison.OrdinalIgnoreCase);
        }

        IReadOnlyList<SliceJobStatusProjection> emptyPage =
            await repository.GetJobStatusProjectionsByUserIdAsync(ownerId, limit: 10, offset: 100);
        Assert.Empty(emptyPage);
        Assert.Empty(db.ChangeTracker.Entries<SliceJob>());
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task MeasureFullEntityAndStatusProjection_For100JobsWithSmallAndLargeSnapshots()
    {
        foreach ((string scenario, int profileSnapshotBytes) in new[]
                 {
                     ("small", 64),
                     ("large", 10 * 1024),
                 })
        {
            await using SqliteConnection connection = new("DataSource=:memory:");
            await connection.OpenAsync();
            CountingDbCommandInterceptor interceptor = new();
            DbContextOptions<SlicerDbContext> options = new DbContextOptionsBuilder<SlicerDbContext>()
                .UseSqlite(connection)
                .AddInterceptors(interceptor)
                .Options;

            await using SlicerDbContext db = new(options);
            await db.Database.EnsureCreatedAsync();
            Guid ownerId = Guid.NewGuid();
            DateTime queuedAt = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
            SliceJob[] jobs = Enumerable.Range(0, PageSize)
                .Select(index => CreateJob(
                    index,
                    ownerId,
                    queuedAt.AddMinutes(index),
                    profileSnapshotBytes))
                .ToArray();
            db.SliceJobs.AddRange(jobs);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            EfSliceJobRepository repository = new(db);
            Measurement[] fullEntity = await MeasureAsync(
                db,
                repository,
                interceptor,
                ownerId,
                projected: false);
            Measurement[] projection = await MeasureAsync(
                db,
                repository,
                interceptor,
                ownerId,
                projected: true);

            string projectionSql = projection[0].Sql;
            foreach (string unusedColumn in UnusedJsonColumns)
            {
                Assert.DoesNotContain(unusedColumn, projectionSql, StringComparison.OrdinalIgnoreCase);
            }

            Assert.DoesNotContain("ModelFileUrl", projectionSql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ResultFileUrl", projectionSql, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(db.ChangeTracker.Entries<SliceJob>());

            long excludedSnapshotBytes = jobs.Sum(job => UnusedJsonColumns
                .Select(column => GetSnapshotValue(job, column))
                .Where(value => value is not null)
                .Sum(value => Encoding.UTF8.GetByteCount(value!)));

            WriteSummary(scenario, profileSnapshotBytes, excludedSnapshotBytes, "full-entity", fullEntity);
            WriteSummary(scenario, profileSnapshotBytes, excludedSnapshotBytes, "projection", projection);
            output.WriteLine($"projection SQL ({scenario}): {projectionSql}");
        }
    }

    private async Task<Measurement[]> MeasureAsync(
        SlicerDbContext db,
        EfSliceJobRepository repository,
        CountingDbCommandInterceptor interceptor,
        Guid ownerId,
        bool projected)
    {
        List<Measurement> samples = new(MeasuredRequests);

        for (int request = 0; request < WarmupRequests + MeasuredRequests; request++)
        {
            db.ChangeTracker.Clear();
            interceptor.Reset();
            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            Stopwatch stopwatch = Stopwatch.StartNew();

            if (projected)
            {
                _ = await repository.GetJobStatusProjectionsByUserIdAsync(ownerId, PageSize);
            }
            else
            {
                _ = await repository.GetByUserIdAsync(ownerId, PageSize);
            }

            stopwatch.Stop();
            long allocatedBytes = GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore;
            string sql = Assert.Single(interceptor.Commands);
            if (request >= WarmupRequests)
            {
                samples.Add(new Measurement(
                    stopwatch.Elapsed.TotalMilliseconds,
                    allocatedBytes,
                    sql));
            }
        }

        return samples.ToArray();
    }

    private void WriteSummary(
        string scenario,
        int profileSnapshotBytes,
        long excludedSnapshotBytes,
        string label,
        IReadOnlyList<Measurement> samples)
    {
        double[] elapsed = samples.Select(sample => sample.ElapsedMilliseconds).Order().ToArray();
        long[] allocations = samples.Select(sample => sample.AllocatedBytes).Order().ToArray();
        output.WriteLine(
            $"n={PageSize} snapshots={scenario}({profileSnapshotBytes}B/profile column) {label}: " +
            $"unused-json-page-bytes={excludedSnapshotBytes}; " +
            $"latency-ms p50={Percentile(elapsed, 0.50):F3} p95={Percentile(elapsed, 0.95):F3}; " +
            $"allocated-bytes p50={Percentile(allocations, 0.50)} p95={Percentile(allocations, 0.95)}; " +
            $"samples={samples.Count}");
    }

    private static SliceJob CreateJob(
        int index,
        Guid ownerId,
        DateTime queuedAt,
        int profileSnapshotBytes)
    {
        string smallSnapshot = CreateJsonPayload(64);
        string profileSnapshot = CreateJsonPayload(profileSnapshotBytes);
        Guid id = Guid.NewGuid();

        return new SliceJob
        {
            Id = id,
            UserId = ownerId,
            ModelFileUrl = $"/models/{id:N}",
            ModelFileName = $"model-{index:D3}.3mf",
            Status = SliceJobStatus.Failed,
            QueuedAt = queuedAt,
            ErrorMessage = "synthetic diagnostic",
            SlicerProfileJson = profileSnapshot,
            MachineProfileJson = profileSnapshot,
            ProcessProfileJson = profileSnapshot,
            FilamentProfileJson = profileSnapshot,
            RequiredCapabilitiesJson = smallSnapshot,
            ExtruderFilamentProfilesJson = smallSnapshot,
            ModelTransformJson = smallSnapshot,
            ModelFileUrlsJson = smallSnapshot,
            ModelFileTransformsJson = smallSnapshot,
            CalibrationParamsJson = smallSnapshot,
        };
    }

    private static string CreateJsonPayload(int byteCount) =>
        $"{{\"v\":\"{new string('x', byteCount - 8)}\"}}";

    private static string? GetSnapshotValue(SliceJob job, string column) => column switch
    {
        nameof(SliceJob.SlicerProfileJson) => job.SlicerProfileJson,
        nameof(SliceJob.RequiredCapabilitiesJson) => job.RequiredCapabilitiesJson,
        nameof(SliceJob.MachineProfileJson) => job.MachineProfileJson,
        nameof(SliceJob.ProcessProfileJson) => job.ProcessProfileJson,
        nameof(SliceJob.FilamentProfileJson) => job.FilamentProfileJson,
        nameof(SliceJob.ExtruderFilamentProfilesJson) => job.ExtruderFilamentProfilesJson,
        nameof(SliceJob.ModelTransformJson) => job.ModelTransformJson,
        nameof(SliceJob.ModelFileUrlsJson) => job.ModelFileUrlsJson,
        nameof(SliceJob.ModelFileTransformsJson) => job.ModelFileTransformsJson,
        nameof(SliceJob.CalibrationParamsJson) => job.CalibrationParamsJson,
        _ => throw new ArgumentOutOfRangeException(nameof(column), column, null),
    };

    private static double Percentile(double[] sortedValues, double percentile) =>
        sortedValues[Math.Max(0, (int)Math.Ceiling(percentile * sortedValues.Length) - 1)];

    private static long Percentile(long[] sortedValues, double percentile) =>
        sortedValues[Math.Max(0, (int)Math.Ceiling(percentile * sortedValues.Length) - 1)];

    private static readonly string[] UnusedJsonColumns =
    [
        nameof(SliceJob.SlicerProfileJson),
        nameof(SliceJob.RequiredCapabilitiesJson),
        nameof(SliceJob.MachineProfileJson),
        nameof(SliceJob.ProcessProfileJson),
        nameof(SliceJob.FilamentProfileJson),
        nameof(SliceJob.ExtruderFilamentProfilesJson),
        nameof(SliceJob.ModelTransformJson),
        nameof(SliceJob.ModelFileUrlsJson),
        nameof(SliceJob.ModelFileTransformsJson),
        nameof(SliceJob.CalibrationParamsJson),
    ];

    private sealed record Measurement(
        double ElapsedMilliseconds,
        long AllocatedBytes,
        string Sql);
}
