using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class DismissedPrinterCameraConfiguration : IEntityTypeConfiguration<DismissedPrinterCamera>
{
    public void Configure(EntityTypeBuilder<DismissedPrinterCamera> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("DismissedPrinterCameras");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.Source)
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(d => d.Name)
            .IsRequired()
            .HasMaxLength(100);

        builder.HasOne<Printer>()
            .WithMany()
            .HasForeignKey(d => d.PrinterId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(d => new { d.PrinterId, d.Source }).IsUnique();
    }
}
