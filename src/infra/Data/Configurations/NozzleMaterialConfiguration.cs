using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class NozzleMaterialConfiguration : IEntityTypeConfiguration<NozzleMaterial>
{
    public void Configure(EntityTypeBuilder<NozzleMaterial> builder)
    {
        _ = builder.HasKey(m => m.Id);
        _ = builder.Property(m => m.Name).IsRequired().HasMaxLength(128);
        _ = builder.Property(m => m.Description).HasMaxLength(512);
        _ = builder.Property(m => m.DefaultMaxTemp).HasDefaultValue(500);
        _ = builder.Property(m => m.IsHardened).HasDefaultValue(false);
        _ = builder.Property(m => m.IsBuiltIn).HasDefaultValue(false);

        _ = builder.HasIndex(m => m.Name).IsUnique();
    }
}
