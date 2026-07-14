using Farm.Infrastructure.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// EF configuration for <see cref="DeviceToken"/>. See <c>docs/OPERATOR_NATIVE_PUSH.md</c>.
/// </summary>
public sealed class DeviceTokenConfiguration : IEntityTypeConfiguration<DeviceToken>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<DeviceToken> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        _ = builder.HasKey(t => t.Id);

        _ = builder.Property(t => t.UserId).IsRequired();

        _ = builder.Property(t => t.InstallationId)
            .HasMaxLength(128)
            .IsRequired();

        _ = builder.Property(t => t.Token)
            .HasMaxLength(4096)
            .IsRequired();

        _ = builder.Property(t => t.Platform)
            .HasMaxLength(16)
            .IsRequired();

        _ = builder.Property(t => t.Environment)
            .HasMaxLength(16)
            .IsRequired();

        _ = builder.Property(t => t.AppBundleId)
            .HasMaxLength(256);

        _ = builder.Property(t => t.CreatedAt).IsRequired();
        _ = builder.Property(t => t.LastUsedAt);
        _ = builder.Property(t => t.LastFailureAt);
        _ = builder.Property(t => t.ConsecutiveFailureCount).IsRequired().HasDefaultValue(0);
        _ = builder.Property(t => t.IsActive).IsRequired().HasDefaultValue(true);

        _ = builder.HasOne(t => t.User)
            .WithMany()
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Upsert key: one active token row per installation per user.
        _ = builder.HasIndex(t => new { t.UserId, t.InstallationId })
            .IsUnique()
            .HasDatabaseName("IX_DeviceTokens_UserId_InstallationId");

        // Reverse lookup for inbound 410 Gone invalidations.
        _ = builder.HasIndex(t => t.Token)
            .HasDatabaseName("IX_DeviceTokens_Token");
    }
}
