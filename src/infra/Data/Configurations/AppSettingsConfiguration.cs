using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity configuration for AppSettingsEntity (application-wide key-value settings).
/// </summary>
public class AppSettingsConfiguration : IEntityTypeConfiguration<AppSettingsEntity>
{
    public void Configure(EntityTypeBuilder<AppSettingsEntity> builder)
    {
        _ = builder.HasKey(a => a.Id);
        _ = builder.Property(a => a.Key).IsRequired().HasMaxLength(128);
        _ = builder.Property(a => a.SettingsJson).IsRequired().HasColumnType("TEXT");
        _ = builder.Property(a => a.UpdatedAt).IsRequired();
        _ = builder.HasIndex(a => a.Key).IsUnique();
        _ = builder.Property(a => a.RowVersion).IsConcurrencyToken();
    }
}
