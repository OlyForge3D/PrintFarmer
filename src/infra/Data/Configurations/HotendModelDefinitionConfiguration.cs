using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class HotendModelDefinitionConfiguration : IEntityTypeConfiguration<HotendModelDefinition>
{
    public void Configure(EntityTypeBuilder<HotendModelDefinition> builder)
    {
        _ = builder.HasKey(h => h.Id);
        _ = builder.Property(h => h.Name).IsRequired().HasMaxLength(128);
        _ = builder.Property(h => h.Description).HasMaxLength(512);
        _ = builder.Property(h => h.MaxTemp).HasDefaultValue(300);
        _ = builder.Property(h => h.IsHighFlow).HasDefaultValue(false);

        // Foreign Key to Manufacturer
        _ = builder.HasOne(h => h.Manufacturer)
            .WithMany()
            .HasForeignKey(h => h.ManufacturerId)
            .OnDelete(DeleteBehavior.Restrict);

        // Index for lookups
        _ = builder.HasIndex(h => h.ManufacturerId);
        _ = builder.HasIndex(h => h.Name);
    }
}
