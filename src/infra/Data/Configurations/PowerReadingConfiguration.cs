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

        // Composite index for the aggregation window query in
        // PowerMonitorPollingService.SetKwhUsedAsync, which filters on both
        // PowerMonitorId and RecordedAt — the single-column RecordedAt index below
        // cannot seek on that combined filter.
        builder.HasIndex(r => new { r.PowerMonitorId, r.RecordedAt });

        // Index for efficient time-range pruning (PowerReadingPruneService).
        builder.HasIndex(r => r.RecordedAt);
    }
}
