using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity configuration for JobExecution (Phase 4.1 execution tracking).
/// Includes RowVersion for optimistic concurrency during scheduler/worker updates.
/// </summary>
public class JobExecutionConfiguration : IEntityTypeConfiguration<JobExecution>
{
    public void Configure(EntityTypeBuilder<JobExecution> builder)
    {
        _ = builder.HasKey(je => je.Id);
        _ = builder.Property(je => je.RowVersion).IsRowVersion();
        _ = builder.Property(je => je.Status).IsRequired().HasMaxLength(50);
        _ = builder.Property(je => je.Message).HasMaxLength(500);

        // Foreign Key
        _ = builder.HasOne(je => je.JobSchedule)
            .WithMany(js => js.Executions)
            .HasForeignKey(je => je.JobScheduleId)
            .OnDelete(DeleteBehavior.Cascade);

        // Indexes for querying execution history
        _ = builder.HasIndex(je => new { je.JobScheduleId, je.ScheduledExecutionTime });
        _ = builder.HasIndex(je => je.Status);
        _ = builder.HasIndex(je => je.ScheduledExecutionTime);
    }
}
