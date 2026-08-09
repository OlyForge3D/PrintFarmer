using Farm.Slicer.Module.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Slicer.Module.Data.Configurations;

/// <summary>
/// Entity configuration for <see cref="SliceJob"/> — slicing job queue entries.
/// </summary>
public class SliceJobConfiguration : IEntityTypeConfiguration<SliceJob>
{
    /// <summary>Database name of the owner/project-scoped correlation uniqueness index.</summary>
    public const string CorrelationUniqueIndexName = "IX_SliceJobs_Owner_Project_Correlation";

    /// <summary>Database name of the owner/project-scoped checksum uniqueness index.</summary>
    public const string ChecksumUniqueIndexName = "IX_SliceJobs_Owner_Project_Checksum";

    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<SliceJob> builder)
    {
        _ = builder.HasKey(j => j.Id);

        // Properties
        _ = builder.Property(j => j.UserId).IsRequired();
        _ = builder.Property(j => j.ModelFileUrl).IsRequired().HasMaxLength(2048);
        _ = builder.Property(j => j.ModelFileName).IsRequired().HasMaxLength(512);
        _ = builder.Property(j => j.SlicerEngine).IsRequired();
        _ = builder.Property(j => j.SlicerEngineVersion).HasMaxLength(32);
        _ = builder.Property(j => j.SlicerEngineName).HasMaxLength(32);
        _ = builder.Property(j => j.NormalizedEngine).IsRequired().HasDefaultValue(0);
        _ = builder.Property(j => j.ModelSha256).HasMaxLength(64);
        _ = builder.Property(j => j.SlicerProfileJson).HasColumnType("TEXT");
        _ = builder.Property(j => j.SlicerProfileId);
        _ = builder.Property(j => j.RequiredCapabilitiesJson).HasColumnType("TEXT");
        _ = builder.Property(j => j.Status).IsRequired().HasMaxLength(50);
        _ = builder.Property(j => j.Priority).IsRequired();
        _ = builder.Property(j => j.QueuedAt).IsRequired();
        _ = builder.Property(j => j.ResultFileUrl).HasMaxLength(2048);
        _ = builder.Property(j => j.ErrorMessage).HasColumnType("TEXT");
        _ = builder.Property(j => j.ProgressMessage).HasMaxLength(512);
        _ = builder.Property(j => j.CreatedAt).IsRequired();
        _ = builder.Property(j => j.UpdatedAt).IsRequired();
        _ = builder.Property(j => j.ClaimToken);
        _ = builder.Property(j => j.ExtruderFilamentProfileNamesJson).HasColumnType("TEXT");
        _ = builder.Property(j => j.ModelFileUrlsJson).HasColumnType("TEXT");

        // Lease fencing: a monotonic counter bumped by every successful atomic claim.
        _ = builder.Property(j => j.LeaseFence).IsRequired().HasDefaultValue(0L);

        // Exact resolved upstream-Orca profile snapshots delivered to the claiming worker.
        _ = builder.Property(j => j.MachineProfileJson).HasColumnType("TEXT");
        _ = builder.Property(j => j.ProcessProfileJson).HasColumnType("TEXT");
        _ = builder.Property(j => j.FilamentProfileJson).HasColumnType("TEXT");
        _ = builder.Property(j => j.MachineProfileSha256).HasMaxLength(64);
        _ = builder.Property(j => j.ProcessProfileSha256).HasMaxLength(64);
        _ = builder.Property(j => j.FilamentProfileSha256).HasMaxLength(64);
        _ = builder.Property(j => j.SlicerDistribution).HasMaxLength(64);
        _ = builder.Property(j => j.SlicerVersion).HasMaxLength(64);
        _ = builder.Property(j => j.SlicerContainerDigest).HasMaxLength(128);

        // Indexes for efficient querying
        _ = builder.HasIndex(j => j.UserId);
        _ = builder.HasIndex(j => j.PrinterId);
        _ = builder.HasIndex(j => j.Status);
        _ = builder.HasIndex(j => j.QueuedAt);
        _ = builder.HasIndex(j => new { j.Status, j.Priority, j.QueuedAt }); // Queue processing

        // Covering index for queue-stat aggregation
        // (EfSliceJobRepository.GetQueueCountsAsync): the query filters WHERE Status IN (four
        // values) and GROUPs BY (NormalizedEngine, Status), with no filter on NormalizedEngine.
        // Status leads so the four reported statuses can be seeked directly (skipping
        // Cancelled rows on this append-only, never-pruned table), with NormalizedEngine as the
        // covering second column so per-engine/per-status counts come from the index alone.
        _ = builder.HasIndex(j => new { j.Status, j.NormalizedEngine })
            .HasDatabaseName("IX_SliceJobs_Status_NormalizedEngine");

        _ = builder.HasIndex(j => j.WorkerId);
        _ = builder.HasIndex(j => j.SlicerProfileId);
        _ = builder.HasIndex(j => j.Model3DId);
        _ = builder.HasIndex(j => j.CalibrationOrchestrationId);

        // Owner/project-scoped idempotency. The filter that SQL Server needs is applied by
        // SlicerDbContext.OnModelCreating, which is the only place that knows the provider.
        _ = builder.Property(j => j.IdempotencyScopeId).IsRequired().HasDefaultValue(Guid.Empty);
        _ = builder.HasIndex(j => new { j.UserId, j.IdempotencyScopeId, j.CorrelationId })
            .IsUnique()
            .HasDatabaseName(CorrelationUniqueIndexName);
        _ = builder.HasIndex(j => new { j.UserId, j.IdempotencyScopeId, j.Checksum })
            .IsUnique()
            .HasDatabaseName(ChecksumUniqueIndexName);

        // Slicer-internal FK: SliceJob → ProcessProfile (optional).
        // If profile deleted later we retain immutable snapshot JSON.
        _ = builder.HasOne(j => j.SlicerProfile)
            .WithMany()
            .HasForeignKey(j => j.SlicerProfileId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
