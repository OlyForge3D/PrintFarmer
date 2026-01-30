using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class MaintenanceAlertConfiguration : IEntityTypeConfiguration<MaintenanceAlert>
{
    public void Configure(EntityTypeBuilder<MaintenanceAlert> builder)
    {
        _ = builder.HasKey(a => a.Id);
        _ = builder.Property(a => a.Title).IsRequired().HasMaxLength(128);
        _ = builder.Property(a => a.Message).IsRequired().HasMaxLength(512);
        _ = builder.Property(a => a.AcknowledgedBy).HasMaxLength(128);
        _ = builder.Property(a => a.ResolvedBy).HasMaxLength(128);
        _ = builder.Property(a => a.DismissedBy).HasMaxLength(128);
        _ = builder.Property(a => a.DismissalReason).HasMaxLength(512);

        // Relationship with Printer (required)
        _ = builder.HasOne(a => a.Printer)
            .WithMany()
            .HasForeignKey(a => a.PrinterId)
            .OnDelete(DeleteBehavior.Cascade);

        // Relationship with MaintenanceSchedule (required)
        _ = builder.HasOne(a => a.MaintenanceSchedule)
            .WithMany()
            .HasForeignKey(a => a.MaintenanceScheduleId)
            .OnDelete(DeleteBehavior.Cascade);

        // Indexes for efficient queries
        _ = builder.HasIndex(a => a.PrinterId);
        _ = builder.HasIndex(a => a.MaintenanceScheduleId);
        _ = builder.HasIndex(a => new { a.Status, a.Severity });
        _ = builder.HasIndex(a => a.CreatedAt);
    }
}
