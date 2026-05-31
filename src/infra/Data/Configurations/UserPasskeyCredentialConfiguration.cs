using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity configuration for <see cref="UserPasskeyCredential"/> (WebAuthn/FIDO2 registered keys).
/// </summary>
public class UserPasskeyCredentialConfiguration : IEntityTypeConfiguration<UserPasskeyCredential>
{
    public void Configure(EntityTypeBuilder<UserPasskeyCredential> builder)
    {
        _ = builder.HasKey(c => c.Id);

        _ = builder.Property(c => c.CredentialId).IsRequired();
        _ = builder.Property(c => c.PublicKey).IsRequired();
        _ = builder.Property(c => c.DeviceName).HasMaxLength(200);
        _ = builder.Property(c => c.AaguidDescription).HasMaxLength(300);

        _ = builder.HasOne(c => c.User)
            .WithMany()
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Unique index: a credential ID must not be registered to more than one user account.
        _ = builder.HasIndex(c => c.CredentialId).IsUnique();

        // Index for per-user queries (list credentials, validate ownership during assertion).
        _ = builder.HasIndex(c => c.UserId);
    }
}
