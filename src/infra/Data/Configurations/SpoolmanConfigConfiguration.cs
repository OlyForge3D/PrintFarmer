using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class SpoolmanConfigConfiguration : IEntityTypeConfiguration<SpoolmanConfig>
{
    public void Configure(EntityTypeBuilder<SpoolmanConfig> builder)
    {
        _ = builder.HasKey(c => c.Id);
        _ = builder.Property(c => c.BaseUrl).IsRequired().HasMaxLength(256);
    }
}
