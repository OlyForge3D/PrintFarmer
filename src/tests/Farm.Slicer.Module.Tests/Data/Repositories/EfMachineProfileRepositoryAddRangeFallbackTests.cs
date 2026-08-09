using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Tests.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Farm.Slicer.Module.Tests.Data.Repositories;

/// <summary>
/// Regression tests for the batch-commit-then-per-row-fallback pattern used by
/// <see cref="ProfilesService"/>'s seed loops (issue #1354). These use a real EF Core
/// <see cref="SlicerDbContext"/> (SQLite) instead of mocks, because the bug this guards
/// against — the change tracker still holding the whole failed batch as <c>Added</c> after a
/// failed <c>SaveChangesAsync</c>, causing a per-row retry to resubmit the entire poisoned
/// batch instead of isolating just the bad row — only reproduces against a real DbContext.
/// </summary>
public class EfMachineProfileRepositoryAddRangeFallbackTests
{
    private static MachineProfile MakeProfile(string name, string hash) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Manufacturer = "Acme",
        SlicerType = SlicerType.OrcaSlicer,
        Hash = hash,
        IsSystem = true,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    [Fact]
    public async Task AddRangeAsync_DuplicateHashInBatch_ThrowsAndDetachesAllEntities()
    {
        using SlicerDbContext db = TestHelpers.CreateSqliteInMemoryDb();
        EfMachineProfileRepository repo = new(db);

        MachineProfile good1 = MakeProfile("Good 1", "hash-a");
        MachineProfile good2 = MakeProfile("Good 2", "hash-b");
        MachineProfile conflicting = MakeProfile("Conflicting", "hash-a"); // duplicate hash -> unique index violation

        _ = await Assert.ThrowsAsync<DbUpdateException>(
            () => repo.AddRangeAsync(new[] { good1, good2, conflicting }));

        // The failed batch must not remain tracked, or a subsequent per-row retry would
        // resubmit the whole (still-poisoned) batch instead of isolating the bad row.
        Assert.Empty(db.ChangeTracker.Entries<MachineProfile>());
    }

    [Fact]
    public async Task AddRangeAsync_FollowedByPerRowFallback_IsolatesOnlyTheBadRow()
    {
        using SlicerDbContext db = TestHelpers.CreateSqliteInMemoryDb();
        EfMachineProfileRepository repo = new(db);

        MachineProfile good1 = MakeProfile("Good 1", "hash-a");
        MachineProfile good2 = MakeProfile("Good 2", "hash-b");
        MachineProfile conflicting = MakeProfile("Conflicting", "hash-a"); // duplicate hash -> unique index violation
        List<MachineProfile> staged = new() { good1, good2, conflicting };

        try
        {
            _ = await repo.AddRangeAsync(staged);
            Assert.Fail("Expected the batch commit to throw due to the duplicate hash.");
        }
        catch (DbUpdateException)
        {
            // Simulate the exact per-row fallback ProfilesService.StageAndCommitBatchAsync performs.
        }

        int succeeded = 0;
        int failed = 0;
        foreach (MachineProfile entity in staged)
        {
            try
            {
                await repo.AddAsync(entity);
                succeeded++;
            }
            catch (DbUpdateException)
            {
                failed++;
            }
        }

        // Only the genuinely conflicting row should fail; the two good rows must both succeed,
        // proving the fallback isolates one bad row instead of resubmitting the whole batch.
        Assert.Equal(2, succeeded);
        Assert.Equal(1, failed);

        List<MachineProfile> persisted = await db.MachineProfiles.AsNoTracking().ToListAsync();
        Assert.Equal(2, persisted.Count);
        Assert.Contains(persisted, p => p.Name == "Good 1");
        Assert.Contains(persisted, p => p.Name == "Good 2");
        Assert.DoesNotContain(persisted, p => p.Name == "Conflicting");
    }
}
