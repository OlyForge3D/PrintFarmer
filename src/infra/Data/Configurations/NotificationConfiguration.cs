using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        _ = builder.HasKey(n => n.Id);
        _ = builder.Property(n => n.UserId).IsRequired();
        _ = builder.Property(n => n.Type).IsRequired();
        _ = builder.Property(n => n.Subject).IsRequired().HasMaxLength(255);
        _ = builder.Property(n => n.Body).IsRequired().HasColumnType("TEXT");
        _ = builder.Property(n => n.IsRead).IsRequired().HasDefaultValue(false);
        _ = builder.Property(n => n.CreatedAt).IsRequired();

        // Foreign Keys
        _ = builder.HasOne(n => n.User)
            .WithMany()
            .HasForeignKey(n => n.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        _ = builder.HasOne(n => n.Job)
            .WithMany()
            .HasForeignKey(n => n.JobId)
            .OnDelete(DeleteBehavior.SetNull);

        // Indexes for efficient querying
        _ = builder.HasIndex(n => n.UserId);
        _ = builder.HasIndex(n => new { n.UserId, n.IsRead });
        _ = builder.HasIndex(n => n.Type);
        _ = builder.HasIndex(n => n.JobId);
        _ = builder.HasIndex(n => n.CreatedAt).IsDescending(); // Most recent first
        _ = builder.HasIndex(n => n.ExpiresAt); // For cleanup queries
    }
}
