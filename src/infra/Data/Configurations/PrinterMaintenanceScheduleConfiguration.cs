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

        _ = builder.HasIndex(s => new { s.MaintenancePlanId, s.PrinterId }).IsUnique();
        _ = builder.HasIndex(s => s.PrinterId);
        _ = builder.HasIndex(s => s.IsActive);
    }
}
