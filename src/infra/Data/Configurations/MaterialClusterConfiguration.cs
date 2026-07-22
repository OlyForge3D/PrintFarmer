using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class MaterialClusterConfiguration : IEntityTypeConfiguration<MaterialCluster>
{
    public void Configure(EntityTypeBuilder<MaterialCluster> builder)
    {
        _ = builder.HasKey(c => c.Id);
        _ = builder.Property(c => c.Name).IsRequired().HasMaxLength(128);
        _ = builder.HasIndex(c => c.Name).IsUnique();
        _ = builder.Property(c => c.Description).HasMaxLength(512);
        _ = builder.Property(c => c.CreatedAt).IsRequired();
        _ = builder.Property(c => c.UpdatedAt).IsRequired();
    }
}
