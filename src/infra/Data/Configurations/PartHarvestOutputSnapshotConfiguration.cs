using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>EF configuration for immutable final harvest output rows.</summary>
public sealed class PartHarvestOutputSnapshotConfiguration
    : IEntityTypeConfiguration<PartHarvestOutputSnapshot>
{
    public void Configure(EntityTypeBuilder<PartHarvestOutputSnapshot> builder)
    {
        _ = builder.ToTable(table =>
        {
            _ = table.HasCheckConstraint(
                "CK_PartHarvestOutputSnapshots_Quantity_Positive",
                "\"Quantity\" > 0");
            _ = table.HasCheckConstraint(
                "CK_PartHarvestOutputSnapshots_Sequence_NonNegative",
                "\"Sequence\" >= 0");
            _ = table.HasCheckConstraint(
                "CK_PartHarvestOutputSnapshots_Sku_Normalized",
                "\"Sku\" = UPPER(\"Sku\")");
            _ = table.HasCheckConstraint(
                "CK_PartHarvestOutputSnapshots_ExpectedBin_Consistent",
                "(\"ExpectedBinId\" IS NULL AND \"ExpectedBinCode\" IS NULL) OR (\"ExpectedBinId\" IS NOT NULL AND \"ExpectedBinCode\" IS NOT NULL)");
        });
        _ = builder.HasKey(snapshot => snapshot.Id);
        _ = builder.Property(snapshot => snapshot.Sku).IsRequired().HasMaxLength(64);
        _ = builder.Property(snapshot => snapshot.Quantity).IsRequired();
        _ = builder.Property(snapshot => snapshot.ExpectedBinCode).HasMaxLength(128);
        _ = builder.Property(snapshot => snapshot.ActualBinCode).IsRequired().HasMaxLength(128);
        _ = builder.Property(snapshot => snapshot.Origin).HasConversion<string>().HasMaxLength(32);
        _ = builder.Property(snapshot => snapshot.OverrideReason).HasMaxLength(1000);
        _ = builder.Property(snapshot => snapshot.Sequence).IsRequired();
        _ = builder.Property(snapshot => snapshot.CreatedAt).IsRequired();

        _ = builder.HasIndex(snapshot => new { snapshot.PrintJobId, snapshot.Sequence }).IsUnique();
        _ = builder.HasIndex(snapshot => snapshot.PartInventoryAdjustmentId).IsUnique();
        _ = builder.HasIndex(snapshot => snapshot.PartInventoryId);
        _ = builder.HasIndex(snapshot => snapshot.ExpectedBinId);
        _ = builder.HasIndex(snapshot => snapshot.ActualBinId);
        _ = builder.HasIndex(snapshot => snapshot.SourceMappingId);

        _ = builder.HasOne(snapshot => snapshot.PrintJob)
            .WithMany()
            .HasForeignKey(snapshot => snapshot.PrintJobId)
            .OnDelete(DeleteBehavior.Cascade);
        _ = builder.HasOne(snapshot => snapshot.PartInventory)
            .WithMany()
            .HasForeignKey(snapshot => snapshot.PartInventoryId)
            .OnDelete(DeleteBehavior.Restrict);
        _ = builder.HasOne(snapshot => snapshot.PartInventoryAdjustment)
            .WithMany()
            .HasForeignKey(snapshot => snapshot.PartInventoryAdjustmentId)
            .OnDelete(DeleteBehavior.Restrict);
        _ = builder.HasOne(snapshot => snapshot.ExpectedBin)
            .WithMany()
            .HasForeignKey(snapshot => snapshot.ExpectedBinId)
            .OnDelete(DeleteBehavior.Restrict);
        _ = builder.HasOne(snapshot => snapshot.ActualBin)
            .WithMany()
            .HasForeignKey(snapshot => snapshot.ActualBinId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
