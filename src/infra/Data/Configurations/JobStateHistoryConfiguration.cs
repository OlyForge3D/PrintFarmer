using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity Framework configuration for the JobStateHistory entity (Phase 3C).
/// Extracted from AppDbContext.OnModelCreating for better maintainability.
/// </summary>
public class JobStateHistoryConfiguration : IEntityTypeConfiguration<JobStateHistory>
{
    public void Configure(EntityTypeBuilder<JobStateHistory> builder)
    {
        builder.HasKey(h => h.Id);

        // Basic properties
        builder.Property(h => h.JobId).IsRequired();
        builder.Property(h => h.FromState).IsRequired().HasMaxLength(50);
        builder.Property(h => h.ToState).IsRequired().HasMaxLength(50);
        builder.Property(h => h.TransitionedAtUtc).IsRequired();
        builder.Property(h => h.DurationInState).HasConversion<long>();
        builder.Property(h => h.Notes).HasMaxLength(500);
        builder.Property(h => h.CreatedAt).IsRequired();

        // Foreign Key - relationship configured in PrintJobConfiguration
        builder.HasOne(h => h.PrintJob)
            .WithMany(j => j.StateHistory)
            .HasForeignKey(h => h.JobId)
            .OnDelete(DeleteBehavior.Cascade);

        // Indexes
        builder.HasIndex(h => h.JobId);
        builder.HasIndex(h => h.TransitionedAtUtc).IsDescending();
    }
}
