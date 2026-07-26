using Farm.Slicer.Module.Data.Configurations;
using Farm.Slicer.Module.Domain;
using Microsoft.EntityFrameworkCore;
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

        ApplyProviderSpecificIdempotencyFilters(modelBuilder);
    }

    /// <summary>
    /// SQL Server treats <see langword="null"/> as a duplicate inside a unique index, so the
    /// owner/project-scoped slice-job idempotency indexes must exclude rows without a
    /// correlation/checksum. PostgreSQL and SQLite already treat nulls as distinct, which keeps
    /// every pre-existing non-calibration job valid without a filter.
    /// </summary>
    private void ApplyProviderSpecificIdempotencyFilters(ModelBuilder modelBuilder)
    {
        if (!Database.IsSqlServer())
        {
            return;
        }

        IMutableEntityType? sliceJob = modelBuilder.Model.FindEntityType(typeof(SliceJob));
        if (sliceJob is null)
        {
            return;
        }

        foreach (IMutableIndex index in sliceJob.GetIndexes())
        {
            switch (index.GetDatabaseName())
            {
                case SliceJobConfiguration.CorrelationUniqueIndexName:
                    index.SetFilter($"[{nameof(SliceJob.CorrelationId)}] IS NOT NULL");
                    break;
                case SliceJobConfiguration.ChecksumUniqueIndexName:
                    index.SetFilter($"[{nameof(SliceJob.Checksum)}] IS NOT NULL");
                    break;
                default:
                    break;
            }
        }
    }
}
