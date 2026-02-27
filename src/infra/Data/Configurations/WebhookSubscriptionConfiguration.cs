using Farm.Infrastructure.Domain.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class WebhookSubscriptionConfiguration : IEntityTypeConfiguration<WebhookSubscription>
{
    public void Configure(EntityTypeBuilder<WebhookSubscription> builder)
    {
        _ = builder.HasKey(w => w.Id);
        _ = builder.Property(w => w.Name).IsRequired().HasMaxLength(128);
        _ = builder.Property(w => w.Url).IsRequired().HasMaxLength(2048);
        _ = builder.Property(w => w.Secret).HasMaxLength(256);
        _ = builder.Property(w => w.EventTypes).IsRequired().HasMaxLength(1024);
        _ = builder.Property(w => w.IsActive).IsRequired().HasDefaultValue(true);
        _ = builder.Property(w => w.ConsecutiveFailures).IsRequired().HasDefaultValue(0);
        _ = builder.Property(w => w.MaxConsecutiveFailures).IsRequired().HasDefaultValue(10);
        _ = builder.Property(w => w.CreatedAt).IsRequired();

        _ = builder.HasIndex(w => w.IsActive);
        _ = builder.HasIndex(w => w.CreatedAt).IsDescending();
    }
}
