using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKey>
{
    public void Configure(EntityTypeBuilder<ApiKey> builder)
    {
        _ = builder.HasKey(a => a.Id);
        _ = builder.Property(a => a.UserId).IsRequired(false); // Nullable for global keys
        _ = builder.Property(a => a.Name).IsRequired().HasMaxLength(256);
        _ = builder.Property(a => a.KeyHash).IsRequired().HasMaxLength(64); // SHA256 hex = 64 chars
        _ = builder.Property(a => a.IsActive).IsRequired().HasDefaultValue(true);
        _ = builder.Property(a => a.CreatedAt).IsRequired();
        _ = builder.Property(a => a.ExpiresAt).IsRequired(false);

        // Indexes for efficient querying
        _ = builder.HasIndex(a => a.KeyHash).IsUnique(); // Fast lookup by hash
        _ = builder.HasIndex(a => a.UserId); // Find user's keys
        _ = builder.HasIndex(a => new { a.UserId, a.IsActive }); // Active keys for user
    }
}
