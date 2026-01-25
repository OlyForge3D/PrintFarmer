using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity configuration for HarvestFileGcodeFileMapping (links discovered files to library files).
/// </summary>
public class HarvestFileGcodeFileMappingConfiguration : IEntityTypeConfiguration<HarvestFileGcodeFileMapping>
{
    public void Configure(EntityTypeBuilder<HarvestFileGcodeFileMapping> builder)
    {
        _ = builder.HasKey(m => m.Id);
        _ = builder.Property(m => m.CreatedAt).IsRequired();

        // Foreign key to HarvestDiscoveredFile
        // Use Restrict (not Cascade) to prevent accidental deletion of mappings when cleaning up harvest operations
        // This protects GcodeFile records from being orphaned if someone deletes the harvest operation
        _ = builder.HasOne<HarvestDiscoveredFile>()
            .WithMany(h => h.GcodeFileMappings)
            .HasForeignKey(m => m.HarvestDiscoveredFileId)
            .OnDelete(DeleteBehavior.Restrict);

        // Foreign key to GcodeFile
        // Use NoAction to absolutely prevent cascade deletion of library files from harvest operations
        _ = builder.HasOne<GcodeFile>()
            .WithMany(g => g.HarvestFileMappings)
            .HasForeignKey(m => m.GcodeFileId)
            .OnDelete(DeleteBehavior.NoAction);

        // Indexes for common queries
        _ = builder.HasIndex(m => m.HarvestDiscoveredFileId);
        _ = builder.HasIndex(m => m.GcodeFileId);
        _ = builder.HasIndex(m => m.CreatedAt);
    }
}
