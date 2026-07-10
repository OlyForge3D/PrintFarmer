using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>Entity configuration for the printed-part SKU catalog.</summary>
public class PartInventoryConfiguration : IEntityTypeConfiguration<PartInventory>
{
    public void Configure(EntityTypeBuilder<PartInventory> builder)
    {
        _ = builder.HasKey(p => p.Id);
        _ = builder.Property(p => p.Sku).IsRequired().HasMaxLength(64);
        _ = builder.Property(p => p.Name).IsRequired().HasMaxLength(200);
        _ = builder.Property(p => p.Description).HasMaxLength(2000);
        _ = builder.Property(p => p.ModelFileRef).HasMaxLength(500);
        _ = builder.Property(p => p.OnHand).IsRequired();
        _ = builder.Property(p => p.ReorderPoint).IsRequired();
        _ = builder.Property(p => p.IsActive).IsRequired();
        _ = builder.Property(p => p.CreatedAt).IsRequired();
        _ = builder.Property(p => p.UpdatedAt).IsRequired();

        _ = builder.HasIndex(p => p.Sku).IsUnique();
        _ = builder.HasIndex(p => p.IsActive);
        _ = builder.HasIndex(p => p.DefaultBinId);

        _ = builder.HasOne(p => p.DefaultBin)
            .WithMany()
            .HasForeignKey(p => p.DefaultBinId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
