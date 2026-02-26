using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class MaintenanceTaskComponentConfiguration : IEntityTypeConfiguration<MaintenanceTaskComponent>
{
    public void Configure(EntityTypeBuilder<MaintenanceTaskComponent> builder)
    {
        _ = builder.HasKey(tc => tc.Id);

        _ = builder.HasOne(tc => tc.MaintenanceTask)
            .WithMany(t => t.TaskComponents)
            .HasForeignKey(tc => tc.MaintenanceTaskId)
            .OnDelete(DeleteBehavior.Cascade);

        _ = builder.HasOne(tc => tc.MaintenanceComponent)
            .WithMany(c => c.TaskComponents)
            .HasForeignKey(tc => tc.MaintenanceComponentId)
            .OnDelete(DeleteBehavior.Restrict);

        // Prevent duplicate task-component pairs
        _ = builder.HasIndex(tc => new { tc.MaintenanceTaskId, tc.MaintenanceComponentId })
            .IsUnique();
    }
}
