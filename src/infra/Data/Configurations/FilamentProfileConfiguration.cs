using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity configuration for FilamentProfile - slicer filament/material profiles.
/// </summary>
public class FilamentProfileConfiguration : IEntityTypeConfiguration<FilamentProfile>
{
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

        // Foreign Keys
        _ = builder.HasOne(p => p.CreatedByUser)
            .WithMany()
            .HasForeignKey(p => p.CreatedByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        // Indexes - Name included in unique constraint to allow multiple profiles with same material
        // (e.g., "Generic PLA" vs "Bambu PLA" both with Material="PLA")
        _ = builder.HasIndex(p => new { p.Name, p.Material, p.SlicerType }).IsUnique();
        _ = builder.HasIndex(p => p.SlicerType);
        _ = builder.HasIndex(p => p.Material);
        _ = builder.HasIndex(p => p.Hash).IsUnique();
        _ = builder.HasIndex(p => p.IsSystem);
        _ = builder.HasIndex(p => p.CreatedByUserId);
    }
}
