using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity configuration for Tag - generic tag for organizing objects (3D models, gcode files, etc.)
/// </summary>
public class TagConfiguration : IEntityTypeConfiguration<Tag>
{
    public void Configure(EntityTypeBuilder<Tag> builder)
    {
        _ = builder.HasKey(t => t.Id);

        // Properties
        _ = builder.Property(t => t.Name).IsRequired().HasMaxLength(128);
        _ = builder.Property(t => t.Color).HasMaxLength(7); // Hex color codes
        _ = builder.Property(t => t.Description).HasMaxLength(512);

        // Sync revision / optimistic-concurrency metadata (#844). ConcurrencyToken is an EF
        // concurrency token so conflicting writes are detected; both are additive.
        _ = builder.Property(t => t.Revision).IsRequired().HasDefaultValue(0L);
        _ = builder.Property(t => t.ConcurrencyToken).IsConcurrencyToken();

        // Index for quick tag lookups
        _ = builder.HasIndex(t => t.Name).IsUnique();

        // Index for analytics
        _ = builder.HasIndex(t => t.CreatedAt);
    }
}
