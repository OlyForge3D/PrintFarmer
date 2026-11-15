using System.Text.Json;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Models;
using Farm.Slicer.Module.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Farm.Slicer.Module.Tests.Slicing;

public sealed class NativeFilamentSnapshotQueueTests
{
    [Fact]
    public async Task EnqueueAsync_ExplicitFilamentSnapshots_PersistsAndLoadsOrderedDocumentsAndDigests()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new SlicerDbContext(
            new DbContextOptionsBuilder<SlicerDbContext>().UseSqlite(connection).Options);
        _ = await context.Database.EnsureCreatedAsync();
        var queue = new DbSlicerJobQueue(new EfSliceJobRepository(context));
        const string first = """{"name":"Same name","nozzle_temperature":["215"]}""";
        const string second = """{"name":"Same name","nozzle_temperature":["245"]}""";
        NativeFilamentProfile[] snapshots =
        [
            new(first, NativeSlicerProfiles.ComputeSha256(first)),
            new(second, NativeSlicerProfiles.ComputeSha256(second))
        ];
        var job = new DistributedSlicingJob
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            ModelFileUrl = new Uri("file:///model.stl"),
            ModelFileName = "model.stl",
            EngineType = SlicerEngineType.OrcaSlicer,
            NativeProfiles = NativeSlicerProfiles.FromJob(
                """{"name":"machine"}""", """{"name":"process"}""", first, null, null, null)! with
            {
                ExtruderFilaments = snapshots
            }
        };

        await queue.EnqueueAsync(job);
        context.ChangeTracker.Clear();
        SliceJob persisted = await context.SliceJobs.AsNoTracking().SingleAsync(value => value.Id == job.Id);
        DistributedSlicingJob? loaded = await queue.GetJobAsync(job.Id);

        _ = persisted.ExtruderFilamentProfilesJson.Should().NotBeNull();
        _ = JsonSerializer.Deserialize<List<NativeFilamentProfile>>(persisted.ExtruderFilamentProfilesJson!)
            .Should().Equal(snapshots);
        _ = loaded.Should().NotBeNull();
        _ = loaded!.NativeProfiles!.ExtruderFilaments.Should().Equal(snapshots);
        _ = loaded.NativeProfiles.MachineJson.Should().Be(job.NativeProfiles!.MachineJson);
        _ = loaded.NativeProfiles.ProcessJson.Should().Be(job.NativeProfiles.ProcessJson);
    }

    [Fact]
    public async Task GetJobAsync_CorruptPersistedSnapshots_RejectsRatherThanLoadingLegacyProfiles()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new SlicerDbContext(
            new DbContextOptionsBuilder<SlicerDbContext>().UseSqlite(connection).Options);
        _ = await context.Database.EnsureCreatedAsync();
        var job = new SliceJob
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            ModelFileUrl = "file:///model.stl",
            ModelFileName = "model.stl",
            MachineProfileJson = "{}",
            ProcessProfileJson = "{}",
            FilamentProfileJson = "{}",
            ExtruderFilamentProfilesJson = "[]",
        };
        _ = context.SliceJobs.Add(job);
        _ = await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var queue = new DbSlicerJobQueue(new EfSliceJobRepository(context));

        Func<Task> load = async () => { _ = await queue.GetJobAsync(job.Id); };

        _ = await load.Should().ThrowAsync<InvalidOperationException>();
    }
}
