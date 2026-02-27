using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class MaintenanceTaskConfiguration : IEntityTypeConfiguration<MaintenanceTask>
{
    public void Configure(EntityTypeBuilder<MaintenanceTask> builder)
    {
        _ = builder.HasKey(t => t.Id);
        _ = builder.Property(t => t.TaskName).IsRequired().HasMaxLength(200);
        _ = builder.Property(t => t.Description).HasMaxLength(1000);
        _ = builder.Property(t => t.Category).IsRequired().HasMaxLength(100);

        _ = builder.HasMany(t => t.TaskComponents)
            .WithOne(tc => tc.MaintenanceTask)
            .HasForeignKey(tc => tc.MaintenanceTaskId)
            .OnDelete(DeleteBehavior.Cascade);

        _ = builder.HasMany(t => t.PlanTasks)
            .WithOne(pt => pt.MaintenanceTask)
            .HasForeignKey(pt => pt.MaintenanceTaskId)
            .OnDelete(DeleteBehavior.Cascade);

        _ = builder.HasIndex(t => t.IsActive);
        _ = builder.HasIndex(t => t.Category);
        _ = builder.HasIndex(t => t.TaskName);
    }
}
