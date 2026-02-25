using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class MaintenancePlanConfiguration : IEntityTypeConfiguration<MaintenancePlan>
{
    public void Configure(EntityTypeBuilder<MaintenancePlan> builder)
    {
        _ = builder.HasKey(p => p.Id);
        _ = builder.Property(p => p.Name).IsRequired().HasMaxLength(200);
        _ = builder.Property(p => p.Description).HasMaxLength(1000);

        _ = builder.HasOne(p => p.Printer)
            .WithMany()
            .HasForeignKey(p => p.PrinterId)
            .OnDelete(DeleteBehavior.SetNull);

        _ = builder.HasOne(p => p.PrinterModel)
            .WithMany()
            .HasForeignKey(p => p.PrinterModelId)
            .OnDelete(DeleteBehavior.SetNull);

        _ = builder.HasOne(p => p.Manufacturer)
            .WithMany()
            .HasForeignKey(p => p.ManufacturerId)
            .OnDelete(DeleteBehavior.SetNull);

        _ = builder.HasMany(p => p.Tasks)
            .WithOne(t => t.MaintenancePlan)
            .HasForeignKey(t => t.MaintenancePlanId)
            .OnDelete(DeleteBehavior.Cascade);

        _ = builder.HasIndex(p => p.PrinterId);
        _ = builder.HasIndex(p => p.PrinterModelId);
        _ = builder.HasIndex(p => p.ManufacturerId);
        _ = builder.HasIndex(p => p.IsActive);
    }
}
