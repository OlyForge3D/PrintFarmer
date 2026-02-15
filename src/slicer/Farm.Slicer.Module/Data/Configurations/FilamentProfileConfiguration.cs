using Farm.Slicer.Module.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Slicer.Module.Data.Configurations;

/// <summary>
/// Entity configuration for <see cref="FilamentProfile"/> — slicer filament/material profiles.
/// </summary>
/// <remarks>
/// The <c>CreatedByUserId</c> column is a soft reference to the core User entity
/// with no FK constraint.
/// </remarks>
public class FilamentProfileConfiguration : IEntityTypeConfiguration<FilamentProfile>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<FilamentProfile> builder)
    {
        _ = builder.HasKey(p => p.Id);

        // Properties
        _ = builder.Property(p => p.Name).IsRequired().HasMaxLength(255);
        _ = builder.Property(p => p.Material).IsRequired().HasMaxLength(64);
        _ = builder.Property(p => p.Manufacturer).HasMaxLength(255);
        _ = builder.Property(p => p.Description).HasMaxLength(1000);
        _ = builder.Property(p => p.SlicerType).HasConversion<int>();
        _ = builder.Property(p => p.RawJson).HasColumnType("TEXT");
        _ = builder.Property(p => p.SettingsJson).HasColumnType("TEXT");
        _ = builder.Property(p => p.Hash).HasMaxLength(64);
        _ = builder.Property(p => p.IsSystem).HasDefaultValue(false);

        // Soft-reference index (no FK constraint)
        _ = builder.HasIndex(p => p.CreatedByUserId);

        // Indexes — Name included in unique constraint to allow multiple profiles
        // with same material (e.g., "Generic PLA" vs "Bambu PLA" both Material="PLA")
        _ = builder.HasIndex(p => new { p.Name, p.Material, p.SlicerType }).IsUnique();
        _ = builder.HasIndex(p => p.SlicerType);
        _ = builder.HasIndex(p => p.Material);
        _ = builder.HasIndex(p => p.Hash).IsUnique();
        _ = builder.HasIndex(p => p.IsSystem);
    }
}
