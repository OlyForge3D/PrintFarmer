using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Farm.Slicer.Module.Tests.Data;

/// <summary>
/// Pins the change-tracker hygiene the profile seed depends on (#1779).
/// </summary>
/// <remarks>
/// A failed <c>SaveChangesAsync</c> does NOT roll back EF's change tracker: the rejected entity
/// stays tracked as <c>Added</c>, so every later save on the same context resubmits it and fails
/// again. <c>AddRangeAsync</c> already detached on failure, but <c>AddAsync</c> did not — which is
/// what made a rejected duplicate take the rows behind it down too. Measured against real SQLite,
/// that turned a backfill of the eight missing high-flow machine profiles into zero imported rows.
/// These tests use a real database so the UNIQUE indexes are actually enforced.
/// </remarks>
public class ProfileRepositoryChangeTrackerTests
{
    [Fact]
    public async Task MachineProfile_AddAsyncRejectedByUniqueIndex_DoesNotBlockTheNextAdd()
    {
        using SlicerDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        EfMachineProfileRepository repo = new(db);

        await repo.AddAsync(Machine("Prusa CORE One 0.4 nozzle", "hash-1"), CancellationToken.None);

        // Same name, different hash — rejected by the UNIQUE (Name, SlicerType) index.
        _ = await Assert.ThrowsAsync<DbUpdateException>(() =>
            repo.AddAsync(Machine("Prusa CORE One 0.4 nozzle", "hash-2"), CancellationToken.None));

        // The rejected entity must not still be tracked, or this valid insert fails too — which is
        // precisely how the HF profiles were lost behind an unrelated duplicate.
        await repo.AddAsync(Machine("Prusa CORE One HF 0.4 nozzle", "hash-3"), CancellationToken.None);

        var persisted = await repo.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, CancellationToken.None);
        Assert.Equal(2, persisted.Count);
        Assert.Contains(persisted, p => p.Name == "Prusa CORE One HF 0.4 nozzle");
    }

    [Fact]
    public async Task FilamentProfile_AddAsyncRejectedByUniqueIndex_DoesNotBlockTheNextAdd()
    {
        using SlicerDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        EfFilamentProfileRepository repo = new(db);

        await repo.AddAsync(Filament("Prusa Generic PLA", "PLA", "f-hash-1"), CancellationToken.None);

        _ = await Assert.ThrowsAsync<DbUpdateException>(() =>
            repo.AddAsync(Filament("Prusa Generic PLA", "PLA", "f-hash-2"), CancellationToken.None));

        await repo.AddAsync(Filament("Prusa Generic PETG", "PETG", "f-hash-3"), CancellationToken.None);

        var persisted = await repo.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, CancellationToken.None);
        Assert.Equal(2, persisted.Count);
    }

    /// <remarks>
    /// Note the process index includes <c>PrinterModelId</c>, and SQL treats NULLs as distinct, so
    /// two model-less rows sharing a name do NOT collide. This test therefore binds both rows to the
    /// same model to exercise a genuine rejection; the model-less duplicate case is what the seed's
    /// own identity guard has to catch, since the database will not.
    /// </remarks>
    [Fact]
    public async Task ProcessProfile_AddAsyncRejectedByUniqueIndex_DoesNotBlockTheNextAdd()
    {
        using SlicerDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        EfProcessProfileRepository repo = new(db);
        Guid modelId = Guid.NewGuid();

        await repo.AddAsync(Process("0.20mm Standard", "p-hash-1", modelId), CancellationToken.None);

        _ = await Assert.ThrowsAsync<DbUpdateException>(() =>
            repo.AddAsync(Process("0.20mm Standard", "p-hash-2", modelId), CancellationToken.None));

        await repo.AddAsync(Process("0.30mm Draft", "p-hash-3", modelId), CancellationToken.None);

        var persisted = await repo.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, CancellationToken.None);
        Assert.Equal(2, persisted.Count);
    }

    /// <summary>
    /// Documents why the seed cannot rely on the database alone for process profiles: because the
    /// unique index includes <c>PrinterModelId</c> and SQL treats NULLs as distinct, two model-less
    /// rows with the same name are accepted. The seed's identity guard is load-bearing here.
    /// </summary>
    [Fact]
    public async Task ProcessProfile_TwoModelLessRowsWithSameName_AreAcceptedByTheDatabase()
    {
        using SlicerDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        EfProcessProfileRepository repo = new(db);

        await repo.AddAsync(Process("0.20mm Standard", "n-hash-1", null), CancellationToken.None);
        await repo.AddAsync(Process("0.20mm Standard", "n-hash-2", null), CancellationToken.None);

        var persisted = await repo.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, CancellationToken.None);
        Assert.Equal(2, persisted.Count);
    }

    private static MachineProfile Machine(string name, string hash) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Manufacturer = "Prusa",
        SlicerType = SlicerType.OrcaSlicer,
        IsSystem = true,
        Hash = hash,
        RawJson = "{}"
    };

    private static FilamentProfile Filament(string name, string material, string hash) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Material = material,
        Manufacturer = "Prusa",
        SlicerType = SlicerType.OrcaSlicer,
        IsSystem = true,
        Hash = hash,
        RawJson = "{}"
    };

    private static ProcessProfile Process(string name, string hash, Guid? printerModelId) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        SlicerType = SlicerType.OrcaSlicer,
        PrinterModelId = printerModelId,
        IsSystem = true,
        Hash = hash,
        RawJson = "{}"
    };
}
