using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class MaintenanceLogConfiguration : IEntityTypeConfiguration<MaintenanceLog>
{
    public void Configure(EntityTypeBuilder<MaintenanceLog> builder)
    {
        _ = builder.HasKey(l => l.Id);
        _ = builder.Property(l => l.TaskName).IsRequired().HasMaxLength(128);
        _ = builder.Property(l => l.Notes).HasMaxLength(2000);
        _ = builder.Property(l => l.Component).HasMaxLength(64);
        _ = builder.Property(l => l.PerformedBy).HasMaxLength(128);
        _ = builder.Property(l => l.PartsReplaced).HasMaxLength(512);

        // Relationship with Printer (required)
        _ = builder.HasOne(l => l.Printer)
            .WithMany()
            .HasForeignKey(l => l.PrinterId)
            .OnDelete(DeleteBehavior.Cascade);

        // Relationship with MaintenanceSchedule (optional)
        _ = builder.HasOne(l => l.MaintenanceSchedule)
            .WithMany()
            .HasForeignKey(l => l.MaintenanceScheduleId)
            .OnDelete(DeleteBehavior.SetNull);

        // Relationship with MaintenanceAlert (optional)
        _ = builder.HasOne(l => l.ResolvedAlert)
            .WithMany()
            .HasForeignKey(l => l.ResolvedAlertId)
            .OnDelete(DeleteBehavior.SetNull);

        // Relationship with MaintenanceTask (optional)
        _ = builder.HasOne(l => l.MaintenanceTask)
            .WithMany()
            .HasForeignKey(l => l.MaintenanceTaskId)
            .OnDelete(DeleteBehavior.SetNull);

        // Indexes for efficient queries
        _ = builder.HasIndex(l => l.PrinterId);
        _ = builder.HasIndex(l => l.MaintenanceScheduleId);
        _ = builder.HasIndex(l => l.ResolvedAlertId);
        _ = builder.HasIndex(l => l.MaintenanceTaskId);
        _ = builder.HasIndex(l => l.PerformedAt);
    }
}
