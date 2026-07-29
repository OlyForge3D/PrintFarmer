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

        _ = builder.Property(t => t.RegistrationVersion)
            .IsRequired()
            .HasDefaultValue(0L)
            .IsConcurrencyToken();

        _ = builder.Property(t => t.InstallationId)
            .HasMaxLength(NativePushRegistrationContract.InstallationIdMaxLength)
            .IsRequired();

        _ = builder.Property(t => t.Token)
            .HasMaxLength(NativePushRegistrationContract.TokenMaxLength)
            .IsRequired();

        _ = builder.Property(t => t.Platform)
            .HasMaxLength(NativePushRegistrationContract.PlatformMaxLength)
            .IsRequired();

        _ = builder.Property(t => t.Environment)
            .HasMaxLength(NativePushRegistrationContract.EnvironmentMaxLength)
            .IsRequired();

        _ = builder.Property(t => t.AppBundleId)
            .HasMaxLength(NativePushRegistrationContract.AppBundleIdMaxLength);

        _ = builder.Property(t => t.CreatedAt).IsRequired();
        _ = builder.Property(t => t.LastUsedAt);
        _ = builder.Property(t => t.LastFailureAt);
        _ = builder.Property(t => t.ConsecutiveFailureCount).IsRequired().HasDefaultValue(0);
        _ = builder.Property(t => t.IsActive).IsRequired().HasDefaultValue(true);

        _ = builder.HasOne(t => t.User)
            .WithMany()
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Installation ownership is global: one physical app installation can
        // belong to exactly one authenticated user at a time.
        _ = builder.HasIndex(t => t.InstallationId)
            .IsUnique()
            .HasDatabaseName("IX_DeviceTokens_InstallationId");

        // Non-unique provider-token lookup for diagnostics; invalidation uses the row Id.
        _ = builder.HasIndex(t => t.Token)
            .HasDatabaseName("IX_DeviceTokens_Token");
    }
}
