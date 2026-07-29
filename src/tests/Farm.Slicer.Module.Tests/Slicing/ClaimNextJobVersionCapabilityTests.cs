using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Slicer.Module.Data.Repositories;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Farm.Slicer.Module.Tests.Slicing;

/// <summary>
/// Verifies the version-scoped capability contract holds at the claim boundary
/// (issue #578, rubber-duck finding B): a worker advertising only its own
/// version must never claim a job pinned to a different version.
/// Uses an isolated in-memory SQLite database — no full web factory.
/// </summary>
public class ClaimNextJobVersionCapabilityTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SlicerDbContext _db;

    public ClaimNextJobVersionCapabilityTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        DbContextOptions<SlicerDbContext> options = new DbContextOptionsBuilder<SlicerDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new SlicerDbContext(options);
        _ = _db.Database.EnsureCreated();
    }

    private static string CapsJson(params string[] caps) => JsonSerializer.Serialize(caps);

    private async Task<Guid> InsertQueuedJobAsync(string requiredCapsJson, string? engineVersion = null)
    {
        SliceJob job = new SliceJob
        {
            Id = Guid.NewGuid(),
            Status = SliceJobStatus.Queued,
            QueuedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            UserId = Guid.NewGuid(),
            ModelFileName = "m.stl",
            ModelFileUrl = "http://x/m.stl",
            SlicerEngine = 0,
            SlicerEngineVersion = engineVersion,
            RequiredCapabilitiesJson = requiredCapsJson,
            Priority = 1,
        };
        _ = _db.SliceJobs.Add(job);
        _ = await _db.SaveChangesAsync();
        return job.Id;
    }

    [Fact(DisplayName = "Wrong-version worker cannot claim job pinned to a different version")]
    public async Task WrongVersionWorker_DoesNotClaimPinnedJob()
    {
        // Pinned job: only carries versioned tag, as SubmitAsync now derives.
        Guid pinnedToCurrent = await InsertQueuedJobAsync(CapsJson("orcaslicer:2.4.2"), "2.4.2");

        EfSliceJobRepository repo = new EfSliceJobRepository(_db);

        // A worker advertising a different version must not claim the current-version job.
        SliceJob? claimed = await repo.ClaimNextJobAsync(
            WorkerClaimIdentity.CreateUnattested(
                Guid.NewGuid(),
                ["orcaslicer", "orcaslicer:9.9.9", "stl-processing"]),
            30,
            3,
            CancellationToken.None);

        _ = claimed.Should().BeNull("a mismatched worker must not pick up a version-pinned job");

        // Sanity: matching worker CAN claim.
        SliceJob? claimedRight = await repo.ClaimNextJobAsync(
            WorkerClaimIdentity.CreateUnattested(
                Guid.NewGuid(),
                ["orcaslicer", "orcaslicer:2.4.2", "stl-processing"]),
            30,
            3,
            CancellationToken.None);

        _ = claimedRight.Should().NotBeNull();
        _ = claimedRight!.Id.Should().Be(pinnedToCurrent);
    }

    [Fact(DisplayName = "Unpinned job (generic capability only) is claimable by any orcaslicer worker")]
    public async Task UnpinnedJob_ClaimableByAnyOrcaslicerWorker()
    {
        Guid unpinned = await InsertQueuedJobAsync(CapsJson("orcaslicer"), engineVersion: null);

        EfSliceJobRepository repo = new EfSliceJobRepository(_db);

        SliceJob? claimed = await repo.ClaimNextJobAsync(
            WorkerClaimIdentity.CreateUnattested(
                Guid.NewGuid(),
                ["orcaslicer", "orcaslicer:2.4.2", "stl-processing"]),
            30,
            3,
            CancellationToken.None);

        _ = claimed.Should().NotBeNull();
        _ = claimed!.Id.Should().Be(unpinned);
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
