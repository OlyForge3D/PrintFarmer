using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Normalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity configuration for remembered non-GTIN scan codes.
/// </summary>
public class ScanCodeAliasConfiguration : IEntityTypeConfiguration<ScanCodeAlias>
{
    public void Configure(EntityTypeBuilder<ScanCodeAlias> builder)
    {
        _ = builder.HasKey(a => a.Id);
        _ = builder.Property(a => a.Code).IsRequired().HasMaxLength(ScanCodeAliasValidator.MaxLength);
        _ = builder.Property(a => a.FilamentId).IsRequired();
        _ = builder.Property(a => a.CreatedAt).IsRequired();
        _ = builder.Property(a => a.UpdatedAt).IsRequired();
        _ = builder.Property(a => a.CreatedByUserId).HasMaxLength(450);

        _ = builder.HasIndex(a => a.Code).IsUnique();
        _ = builder.HasIndex(a => a.FilamentId);
    }
}
