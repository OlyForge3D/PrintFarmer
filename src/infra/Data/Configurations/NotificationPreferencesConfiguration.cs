using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class NotificationPreferencesConfiguration : IEntityTypeConfiguration<NotificationPreferences>
{
    public void Configure(EntityTypeBuilder<NotificationPreferences> builder)
    {
        _ = builder.HasKey(np => np.Id);
        _ = builder.Property(np => np.UserId).IsRequired();
        _ = builder.Property(np => np.Frequency).IsRequired().HasDefaultValue(NotificationFrequency.RealTime);
        _ = builder.Property(np => np.RetentionDays).IsRequired().HasDefaultValue(30);
        _ = builder.Property(np => np.UpdatedAt).IsRequired();

        // Foreign Key - one-to-one relationship with User
        _ = builder.HasOne(np => np.User)
            .WithOne()
            .HasForeignKey<NotificationPreferences>(np => np.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Unique constraint - one preferences per user
        _ = builder.HasIndex(np => np.UserId).IsUnique();
    }
}
