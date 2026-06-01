using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class PowerReadingConfiguration : IEntityTypeConfiguration<PowerReading>
{
    public void Configure(EntityTypeBuilder<PowerReading> builder)
    {
        builder.HasKey(r => r.Id);

        builder.Property(r => r.WattsNow).HasPrecision(10, 2);
        builder.Property(r => r.KwhTotal).HasPrecision(14, 4);

        // Index for efficient time-range queries and pruning
        builder.HasIndex(r => r.RecordedAt);
    }
}
