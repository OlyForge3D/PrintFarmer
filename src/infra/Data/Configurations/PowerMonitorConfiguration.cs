using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class PowerMonitorConfiguration : IEntityTypeConfiguration<PowerMonitor>
{
    public void Configure(EntityTypeBuilder<PowerMonitor> builder)
    {
        builder.HasKey(p => p.Id);

        builder.Property(p => p.ProviderType).IsRequired().HasMaxLength(64);
        builder.Property(p => p.DeviceAddress).IsRequired().HasMaxLength(256);
        builder.Property(p => p.ElectricityRateUsdPerKwh).HasPrecision(10, 4);

        builder.HasOne(p => p.Printer)
            .WithMany()
            .HasForeignKey(p => p.PrinterId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(p => p.Readings)
            .WithOne(r => r.PowerMonitor)
            .HasForeignKey(r => r.PowerMonitorId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
