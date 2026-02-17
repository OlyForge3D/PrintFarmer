using Farm.Slicer.Module.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Slicer.Module.Data.Configurations;

/// <summary>
/// Entity configuration for <see cref="MachineProfile"/> — slicer machine/printer configuration profiles.
/// </summary>
/// <remarks>
/// Cross-domain references (PrinterModel, User) are stored as nullable <see cref="Guid"/>
/// columns with indexes but no FK constraints.
/// The <see cref="MachineProfile.MachineModelProfile"/> navigation is a slicer-internal relationship.
/// </remarks>
public class MachineProfileConfiguration : IEntityTypeConfiguration<MachineProfile>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<MachineProfile> builder)
    {
        _ = builder.HasKey(p => p.Id);

        // Properties
        _ = builder.Property(p => p.Name).IsRequired().HasMaxLength(255);
        _ = builder.Property(p => p.Manufacturer).IsRequired().HasMaxLength(255);
        _ = builder.Property(p => p.Description).HasMaxLength(1000);
        _ = builder.Property(p => p.SlicerType).HasConversion<int>();
        _ = builder.Property(p => p.RawJson).HasColumnType("TEXT");
        _ = builder.Property(p => p.SettingsJson).HasColumnType("TEXT");
        _ = builder.Property(p => p.Hash).HasMaxLength(64);
        _ = builder.Property(p => p.IsSystem).HasDefaultValue(false);

        // Slicer-internal FK: MachineProfile → MachineModelProfile
        _ = builder.HasOne(p => p.MachineModelProfile)
            .WithMany(m => m.MachineProfiles)
            .HasForeignKey(p => p.MachineModelProfileId)
            .OnDelete(DeleteBehavior.SetNull);

        // Soft-reference indexes (no FK constraints)
        _ = builder.HasIndex(p => p.PrinterModelId);
        _ = builder.HasIndex(p => p.CreatedByUserId);

        // Indexes
        _ = builder.HasIndex(p => new { p.Name, p.SlicerType }).IsUnique();
        _ = builder.HasIndex(p => p.SlicerType);
        _ = builder.HasIndex(p => p.Manufacturer);
        _ = builder.HasIndex(p => p.Hash).IsUnique();
        _ = builder.HasIndex(p => p.IsSystem);
    }
}
