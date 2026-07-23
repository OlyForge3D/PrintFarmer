using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity configuration for LoginAuditEntry — dedicated table for login attempt auditing.
/// Separate from AuthAuditLog to provide a focused, admin-queryable security view.
/// </summary>
public class LoginAuditEntryConfiguration : IEntityTypeConfiguration<LoginAuditEntry>
{
    public void Configure(EntityTypeBuilder<LoginAuditEntry> builder)
    {
        _ = builder.HasKey(e => e.Id);

        _ = builder.Property(e => e.Timestamp).IsRequired();

        // Username is attacker-supplied — truncate at 256, never FK-linked
        _ = builder.Property(e => e.Username).HasMaxLength(256);
        _ = builder.Property(e => e.IpAddress).IsRequired().HasMaxLength(64); // IPv6 max is 45, give headroom
        _ = builder.Property(e => e.UserAgent).HasMaxLength(512);
        _ = builder.Property(e => e.FailureReason).HasMaxLength(64);

        // Primary access patterns: latest-first paged browse and per-username lookup
        _ = builder.HasIndex(e => e.Timestamp);
        _ = builder.HasIndex(e => e.Username);
        _ = builder.HasIndex(e => e.Success);
    }
}
