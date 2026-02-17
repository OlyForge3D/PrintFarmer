using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity configuration for FolderNode - virtual folder hierarchy for organizing files and models.
/// </summary>
public class FolderNodeConfiguration : IEntityTypeConfiguration<FolderNode>
{
    public void Configure(EntityTypeBuilder<FolderNode> builder)
    {
        _ = builder.HasKey(f => f.Id);

        // Properties
        _ = builder.Property(f => f.Path).IsRequired().HasMaxLength(1024);
        _ = builder.Property(f => f.FolderType).IsRequired().HasMaxLength(50);
        _ = builder.Property(f => f.CreatedAt).IsRequired();

        // Navigation: FolderNode -> Models removed (Model3D migrated to Farm.Slicer.Module)
        // The FK relationship is maintained via FolderId soft reference on Model3D.

        // Navigation: FolderNode -> Files (inverse of GcodeFile.Folder)
        _ = builder.HasMany(f => f.Files)
            .WithOne(g => g.Folder)
            .HasForeignKey(g => g.FolderId)
            .OnDelete(DeleteBehavior.SetNull);

        // Indexes
        _ = builder.HasIndex(f => new { f.Path, f.FolderType }).IsUnique();
    }
}
