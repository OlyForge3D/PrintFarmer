namespace Farm.Infrastructure.Services.Notifications;

/// <summary>
/// Enumerates the nine event×channel rows the shared preference matrix carries.
/// Duplicates the wire enum (<c>NotificationPreferenceEventType</c>) but lives in
/// the service layer so <see cref="NotificationService"/> does not depend on the
/// API DTOs. The API controller maps its wire enum values onto these tokens 1:1.
/// </summary>
public enum NotificationPreferenceEvent
{
    /// <summary>Job execution started.</summary>
    JobStarted,

    /// <summary>Job execution completed successfully.</summary>
    JobCompleted,

    /// <summary>Job execution failed.</summary>
    JobFailed,

    /// <summary>Job execution paused.</summary>
    JobPaused,

    /// <summary>Printer entered a failure state.</summary>
    PrinterFailure,

    /// <summary>Filament runout detected.</summary>
    FilamentRunout,

    /// <summary>Harvest is ready to collect.</summary>
    HarvestReady,

    /// <summary>Maintenance is due for a printer or component.</summary>
    MaintenanceDue,

    /// <summary>Printer went offline.</summary>
    PrinterOffline,
}

/// <summary>
/// A single row within a <see cref="NotificationPreferencesUpdate"/> matrix patch.
/// All four channel flags are always supplied together (the wire contract is
/// row-shaped), but omitted rows are simply absent from the patch and MUST be
/// preserved by the service.
/// </summary>
/// <param name="EventType">Which of the nine events this row applies to.</param>
/// <param name="InApp">In-app channel opt-in for this event.</param>
/// <param name="Email">Email channel opt-in for this event.</param>
/// <param name="Push">Push channel opt-in for this event.</param>
/// <param name="Telegram">Telegram channel opt-in for this event.</param>
public sealed record NotificationPreferencesRowPatch(
    NotificationPreferenceEvent EventType,
    bool InApp,
    bool Email,
    bool Push,
    bool Telegram);

/// <summary>
/// Authoritative preference-patch payload consumed by
/// <see cref="INotificationService.UpdatePreferencesAsync(System.Guid, NotificationPreferencesUpdate, System.Threading.CancellationToken)"/>.
/// The service applies this as a patch over the tracked persisted entity:
/// scalars overwrite, supplied matrix rows overwrite their columns on the entity,
/// and every omitted row is preserved. Vasquez v6 B3 mandates this shape so
/// partial modern PUTs cannot revert omitted rows to defaults.
/// </summary>
/// <remarks>
/// <para>
/// Two update shapes are supported:
/// </para>
/// <para>
/// <b>Legacy</b>: <see cref="MatrixRows"/> is <see langword="null"/>. The service
/// derives the four job rows from <see cref="NotifyOnStart"/>,
/// <see cref="NotifyOnCompletion"/>, <see cref="NotifyOnFailure"/>,
/// <see cref="NotifyOnPause"/>, and the four Enable{Channel}Notifications
/// scalars — matching pre-#708 semantics. Attention rows are NOT touched.
/// </para>
/// <para>
/// <b>Modern (matrix)</b>: <see cref="MatrixRows"/> is a possibly-partial list of
/// row patches. The service applies each supplied row over the tracked entity
/// and preserves every omitted row's persisted value. Duplicate rows for the
/// same event are last-write-wins within the request.
/// </para>
/// </remarks>
/// <param name="EnableEmailNotifications">Legacy master opt-in for the email channel; used only in the legacy branch to derive job rows.</param>
/// <param name="EnablePushNotifications">Legacy master opt-in for the push channel; used only in the legacy branch to derive job rows.</param>
/// <param name="EnableInAppNotifications">Legacy master opt-in for the in-app channel; used only in the legacy branch to derive job rows.</param>
/// <param name="EnableTelegramNotifications">Legacy master opt-in for the telegram channel; used only in the legacy branch to derive job rows.</param>
/// <param name="NotifyOnStart">Legacy per-event toggle for job start; used only in the legacy branch.</param>
/// <param name="NotifyOnCompletion">Legacy per-event toggle for job completion; used only in the legacy branch.</param>
/// <param name="NotifyOnFailure">Legacy per-event toggle for job failure; used only in the legacy branch.</param>
/// <param name="NotifyOnPause">Legacy per-event toggle for job pause; used only in the legacy branch.</param>
/// <param name="Frequency">Overall notification frequency setting; always applied.</param>
/// <param name="RetentionDays">Notification retention window; always applied.</param>
/// <param name="MatrixRows">
/// Optional list of per-event row patches. When <see langword="null"/>, the
/// service uses the legacy derivation from the scalar toggles. When non-null,
/// every supplied row overwrites its columns on the tracked entity and every
/// omitted row's persisted value is preserved.
/// </param>
public sealed record NotificationPreferencesUpdate(
    bool EnableEmailNotifications,
    bool EnablePushNotifications,
    bool EnableInAppNotifications,
    bool EnableTelegramNotifications,
    bool NotifyOnStart,
    bool NotifyOnCompletion,
    bool NotifyOnFailure,
    bool NotifyOnPause,
    Farm.Infrastructure.Domain.Notifications.NotificationFrequency Frequency,
    int RetentionDays,
    IReadOnlyList<NotificationPreferencesRowPatch>? MatrixRows);
