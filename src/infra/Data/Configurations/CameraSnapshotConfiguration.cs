using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class CameraSnapshotConfiguration : IEntityTypeConfiguration<CameraSnapshot>
{
    public void Configure(EntityTypeBuilder<CameraSnapshot> builder)
    {
        _ = builder.HasKey(s => s.Id);

        _ = builder.Property(s => s.EventType).IsRequired().HasMaxLength(50);
        _ = builder.Property(s => s.FilePath).IsRequired().HasMaxLength(500);

        _ = builder.HasOne(s => s.Printer)
            .WithMany()
            .HasForeignKey(s => s.PrinterId)
            .OnDelete(DeleteBehavior.Cascade);

        _ = builder.HasOne(s => s.Camera)
            .WithMany()
            .HasForeignKey(s => s.CameraId)
            .OnDelete(DeleteBehavior.Restrict);

        _ = builder.HasOne(s => s.PrintJob)
            .WithMany()
            .HasForeignKey(s => s.PrintJobId)
            .OnDelete(DeleteBehavior.SetNull);

        _ = builder.HasIndex(s => s.PrinterId);
        _ = builder.HasIndex(s => s.CameraId);
        _ = builder.HasIndex(s => s.PrintJobId);
        _ = builder.HasIndex(s => s.CapturedAt);
    }
}
