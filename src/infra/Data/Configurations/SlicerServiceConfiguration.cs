using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity configuration for SlicerService - slicer worker service registry entries.
/// </summary>
public class SlicerServiceConfiguration : IEntityTypeConfiguration<SlicerService>
{
    public void Configure(EntityTypeBuilder<SlicerService> builder)
    {
        _ = builder.HasKey(s => s.Id);

        // Properties
        _ = builder.Property(s => s.Name).IsRequired().HasMaxLength(200);
        _ = builder.Property(s => s.Version).HasMaxLength(64);
        _ = builder.Property(s => s.Host).HasMaxLength(512);
        _ = builder.Property(s => s.UiManifestUrl).HasMaxLength(512);
        _ = builder.Property(s => s.CapabilitiesJson).HasColumnType("TEXT");
        _ = builder.Property(s => s.Status).HasMaxLength(64);
        _ = builder.Property(s => s.ApiKey).HasMaxLength(128);

        // Indexes
        _ = builder.HasIndex(s => s.Name);
        _ = builder.HasIndex(s => s.SlicerType);
        _ = builder.HasIndex(s => s.Status);
    }
}
