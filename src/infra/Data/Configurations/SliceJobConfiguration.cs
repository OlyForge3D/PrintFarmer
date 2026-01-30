using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity configuration for SliceJob - slicing job queue entries.
/// </summary>
public class SliceJobConfiguration : IEntityTypeConfiguration<SliceJob>
{
    public void Configure(EntityTypeBuilder<SliceJob> builder)
    {
        _ = builder.HasKey(j => j.Id);

        // Properties
        _ = builder.Property(j => j.UserId).IsRequired();
        _ = builder.Property(j => j.ModelFileUrl).IsRequired().HasMaxLength(2048);
        _ = builder.Property(j => j.ModelFileName).IsRequired().HasMaxLength(512);
        _ = builder.Property(j => j.SlicerEngine).IsRequired();
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

        // Indexes for efficient querying
        _ = builder.HasIndex(j => j.UserId);
        _ = builder.HasIndex(j => j.PrinterId);
        _ = builder.HasIndex(j => j.Status);
        _ = builder.HasIndex(j => j.QueuedAt);
        _ = builder.HasIndex(j => new { j.Status, j.Priority, j.QueuedAt }); // For queue processing
        _ = builder.HasIndex(j => j.WorkerId);
        _ = builder.HasIndex(j => j.SlicerProfileId);

        // Foreign key to SlicerProfile (optional reference). If profile deleted later we retain immutable snapshot JSON.
        _ = builder.HasOne(j => j.SlicerProfile)
            .WithMany()
            .HasForeignKey(j => j.SlicerProfileId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
