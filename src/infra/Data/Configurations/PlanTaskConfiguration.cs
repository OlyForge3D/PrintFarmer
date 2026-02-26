using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class PlanTaskConfiguration : IEntityTypeConfiguration<PlanTask>
{
    public void Configure(EntityTypeBuilder<PlanTask> builder)
    {
        _ = builder.HasKey(pt => pt.Id);

        _ = builder.HasOne(pt => pt.MaintenancePlan)
            .WithMany(p => p.PlanTasks)
            .HasForeignKey(pt => pt.MaintenancePlanId)
            .OnDelete(DeleteBehavior.Cascade);

        _ = builder.HasOne(pt => pt.MaintenanceTask)
            .WithMany(t => t.PlanTasks)
            .HasForeignKey(pt => pt.MaintenanceTaskId)
            .OnDelete(DeleteBehavior.Cascade);

        _ = builder.HasIndex(pt => new { pt.MaintenancePlanId, pt.MaintenanceTaskId }).IsUnique();
        _ = builder.HasIndex(pt => pt.MaintenanceTaskId);
    }
}
