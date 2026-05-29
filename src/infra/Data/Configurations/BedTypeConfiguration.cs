using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class BedTypeConfiguration : IEntityTypeConfiguration<BedType>
{
    public void Configure(EntityTypeBuilder<BedType> builder)
    {
        _ = builder.HasKey(b => b.Id);

        _ = builder.Property(b => b.Name)
            .IsRequired()
            .HasMaxLength(200);

        _ = builder.Property(b => b.Description)
            .HasMaxLength(1000);

        _ = builder.Property(b => b.Color)
            .HasMaxLength(9);

        _ = builder.HasIndex(b => b.Name).IsUnique();

        _ = builder.HasMany(b => b.Printers)
            .WithOne(p => p.BedType)
            .HasForeignKey(p => p.BedTypeId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
