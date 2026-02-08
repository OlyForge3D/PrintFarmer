using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity configuration for PrintJobStatistics (Phase 4.2 analytics).
/// </summary>
public class PrintJobStatisticsConfiguration : IEntityTypeConfiguration<PrintJobStatistics>
{
    public void Configure(EntityTypeBuilder<PrintJobStatistics> builder)
    {
        _ = builder.HasKey(s => s.Id);
        _ = builder.Property(s => s.Material).HasMaxLength(100);
        _ = builder.Property(s => s.FailureReason).HasMaxLength(500);
        _ = builder.Property(s => s.CreatedAtUtc).IsRequired();
        _ = builder.Property(s => s.UpdatedAtUtc).IsRequired();

        // Foreign Key - one-to-one relationship with PrintJob
        _ = builder.HasOne(s => s.PrintJob)
            .WithOne(j => j.Statistics)
            .HasForeignKey<PrintJobStatistics>(s => s.PrintJobId)
            .OnDelete(DeleteBehavior.Cascade);

        // Foreign Key to PrinterModel (optional)
        _ = builder.HasOne(s => s.PrinterModel)
            .WithMany()
            .HasForeignKey(s => s.PrinterModelId)
            .OnDelete(DeleteBehavior.NoAction);

        // Indexes for prediction queries
        _ = builder.HasIndex(s => s.CompletedAtUtc);
        _ = builder.HasIndex(s => s.IsSuccess);
        _ = builder.HasIndex(s => new { s.PrinterModelId, s.Material, s.IsSuccess });
        _ = builder.HasIndex(s => new { s.PrinterModelId, s.Material, s.CompletedAtUtc });
    }
}
