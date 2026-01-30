using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity configuration for Model3D - 3D model files (STL, OBJ, 3MF, etc.)
/// </summary>
public class Model3DConfiguration : IEntityTypeConfiguration<Model3D>
{
    public void Configure(EntityTypeBuilder<Model3D> builder)
    {
        _ = builder.HasKey(m => m.Id);

        // Properties
        _ = builder.Property(m => m.FileName).IsRequired().HasMaxLength(255);
        _ = builder.Property(m => m.FilePath).IsRequired().HasMaxLength(512);
        _ = builder.Property(m => m.FileHash).IsRequired().HasMaxLength(64);
        _ = builder.Property(m => m.FileFormat).HasConversion<int>();
        _ = builder.Property(m => m.FileSizeBytes).IsRequired();
        _ = builder.Property(m => m.ThumbnailFileName).HasMaxLength(255); // Path to thumbnail image
        _ = builder.Property(m => m.ValidationErrors).HasColumnType("TEXT");
        _ = builder.Property(m => m.HealthStatus).HasConversion<int>().HasDefaultValue(FileHealthStatus.Unknown);
        _ = builder.Property(m => m.LastVerificationResult).HasColumnType("TEXT");

        // Foreign Keys
        _ = builder.HasOne(m => m.Folder)
            .WithMany(f => f.Models)
            .HasForeignKey(m => m.FolderId)
            .OnDelete(DeleteBehavior.SetNull);

        _ = builder.HasOne(m => m.UploadedByUser)
            .WithMany()
            .HasForeignKey(m => m.UploadedByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        // Navigation: Model3D -> Tags (skip-navigation collection)
        // Skip-navigation: Model3D.Tags - join table managed by EF Core
        _ = builder.HasMany(m => m.Tags)
            .WithMany();

        // Indexes
        _ = builder.HasIndex(m => m.FileHash).IsUnique();
        _ = builder.HasIndex(m => m.UploadedAt);
        _ = builder.HasIndex(m => m.FolderId); // Index for virtual directory queries
        _ = builder.HasIndex(m => m.FileFormat);
        _ = builder.HasIndex(m => m.IsValid);
        _ = builder.HasIndex(m => m.UploadedByUserId);
        _ = builder.HasIndex(m => m.HealthStatus); // Index for dashboard queries
        _ = builder.HasIndex(m => m.LastHealthCheckDate); // Index for recent health checks
    }
}
