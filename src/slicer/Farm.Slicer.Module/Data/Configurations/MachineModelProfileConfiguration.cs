using Farm.Slicer.Module.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Slicer.Module.Data.Configurations;

/// <summary>
/// Entity configuration for <see cref="MachineModelProfile"/> — base machine model templates
/// from OrcaSlicer's machine_model_list.
/// </summary>
public class MachineModelProfileConfiguration : IEntityTypeConfiguration<MachineModelProfile>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<MachineModelProfile> builder)
    {
        _ = builder.HasKey(m => m.Id);

        // Properties
        _ = builder.Property(m => m.Name).IsRequired().HasMaxLength(256);
        _ = builder.Property(m => m.Manufacturer).IsRequired().HasMaxLength(128);
        _ = builder.Property(m => m.Description).HasMaxLength(1024);
        _ = builder.Property(m => m.SlicerType).HasConversion<int>();
        _ = builder.Property(m => m.RawJson).HasColumnType("TEXT");
        _ = builder.Property(m => m.Hash).HasMaxLength(64);
        _ = builder.Property(m => m.IsSystem).HasDefaultValue(false);
        _ = builder.Property(m => m.SlicerVersion).HasMaxLength(32);
        _ = builder.Property(m => m.SlicerDistribution).HasMaxLength(64);
        _ = builder.Property(m => m.SourceMachineModelName).HasMaxLength(256);
        _ = builder.Property(m => m.FamilyOverridesJson).HasColumnType("TEXT");
        _ = builder.Property(m => m.RenderedForOrcaVersion).HasMaxLength(32);
        _ = builder.Property(m => m.RenderStatus)
            .HasConversion<string>()
            .HasMaxLength(32)
            .HasDefaultValueSql("'NotApplicable'");
        _ = builder.Property(m => m.CreatedAt).IsRequired();
        _ = builder.Property(m => m.UpdatedAt).IsRequired();

        // Soft-reference indexes (no FK constraints — PrinterModel/User live in core)
        _ = builder.HasIndex(m => m.PrinterModelId);
        _ = builder.HasIndex(m => m.CreatedByUserId);

        // Indexes
        _ = builder.HasIndex(m => new { m.Name, m.SlicerType }).IsUnique();
        _ = builder.HasIndex(m => m.Manufacturer);
        _ = builder.HasIndex(m => m.Hash).IsUnique();
        _ = builder.HasIndex(m => m.IsSystem);
    }
}
