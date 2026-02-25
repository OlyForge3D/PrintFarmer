using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class MaintenanceComponentConfiguration : IEntityTypeConfiguration<MaintenanceComponent>
{
    public void Configure(EntityTypeBuilder<MaintenanceComponent> builder)
    {
        _ = builder.HasKey(c => c.Id);
        _ = builder.Property(c => c.Name).IsRequired().HasMaxLength(200);
        _ = builder.Property(c => c.Sku).HasMaxLength(100);
        _ = builder.Property(c => c.Description).HasMaxLength(1000);
        _ = builder.Property(c => c.Category).IsRequired().HasMaxLength(100);
        _ = builder.Property(c => c.Supplier).HasMaxLength(200);
        _ = builder.Property(c => c.Url).HasMaxLength(500);
        _ = builder.Property(c => c.UnitCost).HasColumnType("decimal(10,2)");

        _ = builder.HasMany(c => c.TaskComponents)
            .WithOne(tc => tc.MaintenanceComponent)
            .HasForeignKey(tc => tc.MaintenanceComponentId)
            .OnDelete(DeleteBehavior.Restrict);

        _ = builder.HasIndex(c => c.Category);
        _ = builder.HasIndex(c => c.Name);
    }
}
