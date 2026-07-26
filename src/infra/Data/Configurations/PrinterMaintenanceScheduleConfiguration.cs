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

        // Schedules cannot be silently orphaned when a Printer is deleted (schedules carry
        // required PrinterId). Callers use EfPrintersRepository.RemoveAsync which explicitly
        // deletes schedules before removing the printer (matching GcodeFiles/PrintJobs/
        // GcodeHarvestOperations pattern). Restrict (not Cascade) here breaks the SQL Server
        // multi-cascading-path graph Printers ⇒ Schedules ⇒ MaintenanceAlerts (SetNull) that
        // triggered error 1785. Dallas cascade adjudication for #953 / #723.
        _ = builder.HasOne(s => s.Printer)
            .WithMany()
            .HasForeignKey(s => s.PrinterId)
            .OnDelete(DeleteBehavior.Restrict);

        // Optional physical toolhead scope (issue #711, F6). Null preserves legacy
        // printer-wide semantics for existing schedules.
        _ = builder.HasOne(s => s.Toolhead)
            .WithMany()
            .HasForeignKey(s => s.ToolheadId)
            .OnDelete(DeleteBehavior.Restrict);

        // Uniqueness across (Plan, Printer, Toolhead) for toolhead-scoped schedules. The
        // nullable Toolhead column has provider-specific null semantics: SQL Server auto-adds
        // an [ToolheadId] IS NOT NULL filter, and PostgreSQL treats NULLs as distinct. As a
        // result this index does NOT enforce uniqueness for printer-wide (null-toolhead)
        // schedules on either provider.
        _ = builder.HasIndex(s => new { s.MaintenancePlanId, s.PrinterId, s.ToolheadId })
            .IsUnique()
            .HasDatabaseName("UX_PrinterMaintenanceSchedules_Plan_Printer_Toolhead");

        // Second unique index enforcing the legacy "one printer-wide schedule per
        // (plan, printer)" invariant at the database level (issue #711, F6 remediation).
        // Without this, two concurrent deployments of the same (plan, printer, null) both
        // succeed — the composite index above excludes null rows on both providers. The
        // ANSI double-quoted filter ("ToolheadId" IS NULL) is portable across PostgreSQL,
        // SQL Server (QUOTED_IDENTIFIER ON), and SQLite, mirroring the existing NfcDevice
        // partial-index precedent, so a single fluent definition yields a correct filtered
        // (SQL Server / SQLite) / partial (PostgreSQL) index for every provider.
        _ = builder.HasIndex(s => new { s.MaintenancePlanId, s.PrinterId })
            .IsUnique()
            .HasFilter("\"ToolheadId\" IS NULL")
            .HasDatabaseName("UX_PrinterMaintenanceSchedules_Plan_Printer_NullToolhead");

        _ = builder.HasIndex(s => s.PrinterId);
        _ = builder.HasIndex(s => s.ToolheadId);
        _ = builder.HasIndex(s => s.IsActive);
    }
}
