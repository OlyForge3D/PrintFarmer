using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class CameraConfiguration : IEntityTypeConfiguration<Camera>
{
    public void Configure(EntityTypeBuilder<Camera> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Cameras");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Name)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(c => c.Description)
            .HasMaxLength(500);

        builder.Property(c => c.StreamUrl)
            .HasMaxLength(500);

        builder.Property(c => c.SnapshotUrl)
            .HasMaxLength(500);

        builder.Property(c => c.Location)
            .HasMaxLength(100);

        builder.Property(c => c.IsEnabled)
            .HasDefaultValue(true);

        builder.Property(c => c.SortOrder)
            .HasDefaultValue(0);

        builder.Property(c => c.HealthMessage)
            .HasMaxLength(500);

        // Enum conversions (store as strings)
        builder.Property(c => c.Source)
            .HasConversion<string>()
            .IsRequired();

        builder.Property(c => c.CameraType)
            .HasConversion<string>()
            .IsRequired();

        builder.Property(c => c.HealthStatus)
            .HasConversion<string>()
            .IsRequired();

        builder.Property(c => c.UserModified)
            .HasDefaultValue(false);

        // Optional explicit location (effective location prefers the single linked printer's location).
        builder.HasOne(c => c.AssignedLocation)
            .WithMany()
            .HasForeignKey(c => c.LocationId)
            .OnDelete(DeleteBehavior.SetNull);

        // Many-to-many camera <-> printer via the explicit CameraPrinter join entity.
        // Skip navigations (Camera.Printers / Printer.Cameras) keep existing LINQ compiling while
        // PrinterLinks / CameraLinks expose the join payload (IsPrimary, SortOrder).
        builder.HasMany(c => c.Printers)
            .WithMany(p => p.Cameras)
            .UsingEntity<CameraPrinter>(
                right => right.HasOne(cp => cp.Printer)
                    .WithMany(p => p.CameraLinks)
                    .HasForeignKey(cp => cp.PrinterId)
                    .OnDelete(DeleteBehavior.Cascade),
                left => left.HasOne(cp => cp.Camera)
                    .WithMany(c => c.PrinterLinks)
                    .HasForeignKey(cp => cp.CameraId)
                    .OnDelete(DeleteBehavior.Cascade),
                join =>
                {
                    join.ToTable("CameraPrinters");
                    join.HasKey(cp => new { cp.CameraId, cp.PrinterId });
                    join.Property(cp => cp.IsPrimary).HasDefaultValue(false);
                    join.Property(cp => cp.SortOrder).HasDefaultValue(0);
                    join.HasIndex(cp => cp.PrinterId);
                });

        // Indexes
        builder.HasIndex(c => c.Name);
        builder.HasIndex(c => c.IsEnabled);
        builder.HasIndex(c => c.SortOrder);
        builder.HasIndex(c => c.LocationId);
        builder.HasIndex(c => c.Source);
    }
}
