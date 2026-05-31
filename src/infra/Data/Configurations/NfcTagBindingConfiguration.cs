using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class NfcTagBindingConfiguration : IEntityTypeConfiguration<NfcTagBinding>
{
    public void Configure(EntityTypeBuilder<NfcTagBinding> builder)
    {
        builder.HasKey(b => b.Id);

        builder.Property(b => b.TagUid)
            .IsRequired()
            .HasMaxLength(64);

        builder.HasIndex(b => b.TagUid)
            .IsUnique();

        builder.Property(b => b.SpoolName)
            .HasMaxLength(256);

        builder.Property(b => b.TrayId)
            .HasMaxLength(64);

        builder.HasOne(b => b.Printer)
            .WithMany()
            .HasForeignKey(b => b.PrinterId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(b => b.PrinterId);
        builder.HasIndex(b => b.SpoolId);
    }
}
