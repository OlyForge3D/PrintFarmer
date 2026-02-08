using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class NozzleModelDefinitionConfiguration : IEntityTypeConfiguration<NozzleModelDefinition>
{
    public void Configure(EntityTypeBuilder<NozzleModelDefinition> builder)
    {
        _ = builder.HasKey(n => n.Id);
        _ = builder.Property(n => n.Name).IsRequired().HasMaxLength(128);
        _ = builder.Property(n => n.Description).HasMaxLength(512);
        _ = builder.Property(n => n.MaxTemp).HasDefaultValue(500);

        // IsHardened is a computed property marked [NotMapped] - do not configure it here

        // Foreign Key to Manufacturer
        _ = builder.HasOne(n => n.Manufacturer)
            .WithMany()
            .HasForeignKey(n => n.ManufacturerId)
            .OnDelete(DeleteBehavior.Restrict);

        // Index for lookups
        _ = builder.HasIndex(n => n.ManufacturerId);
        _ = builder.HasIndex(n => n.Name);
    }
}
