using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Models;
using Farm.Slicer.Module.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Farm.Slicer.Module.Tests.Slicing;

/// <summary>
/// Round-trips the calibration columns added by issue #1938 (<see cref="SliceJob.CalibrationMethod"/>,
/// <see cref="SliceJob.CalibrationParamsJson"/>) through a real EF Core-backed
/// <see cref="SlicerDbContext"/>, so the accompanying migration is exercised by a test assembly
/// instead of only being verified by <c>dotnet ef migrations has-pending-model-changes</c>.
/// </summary>
public sealed class DbSlicerJobQueueCalibrationFieldsTests : IAsyncDisposable
{
    private readonly string _databasePath =
        Path.Join(Path.GetTempPath(), $"printfarmer-queue-calibration-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task EnqueueAsync_WithCalibrationFields_PersistsAndRoundTripsThroughRealDatabase()
    {
        string connectionString = $"Data Source={_databasePath}";
        Guid jobId = Guid.NewGuid();
        const string calibrationMethod = "temperature_tower";
        const string calibrationParamsJson = """{"start_temperature":220,"temperature_step":5}""";

        await using SlicerDbContext context = CreateContext(connectionString);
        _ = await context.Database.EnsureCreatedAsync();
        var queue = new DbSlicerJobQueue(new EfSliceJobRepository(context));

        await queue.EnqueueAsync(new DistributedSlicingJob
        {
            Id = jobId,
            UserId = Guid.NewGuid(),
            ModelFileUrl = new Uri("calibration:temperature_tower", UriKind.RelativeOrAbsolute),
            ModelFileName = "temperature_tower.drc",
            EngineType = SlicerEngineType.OrcaSlicer,
            CalibrationMethod = calibrationMethod,
            CalibrationParamsJson = calibrationParamsJson,
        });

        SliceJob persisted = await context.SliceJobs.AsNoTracking().SingleAsync(job => job.Id == jobId);
        DistributedSlicingJob? loaded = await queue.GetJobAsync(jobId);

        _ = persisted.CalibrationMethod.Should().Be(calibrationMethod);
        _ = persisted.CalibrationParamsJson.Should().Be(calibrationParamsJson);

        // This is the defining constraint from issue #1938: a calibration job is still an ordinary
        // slice job. The columns exist independently of the printer/toolhead calibration saga
        // fields, which must remain unset so SlicePrintBridgeController's IsCalibrationSlice(job)
        // gate never trips for these jobs.
        _ = persisted.CalibrationProjectId.Should().BeNull();
        _ = persisted.CalibrationAttemptId.Should().BeNull();
        _ = persisted.CalibrationOrchestrationId.Should().BeNull();

        _ = loaded.Should().NotBeNull();
        _ = loaded!.CalibrationMethod.Should().Be(calibrationMethod);
        _ = loaded.CalibrationParamsJson.Should().Be(calibrationParamsJson);
    }

    [Fact]
    public async Task EnqueueAsync_WithoutCalibration_PersistsNullCalibrationColumns()
    {
        string connectionString = $"Data Source={_databasePath}";
        Guid jobId = Guid.NewGuid();

        await using SlicerDbContext context = CreateContext(connectionString);
        _ = await context.Database.EnsureCreatedAsync();
        var queue = new DbSlicerJobQueue(new EfSliceJobRepository(context));

        await queue.EnqueueAsync(new DistributedSlicingJob
        {
            Id = jobId,
            UserId = Guid.NewGuid(),
            ModelFileUrl = new Uri("file:///model.stl"),
            ModelFileName = "model.stl",
            EngineType = SlicerEngineType.OrcaSlicer,
        });

        SliceJob persisted = await context.SliceJobs.AsNoTracking().SingleAsync(job => job.Id == jobId);

        _ = persisted.CalibrationMethod.Should().BeNull();
        _ = persisted.CalibrationParamsJson.Should().BeNull();
    }

    private static SlicerDbContext CreateContext(string connectionString)
    {
        DbContextOptions<SlicerDbContext> options = new DbContextOptionsBuilder<SlicerDbContext>()
            .UseSqlite(connectionString)
            .Options;
        return new SlicerDbContext(options);
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);
        return ValueTask.CompletedTask;
    }
}
