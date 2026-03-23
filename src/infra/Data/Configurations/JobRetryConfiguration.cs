using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity configuration for JobRetry (Phase 4.4 retry tracking).
/// </summary>
public class JobRetryConfiguration : IEntityTypeConfiguration<JobRetry>
{
    public void Configure(EntityTypeBuilder<JobRetry> builder)
    {
        _ = builder.HasKey(jr => jr.Id);
        _ = builder.Property(jr => jr.AttemptNumber).IsRequired();
        _ = builder.Property(jr => jr.ErrorCategory).HasConversion<int>();
        _ = builder.Property(jr => jr.FailureReason).IsRequired().HasMaxLength(500);
        _ = builder.Property(jr => jr.Status).IsRequired().HasMaxLength(50).HasDefaultValue("Pending");
        _ = builder.Property(jr => jr.Notes).HasMaxLength(500);
        _ = builder.Property(jr => jr.CreatedAt).IsRequired();
        _ = builder.Property(jr => jr.UpdatedAt).IsRequired();

        // Foreign Keys - many-to-one relationships with PrintJobs
        _ = builder.HasOne(jr => jr.OriginalJob)
            .WithMany(pj => pj.RetriesAsOriginal)
            .HasForeignKey(jr => jr.OriginalJobId)
            .OnDelete(DeleteBehavior.Restrict); // Prevent deleting original job if retry exists

        _ = builder.HasOne(jr => jr.RetryJob)
            .WithMany(pj => pj.RetriesAsAttempt)
            .HasForeignKey(jr => jr.RetryJobId)
            .OnDelete(DeleteBehavior.Restrict); // Prevent deleting retry job if history exists

        // Indexes for querying retry history
        _ = builder.HasIndex(jr => jr.OriginalJobId);
        _ = builder.HasIndex(jr => jr.RetryJobId);
        _ = builder.HasIndex(jr => new { jr.OriginalJobId, jr.AttemptNumber });
        _ = builder.HasIndex(jr => jr.Status);
        _ = builder.HasIndex(jr => jr.ScheduledRetryTime);
    }
}
