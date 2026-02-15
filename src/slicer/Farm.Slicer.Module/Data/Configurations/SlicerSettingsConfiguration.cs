using Farm.Slicer.Module.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Slicer.Module.Data.Configurations;

/// <summary>
/// Entity configuration for <see cref="SlicerSettings"/> — global slicer settings (singleton row).
/// </summary>
public class SlicerSettingsConfiguration : IEntityTypeConfiguration<SlicerSettings>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<SlicerSettings> builder)
    {
        _ = builder.HasKey(s => s.Id);

        _ = builder.Property(s => s.Enabled).IsRequired();
        _ = builder.Property(s => s.PerEngineJson).HasColumnType("TEXT");
        _ = builder.Property(s => s.UpdatedAt).IsRequired();
        _ = builder.Property(s => s.JitterPercent).HasDefaultValue(15.0).IsRequired();
    }
}
