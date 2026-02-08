using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity configuration for RevokedToken - tracks revoked JWT tokens for security.
/// </summary>
public class RevokedTokenConfiguration : IEntityTypeConfiguration<RevokedToken>
{
    public void Configure(EntityTypeBuilder<RevokedToken> builder)
    {
        _ = builder.HasKey(rt => rt.Id);

        // Properties
        _ = builder.Property(rt => rt.TokenHash).IsRequired().HasMaxLength(64); // SHA256 hash = 64 hex chars
        _ = builder.Property(rt => rt.Reason).IsRequired().HasMaxLength(512);
        _ = builder.Property(rt => rt.IpAddress).HasMaxLength(45);

        // Foreign Keys
        _ = builder.HasOne(rt => rt.User)
            .WithMany()
            .HasForeignKey(rt => rt.UserId)
            .OnDelete(DeleteBehavior.NoAction); // Changed from Cascade to NoAction to prevent multiple cascade paths in SQL Server

        _ = builder.HasOne(rt => rt.RevokedByUser)
            .WithMany()
            .HasForeignKey(rt => rt.RevokedByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        // Indexes for fast token lookup and cleanup
        _ = builder.HasIndex(rt => rt.TokenHash).IsUnique(); // Fast revocation check
        _ = builder.HasIndex(rt => rt.UserId); // Get all revoked tokens for a user
        _ = builder.HasIndex(rt => rt.ExpiresAt); // Cleanup expired revocations
        _ = builder.HasIndex(rt => rt.RevokedAt); // Audit queries
    }
}
