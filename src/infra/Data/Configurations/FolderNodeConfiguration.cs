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

        // Navigation: FolderNode -> Models (inverse of Model3D.Folder)
        _ = builder.HasMany(f => f.Models)
            .WithOne(m => m.Folder)
            .HasForeignKey(m => m.FolderId)
            .OnDelete(DeleteBehavior.SetNull);

        // Navigation: FolderNode -> Files (inverse of GcodeFile.Folder)
        _ = builder.HasMany(f => f.Files)
            .WithOne(g => g.Folder)
            .HasForeignKey(g => g.FolderId)
            .OnDelete(DeleteBehavior.SetNull);

        // Indexes
        _ = builder.HasIndex(f => new { f.Path, f.FolderType }).IsUnique();
    }
}
