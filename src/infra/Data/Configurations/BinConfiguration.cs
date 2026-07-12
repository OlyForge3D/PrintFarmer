using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>Entity configuration for printed-part storage bins.</summary>
public class BinConfiguration : IEntityTypeConfiguration<Bin>
{
    public void Configure(EntityTypeBuilder<Bin> builder)
    {
        _ = builder.ToTable(table =>
            table.HasCheckConstraint("CK_Bins_Code_Normalized", "\"Code\" = UPPER(\"Code\")"));
        _ = builder.HasKey(b => b.Id);
        _ = builder.Property(b => b.Code).IsRequired().HasMaxLength(128);
        _ = builder.Property(b => b.Name).IsRequired().HasMaxLength(200);
        _ = builder.Property(b => b.Location).HasMaxLength(200);
        _ = builder.Property(b => b.Notes).HasMaxLength(1000);
        _ = builder.Property(b => b.IsActive).IsRequired();
        _ = builder.Property(b => b.CreatedAt).IsRequired();
        _ = builder.Property(b => b.UpdatedAt).IsRequired();

        _ = builder.HasIndex(b => b.Code).IsUnique();
        _ = builder.HasIndex(b => b.IsActive);
    }
}
