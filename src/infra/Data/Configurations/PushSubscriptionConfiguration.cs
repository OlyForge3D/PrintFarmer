using Farm.Infrastructure.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class PushSubscriptionConfiguration : IEntityTypeConfiguration<PushSubscription>
{
    public void Configure(EntityTypeBuilder<PushSubscription> builder)
    {
        _ = builder.HasKey(ps => ps.Id);
        _ = builder.Property(ps => ps.UserId).IsRequired();
        _ = builder.Property(ps => ps.Endpoint).IsRequired().HasMaxLength(2048);
        _ = builder.Property(ps => ps.P256dh).IsRequired().HasMaxLength(512);
        _ = builder.Property(ps => ps.Auth).IsRequired().HasMaxLength(512);

        _ = builder.HasOne(ps => ps.User)
            .WithMany()
            .HasForeignKey(ps => ps.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        _ = builder.HasIndex(ps => new { ps.UserId, ps.Endpoint }).IsUnique();
    }
}
