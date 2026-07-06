using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity configuration for optional barcode scan diagnostics.
/// </summary>
public class BarcodeScanLogConfiguration : IEntityTypeConfiguration<BarcodeScanLog>
{
    public void Configure(EntityTypeBuilder<BarcodeScanLog> builder)
    {
        _ = builder.HasKey(l => l.Id);
        _ = builder.Property(l => l.Timestamp).IsRequired();
        _ = builder.Property(l => l.Barcode).IsRequired().HasMaxLength(256);
        _ = builder.Property(l => l.Action).HasConversion<string>().HasMaxLength(32);
        _ = builder.Property(l => l.Outcome).HasConversion<string>().HasMaxLength(32);
        _ = builder.Property(l => l.HttpStatus).IsRequired();
        _ = builder.Property(l => l.UserId).HasMaxLength(450);
        _ = builder.Property(l => l.Message).HasMaxLength(1024);

        _ = builder.HasIndex(l => l.Timestamp);
        _ = builder.HasIndex(l => l.Barcode);
        _ = builder.HasIndex(l => l.Action);
        _ = builder.HasIndex(l => l.Outcome);
    }
}
