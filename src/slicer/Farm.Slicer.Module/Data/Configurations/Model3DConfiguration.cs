using Farm.Infrastructure.Domain;
using Farm.Slicer.Module.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Slicer.Module.Data.Configurations;

/// <summary>
/// Entity configuration for <see cref="Model3D"/> — 3D model file metadata (STL, OBJ, 3MF, etc.).
/// </summary>
/// <remarks>
/// Cross-domain references (User, FolderNode) are stored as soft <see cref="Guid"/> columns.
/// The <c>Tags</c> skip-navigation from the original <c>AppDbContext</c> is <b>not</b> replicated
/// here because <c>Model3DTag</c> lives in the core module.
/// </remarks>
public class Model3DConfiguration : IEntityTypeConfiguration<Model3D>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<Model3D> builder)
    {
        _ = builder.HasKey(m => m.Id);

        // StoredFileBase properties
        _ = builder.Property(m => m.FileName).IsRequired().HasMaxLength(255);
        _ = builder.Property(m => m.FilePath).IsRequired().HasMaxLength(512);
        _ = builder.Property(m => m.FileHash).IsRequired().HasMaxLength(64);
        _ = builder.Property(m => m.FileSizeBytes).IsRequired();
        _ = builder.Property(m => m.ThumbnailFileName).HasMaxLength(255);
        _ = builder.Property(m => m.HealthStatus).HasConversion<int>().HasDefaultValue(FileHealthStatus.Unknown);
        _ = builder.Property(m => m.LastVerificationResult).HasColumnType("TEXT");
        _ = builder.Property(m => m.RowVersion).IsRowVersion();

        // Model3D-specific properties
        _ = builder.Property(m => m.FileFormat).HasConversion<int>();
        _ = builder.Property(m => m.ValidationErrors).HasColumnType("TEXT");

        // Attribution fields (nullable — only set for imported models)
        _ = builder.Property(m => m.SourceUrl).HasMaxLength(2048);
        _ = builder.Property(m => m.SourceLicense).HasMaxLength(128);
        _ = builder.Property(m => m.SourceCreator).HasMaxLength(256);

        // Soft-reference indexes (no FK constraints — FolderNode, User live in core)
        _ = builder.HasIndex(m => m.FolderId);
        _ = builder.HasIndex(m => m.UploadedByUserId);

        // Indexes
        _ = builder.HasIndex(m => m.FileHash).IsUnique();
        _ = builder.HasIndex(m => m.UploadedAt);
        _ = builder.HasIndex(m => m.FileFormat);
        _ = builder.HasIndex(m => m.IsValid);
        _ = builder.HasIndex(m => m.HealthStatus);
        _ = builder.HasIndex(m => m.LastHealthCheckDate);
    }
}
