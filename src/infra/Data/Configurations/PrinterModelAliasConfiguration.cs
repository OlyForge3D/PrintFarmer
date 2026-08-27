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

        // Unique constraint on the normalized columns (not the raw ones): enforcing uniqueness
        // on the raw SlicerModelName/SlicerType columns let case/whitespace-variant aliases
        // coexist even though BuildMatchingAliasesQuery/ResolveModelAliasAsync only ever match
        // on the normalized columns (#2080).
        _ = builder
            .HasIndex(a => new { a.PrinterModelId, a.SlicerModelNameNormalized, a.SlicerTypeNormalized })
            .IsUnique();

        // Non-unique lookup index kept separate (rather than reusing the unique index above):
        // ResolveModelAliasAsync filters only on the normalized name/type, not PrinterModelId, so
        // a composite index with PrinterModelId as the leading column can't serve that lookup.
        _ = builder
            .HasIndex(a => new { a.SlicerModelNameNormalized, a.SlicerTypeNormalized })
            .HasDatabaseName("IX_PrinterModelAliases_NormalizedLookup");
    }
}
