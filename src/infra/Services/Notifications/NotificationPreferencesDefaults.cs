using Farm.Infrastructure.Domain.Notifications;

namespace Farm.Infrastructure.Services.Notifications;

/// <summary>
/// Canonical shared default matrix for <see cref="NotificationPreferences"/>.
/// One source of truth used by both the anonymous / fresh-user
/// <c>GET /api/notifications/preferences</c> response shape AND the service
/// path that creates a new persisted preferences row on the first PUT for a
/// user. Establishing a single default matrix guarantees that a first partial
/// modern PUT preserves omitted rows exactly as the fresh GET reports them
/// (Hicks #3): the "before" and "after" for any omitted row is the same value.
///
/// Defaults follow the finalized #708 shared preference contract:
/// * Attention rows: InApp/Push = true, Email/Telegram = false (high-signal
///   surfaces on by default; email/telegram opt-in).
/// * Job rows: InApp/Push mirror the pre-#708 completion/failure/pause
///   defaults (start off, others on); Email/Telegram default off across the
///   board so a first-visit user never surprise-emails on a completion.
/// * Master flags derive from the row state after applying the matrix so a
///   response DTO reads exactly what would be persisted.
/// </summary>
public static class NotificationPreferencesDefaults
{
    /// <summary>
    /// Applies the canonical default matrix onto <paramref name="prefs"/>.
    /// Every attention and job column plus every master flag is overwritten,
    /// so callers can use this on either a fresh entity or a partially
    /// populated one — the resulting state is deterministic. Rows not listed
    /// here (Id, UserId, timestamps, category-JSON) are NOT touched.
    /// </summary>
    /// <param name="prefs">Target entity to populate. Must not be null.</param>
    public static void Apply(NotificationPreferences prefs)
    {
        System.ArgumentNullException.ThrowIfNull(prefs);

        // Job rows — mirror the fresh-GET response shape. InApp default:
        // Started=false (avoid noisy start pings); Completed/Failed/Paused=true.
        // Push default: same as InApp. Email/Telegram: all false by default —
        // an opt-in first email requires an explicit user choice.
        prefs.InAppOnJobStarted = false;
        prefs.InAppOnJobCompleted = true;
        prefs.InAppOnJobFailed = true;
        prefs.InAppOnJobPaused = true;

        prefs.EmailOnJobStarted = false;
        prefs.EmailOnJobCompleted = false;
        prefs.EmailOnJobFailed = false;
        prefs.EmailOnJobPaused = false;

        prefs.PushOnJobStarted = false;
        prefs.PushOnJobCompleted = true;
        prefs.PushOnJobFailed = true;
        prefs.PushOnJobPaused = true;

        prefs.TelegramOnJobStarted = false;
        prefs.TelegramOnJobCompleted = false;
        prefs.TelegramOnJobFailed = false;
        prefs.TelegramOnJobPaused = false;

        // Attention rows — high-signal defaults.
        prefs.InAppOnPrinterFailure = true;
        prefs.EmailOnPrinterFailure = false;
        prefs.PushOnPrinterFailure = true;
        prefs.TelegramOnPrinterFailure = false;

        prefs.InAppOnFilamentRunout = true;
        prefs.EmailOnFilamentRunout = false;
        prefs.PushOnFilamentRunout = true;
        prefs.TelegramOnFilamentRunout = false;

        prefs.InAppOnHarvestReady = true;
        prefs.EmailOnHarvestReady = false;
        prefs.PushOnHarvestReady = true;
        prefs.TelegramOnHarvestReady = false;

        prefs.InAppOnMaintenanceDue = true;
        prefs.EmailOnMaintenanceDue = false;
        prefs.PushOnMaintenanceDue = true;
        prefs.TelegramOnMaintenanceDue = false;

        prefs.InAppOnPrinterOffline = true;
        prefs.EmailOnPrinterOffline = false;
        prefs.PushOnPrinterOffline = true;
        prefs.TelegramOnPrinterOffline = false;

        // Legacy per-event scalar toggles kept in sync with the row state so
        // downstream consumers reading NotifyOn* observe the same story.
        prefs.NotifyOnStart = false;
        prefs.NotifyOnCompletion = true;
        prefs.NotifyOnFailure = true;
        prefs.NotifyOnPause = true;

        // Master flags are the OR across all nine rows for each channel.
        // Kept in sync by hand here (rather than a follow-up derive step) so
        // this helper is a single-shot canonical write.
        prefs.EnableInAppNotifications = true;   // any InApp row is true
        prefs.EnableEmailNotifications = false;  // every Email row is false
        prefs.EnablePushNotifications = true;    // any Push row is true
        prefs.EnableTelegramNotifications = false; // every Telegram row is false

        prefs.Frequency = NotificationFrequency.RealTime;
        prefs.RetentionDays = 30;
    }

    /// <summary>Convenience: builds a new default-populated entity for <paramref name="userId"/>.</summary>
    public static NotificationPreferences Create(System.Guid userId)
    {
        var prefs = new NotificationPreferences { UserId = userId };
        Apply(prefs);
        return prefs;
    }
}
