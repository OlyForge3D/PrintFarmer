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

        // Foreign key relationship to Printer
        builder.HasOne(c => c.Printer)
            .WithMany(p => p.Cameras)
            .HasForeignKey(c => c.PrinterId)
            .OnDelete(DeleteBehavior.Cascade);

        // Indexes
        builder.HasIndex(c => c.Name);
        builder.HasIndex(c => c.IsEnabled);
        builder.HasIndex(c => c.SortOrder);
        builder.HasIndex(c => c.PrinterId);
        builder.HasIndex(c => c.Source);
    }
}
