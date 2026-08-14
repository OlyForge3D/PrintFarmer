using Farm.Slicer.Module.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Slicer.Module.Data.Configurations;

/// <summary>
/// Entity configuration for <see cref="SlicerService"/> — registered slicer worker services.
/// </summary>
public class SlicerServiceConfiguration : IEntityTypeConfiguration<SlicerService>
{
    /// <inheritdoc/>
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
        _ = builder.Property(s => s.InstanceId).HasMaxLength(200);

        // Indexes
        _ = builder.HasIndex(s => s.Name);
        _ = builder.HasIndex(s => s.SlicerType);
        _ = builder.HasIndex(s => s.Status);

        // Enforces the upsert-by-InstanceId contract at the database level (issue
        // #1528): two concurrent registrations for the same stable worker instance
        // cannot both insert a new row. Multiple NULL values are permitted by a
        // standard unique index (SQLite/PostgreSQL/SQL Server all treat NULL as
        // distinct from every other NULL for uniqueness purposes), so scaled
        // workers that never send an InstanceId are unaffected.
        _ = builder.HasIndex(s => s.InstanceId).IsUnique();
    }
}
