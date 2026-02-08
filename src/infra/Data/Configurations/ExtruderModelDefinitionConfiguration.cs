using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class ExtruderModelDefinitionConfiguration : IEntityTypeConfiguration<ExtruderModelDefinition>
{
    public void Configure(EntityTypeBuilder<ExtruderModelDefinition> builder)
    {
        _ = builder.HasKey(e => e.Id);
        _ = builder.Property(e => e.Name).IsRequired().HasMaxLength(128);
        _ = builder.Property(e => e.Description).HasMaxLength(512);
        _ = builder.Property(e => e.GearRatio).HasMaxLength(32);
        _ = builder.Property(e => e.IsDirectDrive).HasDefaultValue(true);

        // Foreign Key to Manufacturer
        _ = builder.HasOne(e => e.Manufacturer)
            .WithMany()
            .HasForeignKey(e => e.ManufacturerId)
            .OnDelete(DeleteBehavior.Restrict);

        // Index for lookups
        _ = builder.HasIndex(e => e.ManufacturerId);
        _ = builder.HasIndex(e => e.Name);
    }
}
