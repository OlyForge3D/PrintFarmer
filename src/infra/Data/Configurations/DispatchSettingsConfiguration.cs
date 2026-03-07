using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class DispatchSettingsConfiguration : IEntityTypeConfiguration<DispatchSettings>
{
    public void Configure(EntityTypeBuilder<DispatchSettings> builder)
    {
        _ = builder.HasKey(d => d.Id);

        _ = builder.Property(d => d.AutoDispatchMode)
            .HasConversion<string>()
            .HasMaxLength(20);

        _ = builder.Property(d => d.LoadBalancingStrategy)
            .HasConversion<string>()
            .HasMaxLength(20);

        // Seed singleton row with safe defaults (auto-dispatch OFF)
        _ = builder.HasData(new DispatchSettings
        {
            Id = 1,
            AutoDispatchEnabled = false,
            AutoDispatchMode = Services.Queue.Dispatch.AutoDispatchMode.Manual,
            IdleThresholdSeconds = 30,
            MinimumScoreThreshold = 0.5,
            MaxConcurrentDispatches = 3,
            LoadBalancingStrategy = Services.Queue.Dispatch.LoadBalancingStrategy.BestFit,
            UpdatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
    }
}
