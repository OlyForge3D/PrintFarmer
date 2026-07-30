using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// EF configuration for <see cref="FilamentSwapOverride"/>. Indexes the printer + timestamp
/// columns for forensic queries over a printer's override history.
/// </summary>
public sealed class FilamentSwapOverrideConfiguration : IEntityTypeConfiguration<FilamentSwapOverride>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<FilamentSwapOverride> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        _ = builder.HasKey(o => o.Id);

        _ = builder.Property(o => o.PrinterId).IsRequired();
        _ = builder.Property(o => o.ToolheadIndex).IsRequired();
        _ = builder.Property(o => o.SpoolId).IsRequired();

        _ = builder.Property(o => o.UserId).HasMaxLength(256);
        _ = builder.Property(o => o.UserName).HasMaxLength(256);

        _ = builder.Property(o => o.Reason)
            .HasMaxLength(500)
            .IsRequired();

        _ = builder.Property(o => o.ExpectedMaterial).HasMaxLength(128);
        _ = builder.Property(o => o.ScannedMaterial).HasMaxLength(128);

        _ = builder.Property(o => o.AffectedJobIdsJson).IsRequired();

        _ = builder.Property(o => o.CreatedAtUtc).IsRequired();

        _ = builder.HasIndex(o => new { o.PrinterId, o.CreatedAtUtc })
            .HasDatabaseName("IX_FilamentSwapOverrides_PrinterId_CreatedAtUtc");
    }
}
