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

        // Soft-reference indexes (no FK constraints — these entities live in the core module)
        _ = builder.HasIndex(p => p.PrinterModelId);
        _ = builder.HasIndex(p => p.CreatedByUserId);

        // Indexes
        _ = builder.HasIndex(p => new { p.Name, p.SlicerType, p.PrinterModelId }).IsUnique();
        _ = builder.HasIndex(p => p.SlicerType);
        _ = builder.HasIndex(p => p.IsDefault);
        _ = builder.HasIndex(p => p.IsPublic);
        _ = builder.HasIndex(p => p.Hash).IsUnique();
        _ = builder.HasIndex(p => p.IsSystem);
    }
}
