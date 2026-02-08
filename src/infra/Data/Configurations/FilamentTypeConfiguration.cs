using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class FilamentTypeConfiguration : IEntityTypeConfiguration<FilamentType>
{
    public void Configure(EntityTypeBuilder<FilamentType> builder)
    {
        _ = builder.HasKey(f => f.Id);
        _ = builder.Property(f => f.Name).IsRequired().HasMaxLength(64);
        _ = builder.HasIndex(f => f.Name).IsUnique();
        _ = builder.Property(f => f.DefaultHotendTemp);
        _ = builder.Property(f => f.DefaultBedTemp);
        _ = builder.Property(f => f.CreatedAt).IsRequired();
    }
}
