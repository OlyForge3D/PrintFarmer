using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class PrinterModelAliasConfiguration : IEntityTypeConfiguration<PrinterModelAlias>
{
    public void Configure(EntityTypeBuilder<PrinterModelAlias> builder)
    {
        _ = builder.HasKey(a => a.Id);
        _ = builder.Property(a => a.SlicerModelName).IsRequired().HasMaxLength(256);
        _ = builder.Property(a => a.SlicerModelNameNormalized).IsRequired().HasMaxLength(256);
        _ = builder.Property(a => a.SlicerType).HasMaxLength(128);
        _ = builder.Property(a => a.SlicerTypeNormalized).HasMaxLength(128);
        _ = builder.Property(a => a.CreatedAt).IsRequired();
        _ = builder.HasOne(a => a.PrinterModel)
            .WithMany(m => m.Aliases)
            .HasForeignKey(a => a.PrinterModelId)
            .OnDelete(DeleteBehavior.Cascade);

        // Unique constraint: SlicerModelName + SlicerType (NULL safe)
        _ = builder.HasIndex(a => new { a.PrinterModelId, a.SlicerModelName, a.SlicerType }).IsUnique();
        _ = builder
            .HasIndex(a => new { a.SlicerModelNameNormalized, a.SlicerTypeNormalized })
            .HasDatabaseName("IX_PrinterModelAliases_NormalizedLookup");
    }
}
