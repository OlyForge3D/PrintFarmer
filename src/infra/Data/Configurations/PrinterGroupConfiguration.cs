using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class PrinterGroupConfiguration : IEntityTypeConfiguration<PrinterGroup>
{
    public void Configure(EntityTypeBuilder<PrinterGroup> builder)
    {
        _ = builder.HasKey(g => g.Id);

        _ = builder.Property(g => g.Name)
            .IsRequired()
            .HasMaxLength(200);

        _ = builder.Property(g => g.Description)
            .HasMaxLength(1000);

        _ = builder.HasIndex(g => g.Name).IsUnique();

        _ = builder.HasMany(g => g.Printers)
            .WithOne(p => p.PrinterGroup)
            .HasForeignKey(p => p.PrinterGroupId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
