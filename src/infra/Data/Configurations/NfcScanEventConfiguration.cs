using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class NfcScanEventConfiguration : IEntityTypeConfiguration<NfcScanEvent>
{
    public void Configure(EntityTypeBuilder<NfcScanEvent> builder)
    {
        builder.HasKey(s => s.Id);

        builder.Property(s => s.TagFormat)
            .IsRequired()
            .HasMaxLength(32);

        builder.Property(s => s.MaterialType)
            .HasMaxLength(64);

        builder.Property(s => s.BrandName)
            .HasMaxLength(128);

        builder.Property(s => s.Action)
            .HasMaxLength(64);

        builder.HasIndex(s => s.ScannedAt);
        builder.HasIndex(s => s.NfcDeviceId);
    }
}
