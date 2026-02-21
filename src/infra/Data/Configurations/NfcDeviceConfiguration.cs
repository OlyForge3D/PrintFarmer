using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class NfcDeviceConfiguration : IEntityTypeConfiguration<NfcDevice>
{
    public void Configure(EntityTypeBuilder<NfcDevice> builder)
    {
        builder.HasKey(n => n.Id);

        builder.Property(n => n.Name)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(n => n.IpAddress)
            .HasMaxLength(45);

        builder.Property(n => n.FirmwareVersion)
            .HasMaxLength(32);

        builder.HasOne(n => n.Printer)
            .WithMany()
            .HasForeignKey(n => n.PrinterId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(n => n.PrinterId)
            .IsUnique()
            .HasFilter("\"PrinterId\" IS NOT NULL");

        builder.HasMany(n => n.ScanHistory)
            .WithOne(s => s.NfcDevice)
            .HasForeignKey(s => s.NfcDeviceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
