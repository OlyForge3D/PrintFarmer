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

        // Purpose/Scopes default to OctoPrint/None so existing rows never gain desktop
        // access implicitly when these columns are introduced (see issue #837/#839).
        _ = builder.Property(a => a.Purpose).IsRequired().HasConversion<int>().HasDefaultValue(ApiKeyPurpose.OctoPrint);
        _ = builder.Property(a => a.Scopes).IsRequired().HasConversion<int>().HasDefaultValue(ApiKeyScope.None);

        // Indexes for efficient querying
        _ = builder.HasIndex(a => a.KeyHash).IsUnique(); // Fast lookup by hash
        _ = builder.HasIndex(a => a.UserId); // Find user's keys
        _ = builder.HasIndex(a => new { a.UserId, a.IsActive }); // Active keys for user
    }
}
