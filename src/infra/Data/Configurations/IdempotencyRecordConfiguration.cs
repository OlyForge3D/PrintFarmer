using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// EF Core configuration for <see cref="IdempotencyRecord"/>.
/// The composite unique index on <c>(UserId, RouteKey, IdempotencyKey)</c> is the
/// concurrency backbone: two racing first-requests both attempt to insert the
/// same key, exactly one wins, and the loser observes a unique-violation and
/// falls through to the replay path.
/// </summary>
public class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        _ = builder.HasKey(r => r.Id);

        _ = builder.Property(r => r.UserId).HasMaxLength(450).IsRequired();
        _ = builder.Property(r => r.RouteKey).HasMaxLength(200).IsRequired();
        _ = builder.Property(r => r.IdempotencyKey).HasMaxLength(200).IsRequired();
        _ = builder.Property(r => r.RequestHash).HasMaxLength(64).IsRequired();
        _ = builder.Property(r => r.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();
        _ = builder.Property(r => r.ResponseContentType).HasMaxLength(200);
        _ = builder.Property(r => r.CreatedAt).IsRequired();
        _ = builder.Property(r => r.UpdatedAt).IsRequired();

        // Concurrency backbone: exactly one row per user/route/key.
        _ = builder.HasIndex(r => new { r.UserId, r.RouteKey, r.IdempotencyKey })
            .IsUnique()
            .HasDatabaseName("IX_IdempotencyRecords_User_Route_Key");

        // Retention scan: pruned by createdAt only.
        _ = builder.HasIndex(r => r.CreatedAt)
            .HasDatabaseName("IX_IdempotencyRecords_CreatedAt");
    }
}
