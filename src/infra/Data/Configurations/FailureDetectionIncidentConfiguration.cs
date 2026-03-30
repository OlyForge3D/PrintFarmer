using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity Framework configuration for persisted failure-detection incidents.
/// </summary>
public sealed class FailureDetectionIncidentConfiguration : IEntityTypeConfiguration<FailureDetectionIncident>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<FailureDetectionIncident> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        _ = builder.HasKey(incident => incident.Id);

        _ = builder.Property(incident => incident.JobName).HasMaxLength(255);
        _ = builder.Property(incident => incident.FileName).HasMaxLength(255);
        _ = builder.Property(incident => incident.Confidence).HasColumnType("decimal(5,4)");
        _ = builder.Property(incident => incident.DetectedAt).IsRequired();
        _ = builder.Property(incident => incident.SnapshotUrl).HasMaxLength(1024);

        _ = builder.HasOne(incident => incident.Printer)
            .WithMany()
            .HasForeignKey(incident => incident.PrinterId)
            .OnDelete(DeleteBehavior.Cascade);

        _ = builder.HasIndex(incident => incident.DetectedAt);
        _ = builder.HasIndex(incident => new { incident.PrinterId, incident.DetectedAt });
    }
}
