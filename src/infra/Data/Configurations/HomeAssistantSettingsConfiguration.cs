using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class HomeAssistantSettingsConfiguration : IEntityTypeConfiguration<HomeAssistantSettings>
{
    public void Configure(EntityTypeBuilder<HomeAssistantSettings> builder)
    {
        _ = builder.HasKey(h => h.Id);

        _ = builder.Property(h => h.BaseUrl)
            .HasMaxLength(500);

        _ = builder.Property(h => h.LongLivedAccessToken)
            .HasMaxLength(2000);

        // Seed singleton row — disabled by default so existing installs are unaffected.
        _ = builder.HasData(new HomeAssistantSettings
        {
            Id = 1,
            Enabled = false,
            BaseUrl = null,
            LongLivedAccessToken = null,
            CreatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
    }
}
