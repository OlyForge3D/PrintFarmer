using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>Entity configuration for the immutable printed-part adjustment ledger.</summary>
public class PartInventoryAdjustmentConfiguration : IEntityTypeConfiguration<PartInventoryAdjustment>
{
    public void Configure(EntityTypeBuilder<PartInventoryAdjustment> builder)
    {
        _ = builder.HasKey(a => a.Id);
        _ = builder.Property(a => a.Delta).IsRequired();
        _ = builder.Property(a => a.Reason).HasConversion<string>().HasMaxLength(32).IsRequired();
        _ = builder.Property(a => a.OperationKey).HasMaxLength(128);
        _ = builder.Property(a => a.Notes).HasMaxLength(1000);
        _ = builder.Property(a => a.UserId).HasMaxLength(450);
        _ = builder.Property(a => a.CreatedAt).IsRequired();

        _ = builder.HasIndex(a => a.PartInventoryId);
        _ = builder.HasIndex(a => new { a.PartInventoryId, a.CreatedAt });
        _ = builder.HasIndex(a => a.PrintJobId);
        _ = builder.HasIndex(a => a.BinId);
        _ = builder.HasIndex(a => a.Reason);

        // Unique idempotency key when present. Filtered so multiple null keys are allowed.
        _ = builder.HasIndex(a => a.OperationKey)
            .IsUnique()
            .HasFilter("\"OperationKey\" IS NOT NULL");

        _ = builder.HasOne(a => a.PartInventory)
            .WithMany(p => p.Adjustments)
            .HasForeignKey(a => a.PartInventoryId)
            .OnDelete(DeleteBehavior.Cascade);

        _ = builder.HasOne(a => a.Bin)
            .WithMany()
            .HasForeignKey(a => a.BinId)
            .OnDelete(DeleteBehavior.SetNull);

        _ = builder.HasOne(a => a.PrintJob)
            .WithMany()
            .HasForeignKey(a => a.PrintJobId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
