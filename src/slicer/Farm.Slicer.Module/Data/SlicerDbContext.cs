using Farm.Infrastructure.Data;
using Farm.Slicer.Module.Data.Configurations;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Farm.Slicer.Module.Data;

/// <summary>
/// Database context for slicer-module entities.
/// Uses the <c>slicer</c> schema on PostgreSQL and SQL Server;
/// SQLite receives flat tables (no schema support).
/// </summary>
/// <remarks>
/// Entity configurations are discovered automatically via
/// <see cref="ModelBuilder.ApplyConfigurationsFromAssembly"/> from this assembly.
/// Cross-domain references (User, Printer, PrinterModel, FolderNode) are stored as
/// nullable <see cref="Guid"/> columns with no FK constraints.
/// </remarks>
public class SlicerDbContext(DbContextOptions<SlicerDbContext> options) : DbContext(options)
{
    /// <summary>Schema name applied on providers that support schemas (PostgreSQL, SQL Server).</summary>
    internal const string SchemaName = "slicer";

    /// <summary>Slicing job queue entries.</summary>
    public DbSet<SliceJob> SliceJobs => Set<SliceJob>();

    /// <summary>Process/print-settings profiles.</summary>
    public DbSet<ProcessProfile> ProcessProfiles => Set<ProcessProfile>();

    /// <summary>Machine/printer configuration profiles.</summary>
    public DbSet<MachineProfile> MachineProfiles => Set<MachineProfile>();

    /// <summary>Base machine model profiles (template definitions).</summary>
    public DbSet<MachineModelProfile> MachineModelProfiles => Set<MachineModelProfile>();

    /// <summary>Filament/material profiles.</summary>
    public DbSet<FilamentProfile> FilamentProfiles => Set<FilamentProfile>();

    /// <summary>3D model file metadata.</summary>
    public DbSet<Model3D> Models3D => Set<Model3D>();

    /// <summary>Slice-job output artifacts (gcode, thumbnails, logs).</summary>
    public DbSet<Artifact> Artifacts => Set<Artifact>();

    /// <summary>Global slicer settings (singleton row).</summary>
    public DbSet<SlicerSettings> SlicerSettings => Set<SlicerSettings>();

    /// <summary>Registered slicer services.</summary>
    public DbSet<SlicerService> SlicerServices => Set<SlicerService>();

    /// <summary>Worker nodes.</summary>
    public DbSet<Worker> Workers => Set<Worker>();

    /// <inheritdoc/>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Apply schema only on providers that support it (PostgreSQL, SQL Server).
        // SQLite silently ignores HasDefaultSchema — but we skip it explicitly
        // so the intent is clear and testable.
        if (!Database.IsSqlite())
        {
            _ = modelBuilder.HasDefaultSchema(SchemaName);
        }

        // Discover all IEntityTypeConfiguration<T> implementations in this assembly.
        _ = modelBuilder.ApplyConfigurationsFromAssembly(typeof(SlicerDbContext).Assembly);
        RevisionConcurrency.Configure(modelBuilder);

        ApplyProviderSpecificIdempotencyFilters(modelBuilder);
    }

    /// <inheritdoc/>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        NormalizeSliceJobEngines();
        RevisionConcurrency.Advance(ChangeTracker);
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <inheritdoc/>
    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        NormalizeSliceJobEngines();
        RevisionConcurrency.Advance(ChangeTracker);
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Keeps <see cref="SliceJob.NormalizedEngine"/> — the persisted column backing the
    /// (NormalizedEngine, Status) covering index used by queue-stat aggregation — in sync with
    /// <see cref="SliceJob.SlicerEngineName"/> on every insert/update.
    /// </summary>
    /// <remarks>
    /// Uses the same fallback rule as <see cref="SlicerEngineNames.ResolvePersistedName(string?)"/>:
    /// a missing or malformed engine name resolves to OrcaSlicer. Computing this here (rather than
    /// requiring every call site that constructs a <see cref="SliceJob"/> to set it) guarantees the
    /// column can never drift out of sync with <see cref="SliceJob.SlicerEngineName"/>.
    /// </remarks>
    private void NormalizeSliceJobEngines()
    {
        foreach (EntityEntry<SliceJob> entry in ChangeTracker.Entries<SliceJob>().Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            entry.Entity.NormalizedEngine =
                (int)SlicerEngineNames.ResolvePersistedName(entry.Entity.SlicerEngineName);
        }
    }

    /// <summary>
    /// Slice-job idempotency is calibration/project scoped. Standard jobs use
    /// <see cref="Guid.Empty"/> and must remain repeatable even when their content checksum
    /// matches an earlier standard slice.
    /// </summary>
    private void ApplyProviderSpecificIdempotencyFilters(ModelBuilder modelBuilder)
    {
        IMutableEntityType? sliceJob = modelBuilder.Model.FindEntityType(typeof(SliceJob));
        if (sliceJob is null)
        {
            return;
        }

        foreach (IMutableIndex index in sliceJob.GetIndexes())
        {
            string scopeColumn = Database.IsSqlServer()
                ? $"[{nameof(SliceJob.IdempotencyScopeId)}]"
                : $"\"{nameof(SliceJob.IdempotencyScopeId)}\"";
            string correlationColumn = Database.IsSqlServer()
                ? $"[{nameof(SliceJob.CorrelationId)}]"
                : $"\"{nameof(SliceJob.CorrelationId)}\"";
            string checksumColumn = Database.IsSqlServer()
                ? $"[{nameof(SliceJob.Checksum)}]"
                : $"\"{nameof(SliceJob.Checksum)}\"";
            string nonEmptyScope =
                $"{scopeColumn} <> '00000000-0000-0000-0000-000000000000'";
            switch (index.GetDatabaseName())
            {
                case SliceJobConfiguration.CorrelationUniqueIndexName:
                    index.SetFilter($"{correlationColumn} IS NOT NULL AND {nonEmptyScope}");
                    break;
                case SliceJobConfiguration.ChecksumUniqueIndexName:
                    index.SetFilter($"{checksumColumn} IS NOT NULL AND {nonEmptyScope}");
                    break;
                default:
                    break;
            }
        }
    }
}
