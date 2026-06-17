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
        _ = builder.Property(np => np.InAppOnJobStarted).IsRequired().HasDefaultValue(false);
        _ = builder.Property(np => np.InAppOnJobCompleted).IsRequired().HasDefaultValue(true);
        _ = builder.Property(np => np.InAppOnJobFailed).IsRequired().HasDefaultValue(true);
        _ = builder.Property(np => np.InAppOnJobPaused).IsRequired().HasDefaultValue(true);
        _ = builder.Property(np => np.EmailOnJobStarted).IsRequired().HasDefaultValue(false);
        _ = builder.Property(np => np.EmailOnJobCompleted).IsRequired().HasDefaultValue(true);
        _ = builder.Property(np => np.EmailOnJobFailed).IsRequired().HasDefaultValue(true);
        _ = builder.Property(np => np.EmailOnJobPaused).IsRequired().HasDefaultValue(true);
        _ = builder.Property(np => np.PushOnJobStarted).IsRequired().HasDefaultValue(false);
        _ = builder.Property(np => np.PushOnJobCompleted).IsRequired().HasDefaultValue(true);
        _ = builder.Property(np => np.PushOnJobFailed).IsRequired().HasDefaultValue(true);
        _ = builder.Property(np => np.PushOnJobPaused).IsRequired().HasDefaultValue(true);

        // Foreign Key - one-to-one relationship with User
        _ = builder.HasOne(np => np.User)
            .WithOne()
            .HasForeignKey<NotificationPreferences>(np => np.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Unique constraint - one preferences per user
        _ = builder.HasIndex(np => np.UserId).IsUnique();
    }
}
