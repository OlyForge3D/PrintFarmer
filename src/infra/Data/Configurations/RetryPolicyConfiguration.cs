using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity configuration for RetryPolicy (Phase 4.4 retry settings).
/// </summary>
public class RetryPolicyConfiguration : IEntityTypeConfiguration<RetryPolicy>
{
    public void Configure(EntityTypeBuilder<RetryPolicy> builder)
    {
        _ = builder.HasKey(r => r.Id);
        _ = builder.Property(r => r.IsEnabled).HasDefaultValue(true);
        _ = builder.Property(r => r.MaxRetries).HasDefaultValue(3);
        _ = builder.Property(r => r.InitialDelaySeconds).HasDefaultValue(60);
        _ = builder.Property(r => r.ExponentialBase).HasDefaultValue(2.0);
        _ = builder.Property(r => r.MaxDelaySeconds).HasDefaultValue(3600);
        _ = builder.Property(r => r.RetryOnErrorCategories).HasMaxLength(100).HasDefaultValue("Recoverable");
        _ = builder.Property(r => r.CreatedAt).IsRequired();
        _ = builder.Property(r => r.UpdatedAt).IsRequired();

        // No indexes needed - typically only one global retry policy, accessed infrequently
    }
}
