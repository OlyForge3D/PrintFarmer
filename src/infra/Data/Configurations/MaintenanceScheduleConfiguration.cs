using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class MaintenanceScheduleConfiguration : IEntityTypeConfiguration<MaintenanceSchedule>
{
    public void Configure(EntityTypeBuilder<MaintenanceSchedule> builder)
    {
        _ = builder.HasKey(s => s.Id);
        _ = builder.Property(s => s.TaskName).IsRequired().HasMaxLength(128);
        _ = builder.Property(s => s.Description).HasMaxLength(512);
        _ = builder.Property(s => s.Component).HasMaxLength(64);

        // Relationship with Printer (optional - null for model-wide defaults)
        _ = builder.HasOne(s => s.Printer)
            .WithMany()
            .HasForeignKey(s => s.PrinterId)
            .OnDelete(DeleteBehavior.Cascade);

        // Relationship with PrinterModel (optional - for model-wide defaults)
        _ = builder.HasOne(s => s.PrinterModel)
            .WithMany()
            .HasForeignKey(s => s.PrinterModelId)
            .OnDelete(DeleteBehavior.Cascade);

        // Indexes for efficient queries
        _ = builder.HasIndex(s => s.PrinterId);
        _ = builder.HasIndex(s => s.PrinterModelId);
        _ = builder.HasIndex(s => new { s.IsActive, s.IsDefault });
    }
}
