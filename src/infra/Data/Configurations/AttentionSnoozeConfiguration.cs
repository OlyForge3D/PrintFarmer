using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// EF configuration for <see cref="AttentionSnooze"/>. Enforces per-user uniqueness of the
/// computed attention item id and provides an index on the expiry column for cleanup.
/// </summary>
public sealed class AttentionSnoozeConfiguration : IEntityTypeConfiguration<AttentionSnooze>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AttentionSnooze> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        _ = builder.HasKey(s => s.Id);

        _ = builder.Property(s => s.UserId).IsRequired();

        _ = builder.Property(s => s.AttentionItemId)
            .HasMaxLength(128)
            .IsRequired();

        _ = builder.Property(s => s.SnoozedUntilUtc).IsRequired();
        _ = builder.Property(s => s.CreatedAtUtc).IsRequired();
        _ = builder.Property(s => s.AttentionItemAnchorAtUtc).IsRequired(false);

        // Per-user uniqueness of the item id so upserts collapse cleanly.
        _ = builder.HasIndex(s => new { s.UserId, s.AttentionItemId })
            .IsUnique()
            .HasDatabaseName("IX_AttentionSnoozes_UserId_AttentionItemId");

        // Expiry-column index for background cleanup jobs and feed queries.
        _ = builder.HasIndex(s => s.SnoozedUntilUtc)
            .HasDatabaseName("IX_AttentionSnoozes_SnoozedUntilUtc");
    }
}
