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
            .WithMany(p => p.MaintenanceLogs)
            .HasForeignKey(l => l.PrinterId)
            .OnDelete(DeleteBehavior.Cascade);

        // Relationship with PrinterMaintenanceSchedule (optional)
        _ = builder.HasOne(l => l.PrinterMaintenanceSchedule)
            .WithMany()
            .HasForeignKey(l => l.PrinterMaintenanceScheduleId)
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

        // Optional physical toolhead scope (issue #711, F6). Null = printer-wide log.
        _ = builder.HasOne(l => l.Toolhead)
            .WithMany()
            .HasForeignKey(l => l.ToolheadId)
            .OnDelete(DeleteBehavior.Restrict);

        // Indexes for efficient queries
        _ = builder.HasIndex(l => l.PrinterId);
        _ = builder.HasIndex(l => l.PrinterMaintenanceScheduleId);

        // NOTE: the ResolvedAlertId index is declared in AppDbContext.OnModelCreating as a
        // provider-switched FILTERED-UNIQUE index (issue #711, Finding H7) so at most one
        // completion log can link to a given alert. The filter SQL is provider-specific and
        // therefore cannot live here.
        _ = builder.HasIndex(l => l.MaintenanceTaskId);
        _ = builder.HasIndex(l => l.ToolheadId);
        _ = builder.HasIndex(l => l.PerformedAt);
    }
}
