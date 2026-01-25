using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity configuration for AuthAuditLog - security audit logging for authentication events.
/// </summary>
public class AuthAuditLogConfiguration : IEntityTypeConfiguration<AuthAuditLog>
{
    public void Configure(EntityTypeBuilder<AuthAuditLog> builder)
    {
        _ = builder.HasKey(aal => aal.Id);

        // Properties
        _ = builder.Property(aal => aal.EventType).IsRequired();
        _ = builder.Property(aal => aal.Timestamp).IsRequired();
        _ = builder.Property(aal => aal.IpAddress).HasMaxLength(45);
        _ = builder.Property(aal => aal.UserAgent).HasMaxLength(512);
        _ = builder.Property(aal => aal.FailureReason).HasMaxLength(512);
        _ = builder.Property(aal => aal.Metadata).HasColumnType("TEXT");
        _ = builder.Property(aal => aal.CorrelationId).HasMaxLength(64);

        // Foreign Key (nullable - for failed logins where user doesn't exist)
        _ = builder.HasOne(aal => aal.User)
            .WithMany()
            .HasForeignKey(aal => aal.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Indexes for common queries
        _ = builder.HasIndex(aal => aal.UserId);
        _ = builder.HasIndex(aal => aal.EventType);
        _ = builder.HasIndex(aal => aal.Timestamp);
        _ = builder.HasIndex(aal => aal.Success);
        _ = builder.HasIndex(aal => new { aal.UserId, aal.Timestamp }); // Common query pattern
    }
}
