using Farm.Slicer.Module.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Slicer.Module.Data.Configurations;

/// <summary>
/// Entity configuration for <see cref="ProcessProfile"/> — slicer process/print-settings profiles.
/// </summary>
/// <remarks>
/// Cross-domain references (PrinterModel, Printer, User) are stored as nullable <see cref="Guid"/>
/// columns with indexes but no FK constraints — the slicer module does not own those entities.
/// </remarks>
public class ProcessProfileConfiguration : IEntityTypeConfiguration<ProcessProfile>
{
    /// <summary>
    /// Name of the unique <c>(Name, SlicerType, PrinterModelId)</c> index over unowned
    /// (system/stock) rows. Its filter is provider-specific SQL, so it is applied in
    /// <see cref="SlicerDbContext"/> rather than here.
    /// </summary>
    public const string UnownedNameUniqueIndexName = "IX_ProcessProfiles_Name_SlicerType_PrinterModelId_Unowned";

    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<ProcessProfile> builder)
    {
        _ = builder.HasKey(p => p.Id);

        // Properties
        _ = builder.Property(p => p.Name).IsRequired().HasMaxLength(255);
        _ = builder.Property(p => p.Description).HasMaxLength(1000);
        _ = builder.Property(p => p.SlicerType).HasConversion<int>();
        _ = builder.Property(p => p.Quality).HasConversion<int>();
        _ = builder.Property(p => p.AdvancedSettings).HasColumnType("TEXT");
        _ = builder.Property(p => p.RawJson).HasColumnType("TEXT");
        _ = builder.Property(p => p.SettingsJson).HasColumnType("TEXT");
        _ = builder.Property(p => p.Hash).HasMaxLength(64);
        _ = builder.Property(p => p.IsSystem).HasDefaultValue(false);
        _ = builder.Property(p => p.Material).IsRequired().HasMaxLength(64);

        // Soft-reference indexes (no FK constraints — these entities live in the core module)
        _ = builder.HasIndex(p => p.PrinterModelId);
        _ = builder.HasIndex(p => p.CreatedByUserId);

        // Indexes
        // Name uniqueness is scoped per owner (#3198, mirroring #3192 for filaments); see
        // MachineProfileConfiguration. PrinterModelId is nullable, and a NULL model never
        // participates in uniqueness on any provider: PostgreSQL and SQLite treat NULLs as distinct,
        // and SQL Server gets EF's auto "IS NOT NULL" filter for the per-owner index and an explicit
        // one for the unowned index. That preserves the pre-#3198 semantics of the global index.
        _ = builder.HasIndex(p => new { p.CreatedByUserId, p.Name, p.SlicerType, p.PrinterModelId }).IsUnique();
        _ = builder.HasIndex(p => new { p.Name, p.SlicerType, p.PrinterModelId })
            .IsUnique()
            .HasDatabaseName(UnownedNameUniqueIndexName);
        _ = builder.HasIndex(p => p.SlicerType);
        _ = builder.HasIndex(p => p.IsDefault);
        _ = builder.HasIndex(p => p.IsPublic);
        _ = builder.HasIndex(p => p.Hash).IsUnique();
        _ = builder.HasIndex(p => p.IsSystem);
    }
}
