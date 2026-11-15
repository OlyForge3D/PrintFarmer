using System.Collections.Concurrent;
using System.Data.Common;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Farm.Slicer.Module.Tests.Data.Repositories;

public sealed class ProfileOwnedNonSystemReadTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly QueryDiagnostics _diagnostics = new();
    private readonly SlicerDbContext _db;
    private readonly Dictionary<string, Guid> _ids = new(StringComparer.Ordinal);
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly Guid _otherUserId = Guid.NewGuid();

    public ProfileOwnedNonSystemReadTests()
    {
        _connection.Open();
        DbContextOptions<SlicerDbContext> options = new DbContextOptionsBuilder<SlicerDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(_diagnostics)
            .Options;

        _db = new SlicerDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task OwnedNonSystemReads_FilterInSql_WithoutNarrowingSharedVisibilityReads()
    {
        await SeedAsync();

        EfProcessProfileRepository processRepo = new(_db);
        EfFilamentProfileRepository filamentRepo = new(_db);
        EfMachineProfileRepository machineRepo = new(_db);

        IReadOnlyList<ProcessProfile> ownedProcesses =
            await processRepo.GetOwnedNonSystemAsync(_ownerId, CancellationToken.None);
        Assert.Equal(new[] { "Owned Process A", "Owned Process B" }, ownedProcesses.Select(profile => profile.Name));
        Assert.False(ownedProcesses[0].IsPublic);
        Assert.True(ownedProcesses[1].IsPublic);
        Assert.Equal(SlicerType.PrusaSlicer, ownedProcesses[1].SlicerType);
        Assert.DoesNotContain(ownedProcesses, profile => profile.Name == "Owned System Process");
        AssertQueryMaterializedOnly<ProcessProfile>(2);
        AssertOwnedNonSystemSql();

        _diagnostics.Reset();
        IReadOnlyList<FilamentProfile> ownedFilaments =
            await filamentRepo.GetOwnedNonSystemAsync(SlicerType.OrcaSlicer, _ownerId, CancellationToken.None);
        Assert.Equal(new[] { "Owned Filament A", "Owned Filament B" }, ownedFilaments.Select(profile => profile.Name));
        Assert.True(ownedFilaments[1].IsPublic);
        Assert.DoesNotContain(ownedFilaments, profile => profile.Name == "Owned System Filament");
        Assert.DoesNotContain(ownedFilaments, profile => profile.Name == "Owned Wrong-Engine Filament");
        AssertQueryMaterializedOnly<FilamentProfile>(2);
        AssertOwnedNonSystemSql();

        _diagnostics.Reset();
        IReadOnlyList<MachineProfile> ownedMachines =
            await machineRepo.GetOwnedNonSystemAsync(SlicerType.OrcaSlicer, _ownerId, CancellationToken.None);
        Assert.Equal(new[] { "Owned Machine A", "Owned Machine B" }, ownedMachines.Select(profile => profile.Name));
        Assert.True(ownedMachines[1].IsPublic);
        Assert.DoesNotContain(ownedMachines, profile => profile.Name == "Owned System Machine");
        Assert.DoesNotContain(ownedMachines, profile => profile.Name == "Owned Wrong-Engine Machine");
        AssertQueryMaterializedOnly<MachineProfile>(2);
        AssertOwnedNonSystemSql();

        _diagnostics.Reset();
        IReadOnlyList<ProcessProfile> visibleProcesses =
            await processRepo.GetByUserAsync(_ownerId, CancellationToken.None);
        Assert.Contains(visibleProcesses, profile => profile.Id == Id("Other Public Process"));
        Assert.Equal(4, _diagnostics.Materialized<ProcessProfile>());

        _diagnostics.Reset();
        IReadOnlyList<FilamentProfile> visibleFilaments =
            await filamentRepo.GetByEngineAsync(SlicerType.OrcaSlicer, includeSystem: false, userId: _ownerId, ct: CancellationToken.None);
        Assert.Contains(visibleFilaments, profile => profile.Id == Id("Other Public Filament"));
        Assert.Equal(3, _diagnostics.Materialized<FilamentProfile>());

        _diagnostics.Reset();
        IReadOnlyList<MachineProfile> visibleMachines =
            await machineRepo.GetByEngineAsync(SlicerType.OrcaSlicer, includeSystem: false, userId: _ownerId, ct: CancellationToken.None);
        Assert.Contains(visibleMachines, profile => profile.Id == Id("Other Public Machine"));
        Assert.Equal(3, _diagnostics.Materialized<MachineProfile>());
    }

    private async Task SeedAsync()
    {
        _db.ProcessProfiles.AddRange(
            Process("Owned Process A", _ownerId, isPublic: false),
            Process("Owned Process B", _ownerId, isPublic: true, slicerType: SlicerType.PrusaSlicer),
            Process("Owned System Process", _ownerId, isSystem: true),
            Process("Other Public Process", _otherUserId, isPublic: true),
            Process("Other Private Process", _otherUserId, isPublic: false));
        _db.FilamentProfiles.AddRange(
            Filament("Owned Filament A", _ownerId, "A", isPublic: false),
            Filament("Owned Filament B", _ownerId, "B", isPublic: true),
            Filament("Owned Wrong-Engine Filament", _ownerId, "0", isPublic: false, slicerType: SlicerType.PrusaSlicer),
            Filament("Owned System Filament", _ownerId, "C", isSystem: true),
            Filament("Other Public Filament", _otherUserId, "D", isPublic: true),
            Filament("Other Private Filament", _otherUserId, "E", isPublic: false));
        _db.MachineProfiles.AddRange(
            Machine("Owned Machine A", _ownerId, "A", isPublic: false),
            Machine("Owned Machine B", _ownerId, "B", isPublic: true),
            Machine("Owned Wrong-Engine Machine", _ownerId, "0", isPublic: false, slicerType: SlicerType.PrusaSlicer),
            Machine("Owned System Machine", _ownerId, "C", isSystem: true),
            Machine("Other Public Machine", _otherUserId, "D", isPublic: true),
            Machine("Other Private Machine", _otherUserId, "E", isPublic: false));

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        _diagnostics.Reset();
    }

    private void AssertOwnedNonSystemSql()
    {
        string sql = Assert.Single(_diagnostics.Commands);
        int whereStart = sql.IndexOf("WHERE", StringComparison.OrdinalIgnoreCase);
        int orderByStart = sql.IndexOf("ORDER BY", whereStart, StringComparison.OrdinalIgnoreCase);
        Assert.True(whereStart >= 0);
        Assert.True(orderByStart > whereStart);

        string predicate = sql[whereStart..orderByStart];
        Assert.Contains("CreatedByUserId", predicate, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IsSystem", predicate, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IsPublic", predicate, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" OR ", predicate, StringComparison.OrdinalIgnoreCase);
    }

    private void AssertQueryMaterializedOnly<TProfile>(int expectedCount)
    {
        Assert.Equal(expectedCount, _diagnostics.Materialized<TProfile>());
        Assert.Single(_diagnostics.Commands);
    }

    private Guid Id(string name)
    {
        if (!_ids.TryGetValue(name, out Guid id))
        {
            id = Guid.NewGuid();
            _ids.Add(name, id);
        }

        return id;
    }

    private ProcessProfile Process(
        string name,
        Guid ownerId,
        bool isSystem = false,
        bool isPublic = true,
        SlicerType slicerType = SlicerType.OrcaSlicer) =>
        new()
        {
            Id = Id(name),
            Name = name,
            CreatedByUserId = ownerId,
            IsSystem = isSystem,
            IsPublic = isPublic,
            SlicerType = slicerType,
            RawJson = $"{{\"name\":\"{name}\"}}",
            SettingsJson = $"{{\"payload\":\"{name}\"}}"
        };

    private FilamentProfile Filament(
        string name,
        Guid ownerId,
        string material,
        bool isSystem = false,
        bool isPublic = true,
        SlicerType slicerType = SlicerType.OrcaSlicer) =>
        new()
        {
            Id = Id(name),
            Name = name,
            Material = material,
            CreatedByUserId = ownerId,
            IsSystem = isSystem,
            IsPublic = isPublic,
            SlicerType = slicerType,
            RawJson = $"{{\"name\":\"{name}\"}}",
            SettingsJson = $"{{\"payload\":\"{name}\"}}"
        };

    private MachineProfile Machine(
        string name,
        Guid ownerId,
        string manufacturer,
        bool isSystem = false,
        bool isPublic = true,
        SlicerType slicerType = SlicerType.OrcaSlicer) =>
        new()
        {
            Id = Id(name),
            Name = name,
            Manufacturer = manufacturer,
            CreatedByUserId = ownerId,
            IsSystem = isSystem,
            IsPublic = isPublic,
            SlicerType = slicerType,
            RawJson = $"{{\"name\":\"{name}\"}}",
            SettingsJson = $"{{\"payload\":\"{name}\"}}"
        };

    private sealed class QueryDiagnostics : DbCommandInterceptor, IMaterializationInterceptor
    {
        private readonly ConcurrentQueue<string> _commands = new();
        private readonly ConcurrentDictionary<Type, int> _materialized = new();

        public IReadOnlyList<string> Commands => _commands.ToArray();

        public int Materialized<TProfile>() => _materialized.GetValueOrDefault(typeof(TProfile));

        public void Reset()
        {
            while (_commands.TryDequeue(out _))
            {
            }

            _materialized.Clear();
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            _commands.Enqueue(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            _commands.Enqueue(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
        {
            _materialized.AddOrUpdate(entity.GetType(), 1, (_, count) => count + 1);
            return entity;
        }
    }
}
