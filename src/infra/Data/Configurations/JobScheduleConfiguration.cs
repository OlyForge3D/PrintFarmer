using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity configuration for JobSchedule (Phase 4.1 scheduling support).
/// </summary>
public class JobScheduleConfiguration : IEntityTypeConfiguration<JobSchedule>
{
    public void Configure(EntityTypeBuilder<JobSchedule> builder)
    {
        _ = builder.HasKey(js => js.Id);
        _ = builder.Property(js => js.TimeZone).IsRequired().HasDefaultValue("UTC");
        _ = builder.Property(js => js.IsActive).HasDefaultValue(true);
        _ = builder.Property(js => js.IsPaused).HasDefaultValue(false);

        // Foreign Key - one-to-one relationship with PrintJob
        _ = builder.HasOne(js => js.PrintJob)
            .WithOne(j => j.Schedule)
            .HasForeignKey<JobSchedule>(js => js.PrintJobId)
            .OnDelete(DeleteBehavior.Cascade);

        // Indexes for querying
        _ = builder.HasIndex(js => js.ScheduledStartTime);
        _ = builder.HasIndex(js => js.IsActive);
        _ = builder.HasIndex(js => new { js.IsActive, js.IsPaused });
    }
}
