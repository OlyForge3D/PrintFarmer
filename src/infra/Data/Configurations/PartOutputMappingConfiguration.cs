using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>Entity configuration for job-output → SKU mappings used by harvest.</summary>
public class PartOutputMappingConfiguration : IEntityTypeConfiguration<PartOutputMapping>
{
    public void Configure(EntityTypeBuilder<PartOutputMapping> builder)
    {
        _ = builder.HasKey(m => m.Id);
        _ = builder.Property(m => m.Quantity).IsRequired();
        _ = builder.Property(m => m.CreatedAt).IsRequired();
        _ = builder.Property(m => m.UpdatedAt).IsRequired();

        _ = builder.HasIndex(m => m.PartInventoryId);
        _ = builder.HasIndex(m => m.GcodeFileId);
        _ = builder.HasIndex(m => m.PrintProjectFileId);

        // Prevent duplicate mappings for the same output → SKU pair.
        _ = builder.HasIndex(m => new { m.GcodeFileId, m.PartInventoryId })
            .IsUnique()
            .HasFilter("\"GcodeFileId\" IS NOT NULL");
        _ = builder.HasIndex(m => new { m.PrintProjectFileId, m.PartInventoryId })
            .IsUnique()
            .HasFilter("\"PrintProjectFileId\" IS NOT NULL");

        _ = builder.HasOne(m => m.PartInventory)
            .WithMany(p => p.Mappings)
            .HasForeignKey(m => m.PartInventoryId)
            .OnDelete(DeleteBehavior.Cascade);

        _ = builder.HasOne(m => m.GcodeFile)
            .WithMany()
            .HasForeignKey(m => m.GcodeFileId)
            .OnDelete(DeleteBehavior.Cascade);

        _ = builder.HasOne(m => m.PrintProjectFile)
            .WithMany()
            .HasForeignKey(m => m.PrintProjectFileId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
