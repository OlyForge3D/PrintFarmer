using Farm.Infrastructure.Domain.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity configuration for <see cref="LibrarySyncChange"/> — the append-only library sync
/// journal (#844). <see cref="LibrarySyncChange.Revision"/> is the primary key and is
/// store-generated so it is monotonic across PostgreSQL, SQL Server, and SQLite. Enum
/// columns are persisted as strings to keep the sync contract stable and readable.
/// </summary>
public class LibrarySyncChangeConfiguration : IEntityTypeConfiguration<LibrarySyncChange>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<LibrarySyncChange> builder)
    {
        _ = builder.ToTable("LibrarySyncChanges");

        // Revision is the monotonic, store-generated cursor and the primary key.
        _ = builder.HasKey(c => c.Revision);
        _ = builder.Property(c => c.Revision).ValueGeneratedOnAdd();

        _ = builder.Property(c => c.EntityType).IsRequired().HasConversion<string>().HasMaxLength(64);
        _ = builder.Property(c => c.EntityId).IsRequired();
        _ = builder.Property(c => c.Operation).IsRequired().HasConversion<string>().HasMaxLength(32);
        _ = builder.Property(c => c.OwnerUserId);
        _ = builder.Property(c => c.Visibility).IsRequired().HasConversion<string>().HasMaxLength(32);
        _ = builder.Property(c => c.ActorUserId).IsRequired();
        _ = builder.Property(c => c.Timestamp).IsRequired();

        // Locate all changes for one entity (e.g. to build a tombstone or history view).
        _ = builder.HasIndex(c => new { c.EntityType, c.EntityId });

        // Owner-scoped incremental pulls (#845).
        _ = builder.HasIndex(c => c.OwnerUserId);

        // Time-based diagnostics / auditing.
        _ = builder.HasIndex(c => c.Timestamp);
    }
}
