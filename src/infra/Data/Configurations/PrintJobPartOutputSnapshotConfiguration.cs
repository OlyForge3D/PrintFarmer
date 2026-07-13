using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>EF configuration for immutable first-dispatch output rows.</summary>
public sealed class PrintJobPartOutputSnapshotConfiguration
    : IEntityTypeConfiguration<PrintJobPartOutputSnapshot>
{
    public void Configure(EntityTypeBuilder<PrintJobPartOutputSnapshot> builder)
    {
        _ = builder.ToTable(table =>
        {
            _ = table.HasCheckConstraint(
                "CK_PrintJobPartOutputSnapshots_Quantity_Positive",
                "\"QuantityPerPrint\" > 0");
            _ = table.HasCheckConstraint(
                "CK_PrintJobPartOutputSnapshots_Sequence_NonNegative",
                "\"Sequence\" >= 0");
            _ = table.HasCheckConstraint(
                "CK_PrintJobPartOutputSnapshots_Sku_Normalized",
                "\"Sku\" = UPPER(\"Sku\")");
            _ = table.HasCheckConstraint(
                "CK_PrintJobPartOutputSnapshots_ExpectedBin_Consistent",
                "(\"ExpectedBinId\" IS NULL AND \"ExpectedBinCode\" IS NULL) OR (\"ExpectedBinId\" IS NOT NULL AND \"ExpectedBinCode\" IS NOT NULL)");
        });
        _ = builder.HasKey(snapshot => snapshot.Id);
        _ = builder.Property(snapshot => snapshot.Sku).IsRequired().HasMaxLength(64);
        _ = builder.Property(snapshot => snapshot.QuantityPerPrint).IsRequired();
        _ = builder.Property(snapshot => snapshot.ExpectedBinCode).HasMaxLength(128);
        _ = builder.Property(snapshot => snapshot.SourceKind).HasConversion<string>().HasMaxLength(32);
        _ = builder.Property(snapshot => snapshot.Sequence).IsRequired();
        _ = builder.Property(snapshot => snapshot.CreatedAt).IsRequired();

        _ = builder.HasIndex(snapshot => new { snapshot.PrintJobId, snapshot.Sequence }).IsUnique();
        _ = builder.HasIndex(snapshot => snapshot.PartInventoryId);
        _ = builder.HasIndex(snapshot => snapshot.ExpectedBinId);
        _ = builder.HasIndex(snapshot => snapshot.SourceMappingId);

        _ = builder.HasOne(snapshot => snapshot.PrintJob)
            .WithMany()
            .HasForeignKey(snapshot => snapshot.PrintJobId)
            .OnDelete(DeleteBehavior.Cascade);
        _ = builder.HasOne(snapshot => snapshot.PartInventory)
            .WithMany()
            .HasForeignKey(snapshot => snapshot.PartInventoryId)
            .OnDelete(DeleteBehavior.Restrict);
        _ = builder.HasOne(snapshot => snapshot.ExpectedBin)
            .WithMany()
            .HasForeignKey(snapshot => snapshot.ExpectedBinId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
