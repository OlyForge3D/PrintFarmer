using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class ManufacturerConfiguration : IEntityTypeConfiguration<Manufacturer>
{
    public void Configure(EntityTypeBuilder<Manufacturer> builder)
    {
        _ = builder.HasKey(m => m.Id);
        _ = builder.Property(m => m.Name).IsRequired().HasMaxLength(128);

        // Persisted shadow column for cross-provider case-insensitive uniqueness.
        // Populated in SaveChanges overrides (lower-invariant).
        _ = builder.Property<string>("NameLowered")
            .HasColumnName("NameLowered")
            .HasMaxLength(128)
            .IsRequired();
        _ = builder.HasIndex("NameLowered").IsUnique();
    }
}
