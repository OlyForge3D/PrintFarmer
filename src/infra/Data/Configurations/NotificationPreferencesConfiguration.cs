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

        // Hicks #5: EmailOnJobCompleted/Failed/Paused canonical baseline is
        // false — every email column is opt-in so a fresh row from a bare
        // `{}` PUT (or a first insert against the CLR defaults) never sends
        // surprise email. Aligned with NotificationPreferencesDefaults.Apply
        // and the CLR entity defaults.
        _ = builder.Property(np => np.EmailOnJobCompleted).IsRequired().HasDefaultValue(false);
        _ = builder.Property(np => np.EmailOnJobFailed).IsRequired().HasDefaultValue(false);
        _ = builder.Property(np => np.EmailOnJobPaused).IsRequired().HasDefaultValue(false);
        _ = builder.Property(np => np.PushOnJobStarted).IsRequired().HasDefaultValue(false);
        _ = builder.Property(np => np.PushOnJobCompleted).IsRequired().HasDefaultValue(true);
        _ = builder.Property(np => np.PushOnJobFailed).IsRequired().HasDefaultValue(true);
        _ = builder.Property(np => np.PushOnJobPaused).IsRequired().HasDefaultValue(true);

        // Attention-row per-channel toggles (issue #708 shared preference contract).
        _ = builder.Property(np => np.InAppOnPrinterFailure).IsRequired().HasDefaultValue(true);
        _ = builder.Property(np => np.EmailOnPrinterFailure).IsRequired().HasDefaultValue(false);
        _ = builder.Property(np => np.PushOnPrinterFailure).IsRequired().HasDefaultValue(true);
        _ = builder.Property(np => np.TelegramOnPrinterFailure).IsRequired().HasDefaultValue(false);

        _ = builder.Property(np => np.InAppOnFilamentRunout).IsRequired().HasDefaultValue(true);
        _ = builder.Property(np => np.EmailOnFilamentRunout).IsRequired().HasDefaultValue(false);
        _ = builder.Property(np => np.PushOnFilamentRunout).IsRequired().HasDefaultValue(true);
        _ = builder.Property(np => np.TelegramOnFilamentRunout).IsRequired().HasDefaultValue(false);

        _ = builder.Property(np => np.InAppOnHarvestReady).IsRequired().HasDefaultValue(true);
        _ = builder.Property(np => np.EmailOnHarvestReady).IsRequired().HasDefaultValue(false);
        _ = builder.Property(np => np.PushOnHarvestReady).IsRequired().HasDefaultValue(true);
        _ = builder.Property(np => np.TelegramOnHarvestReady).IsRequired().HasDefaultValue(false);

        _ = builder.Property(np => np.InAppOnMaintenanceDue).IsRequired().HasDefaultValue(true);
        _ = builder.Property(np => np.EmailOnMaintenanceDue).IsRequired().HasDefaultValue(false);
        _ = builder.Property(np => np.PushOnMaintenanceDue).IsRequired().HasDefaultValue(true);
        _ = builder.Property(np => np.TelegramOnMaintenanceDue).IsRequired().HasDefaultValue(false);

        _ = builder.Property(np => np.InAppOnPrinterOffline).IsRequired().HasDefaultValue(true);
        _ = builder.Property(np => np.EmailOnPrinterOffline).IsRequired().HasDefaultValue(false);
        _ = builder.Property(np => np.PushOnPrinterOffline).IsRequired().HasDefaultValue(true);
        _ = builder.Property(np => np.TelegramOnPrinterOffline).IsRequired().HasDefaultValue(false);

        _ = builder.Property(np => np.AttentionPushCategoryPreferencesJson)
            .IsRequired(false);

        // Foreign Key - one-to-one relationship with User
        _ = builder.HasOne(np => np.User)
            .WithOne()
            .HasForeignKey<NotificationPreferences>(np => np.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Unique constraint - one preferences per user
        _ = builder.HasIndex(np => np.UserId).IsUnique();
    }
}
