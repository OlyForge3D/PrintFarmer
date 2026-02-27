using Farm.Infrastructure.Domain.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class WebhookDeliveryLogConfiguration : IEntityTypeConfiguration<WebhookDeliveryLog>
{
    public void Configure(EntityTypeBuilder<WebhookDeliveryLog> builder)
    {
        _ = builder.HasKey(d => d.Id);
        _ = builder.Property(d => d.EventType).IsRequired().HasMaxLength(64);
        _ = builder.Property(d => d.Payload).IsRequired().HasColumnType("TEXT");
        _ = builder.Property(d => d.ErrorMessage).HasMaxLength(1024);
        _ = builder.Property(d => d.CreatedAt).IsRequired();

        _ = builder.HasOne(d => d.Subscription)
            .WithMany()
            .HasForeignKey(d => d.WebhookSubscriptionId)
            .OnDelete(DeleteBehavior.Cascade);

        _ = builder.HasIndex(d => d.WebhookSubscriptionId);
        _ = builder.HasIndex(d => d.EventType);
        _ = builder.HasIndex(d => d.CreatedAt).IsDescending();
        _ = builder.HasIndex(d => d.Success);
    }
}
