using Farm.Slicer.Module.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Slicer.Module.Data.Configurations;

/// <summary>
/// Entity configuration for <see cref="Artifact"/> — slice-job output files (gcode, thumbnails, logs).
/// </summary>
public class ArtifactConfiguration : IEntityTypeConfiguration<Artifact>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<Artifact> builder)
    {
        _ = builder.HasKey(a => a.Id);

        _ = builder.Property(a => a.JobId).IsRequired();
        _ = builder.Property(a => a.Kind).IsRequired().HasMaxLength(64);
        _ = builder.Property(a => a.FileName).IsRequired().HasMaxLength(256);
        _ = builder.Property(a => a.RelativePath).IsRequired().HasMaxLength(1024);
        _ = builder.Property(a => a.ContentType).IsRequired().HasMaxLength(128);
        _ = builder.Property(a => a.SizeBytes).IsRequired();
        _ = builder.Property(a => a.Sha256).IsRequired().HasMaxLength(64);
        _ = builder.Property(a => a.CreatedAt).IsRequired();

        // Indexes
        _ = builder.HasIndex(a => a.JobId);
        _ = builder.HasIndex(a => a.WorkerId);
        _ = builder.HasIndex(a => a.CreatedAt);
        _ = builder.HasIndex(a => new { a.JobId, a.Kind });
    }
}
