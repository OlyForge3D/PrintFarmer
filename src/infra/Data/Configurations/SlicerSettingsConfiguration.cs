using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class SlicerSettingsConfiguration : IEntityTypeConfiguration<SlicerSettings>
{
    public void Configure(EntityTypeBuilder<SlicerSettings> builder)
    {
        _ = builder.HasKey(s => s.Id);
        _ = builder.Property(s => s.Enabled).IsRequired();
        _ = builder.Property(s => s.PerEngineJson).HasColumnType("TEXT");
        _ = builder.Property(s => s.UpdatedAt).IsRequired();
        _ = builder.Property(s => s.JitterPercent).HasDefaultValue(15.0).IsRequired();
    }
}
