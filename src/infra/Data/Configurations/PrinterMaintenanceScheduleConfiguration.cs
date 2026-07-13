using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class PrinterMaintenanceScheduleConfiguration : IEntityTypeConfiguration<PrinterMaintenanceSchedule>
{
    public void Configure(EntityTypeBuilder<PrinterMaintenanceSchedule> builder)
    {
        _ = builder.HasKey(s => s.Id);
        _ = builder.Property(s => s.Notes).HasMaxLength(1000);

        _ = builder.HasOne(s => s.MaintenancePlan)
            .WithMany()
            .HasForeignKey(s => s.MaintenancePlanId)
            .OnDelete(DeleteBehavior.Restrict);

        _ = builder.HasOne(s => s.Printer)
            .WithMany()
            .HasForeignKey(s => s.PrinterId)
            .OnDelete(DeleteBehavior.Cascade);

        // Optional physical toolhead scope (issue #711, F6). Null preserves legacy
        // printer-wide semantics for existing schedules.
        _ = builder.HasOne(s => s.Toolhead)
            .WithMany()
            .HasForeignKey(s => s.ToolheadId)
            .OnDelete(DeleteBehavior.SetNull);

        // Uniqueness across (Plan, Printer, Toolhead). The nullable Toolhead column has
        // provider-specific null semantics (SQL Server auto-adds an IS NOT NULL filter;
        // PostgreSQL treats NULLs as distinct). Service-layer validation enforces the
        // legacy "one printer-wide schedule per (plan, printer)" invariant when ToolheadId
        // is null, so the index remains portable across providers.
        _ = builder.HasIndex(s => new { s.MaintenancePlanId, s.PrinterId, s.ToolheadId })
            .IsUnique()
            .HasDatabaseName("UX_PrinterMaintenanceSchedules_Plan_Printer_Toolhead");

        _ = builder.HasIndex(s => s.PrinterId);
        _ = builder.HasIndex(s => s.ToolheadId);
        _ = builder.HasIndex(s => s.IsActive);
    }
}
