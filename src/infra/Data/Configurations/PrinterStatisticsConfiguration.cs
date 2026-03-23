using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class PrinterStatisticsConfiguration : IEntityTypeConfiguration<PrinterStatistics>
{
    public void Configure(EntityTypeBuilder<PrinterStatistics> builder)
    {
        _ = builder.HasKey(s => s.Id);

        // One-to-one with Printer (PrinterId should match Id)
        _ = builder.HasOne(s => s.Printer)
            .WithOne(p => p.Statistics)
            .HasForeignKey<PrinterStatistics>(s => s.PrinterId)
            .OnDelete(DeleteBehavior.Cascade);

        // Index for efficient queries
        _ = builder.HasIndex(s => s.PrinterId).IsUnique();
        _ = builder.HasIndex(s => s.LastSyncTime);
    }
}
