using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity configuration for GcodeHarvestQueueItem with status and priority tracking.
/// Includes RowVersion for optimistic concurrency during queue processing.
/// </summary>
public class GcodeHarvestQueueItemConfiguration : IEntityTypeConfiguration<GcodeHarvestQueueItem>
{
    public void Configure(EntityTypeBuilder<GcodeHarvestQueueItem> builder)
    {
        _ = builder.HasKey(q => q.Id);
        _ = builder.Property(q => q.RowVersion).IsRowVersion();
        _ = builder.Property(q => q.PrinterId).IsRequired();
        _ = builder.Property(q => q.QueuedAt).IsRequired();
        _ = builder.Property(q => q.ProcessingStartedAt);
        _ = builder.Property(q => q.CompletedAt);
        _ = builder.Property(q => q.Priority).IsRequired().HasDefaultValue(0);
        _ = builder.Property(q => q.Status).IsRequired().HasConversion<int>();
        _ = builder.Property(q => q.Parameters).IsRequired().HasColumnType("TEXT");
        _ = builder.Property(q => q.ErrorMessage);
        _ = builder.Property(q => q.ErrorDetails).HasColumnType("TEXT");
        _ = builder.Property(q => q.FilesFound).HasDefaultValue(0);
        _ = builder.Property(q => q.FilesAdded).HasDefaultValue(0);
        _ = builder.Property(q => q.FilesSkipped).HasDefaultValue(0);
        _ = builder.Property(q => q.FilesErrored).HasDefaultValue(0);

        // Foreign key to Printer
        _ = builder.HasOne(q => q.Printer)
            .WithMany()
            .HasForeignKey(q => q.PrinterId)
            .OnDelete(DeleteBehavior.Cascade);

        // Indexes for efficient queue processing
        _ = builder.HasIndex(q => new { q.Status, q.Priority, q.QueuedAt });
        _ = builder.HasIndex(q => q.PrinterId);
        _ = builder.HasIndex(q => q.QueuedAt).IsDescending();
        _ = builder.HasIndex(q => q.Status);
    }
}
