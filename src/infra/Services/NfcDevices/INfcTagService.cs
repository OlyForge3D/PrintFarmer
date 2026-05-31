namespace Farm.Infrastructure.Services.NfcDevices;

/// <summary>
/// Handles NFC tag binding lookup, SpoolLastSeenAt updates, and SignalR event broadcasting.
/// </summary>
public interface INfcTagService
{
    /// <summary>
    /// Processes a tag read from an NFC device.
    /// Looks up the tag UID in NfcTagBinding:
    ///   - Known tag: updates SpoolLastSeenAt and broadcasts nfctagread
    ///   - Unknown tag: broadcasts nfctagunknown
    /// If the device is offline (heartbeat timeout) the event is queued until reconnect.
    /// </summary>
    Task ProcessTagReadAsync(string tagUid, Guid nfcDeviceId, Guid? printerId, DateTime readAt, CancellationToken ct);

    /// <summary>
    /// Creates or updates a tag binding (POST /api/nfc/link).
    /// </summary>
    Task<NfcTagBindingDto> LinkTagAsync(LinkNfcTagRequest request, CancellationToken ct);

    /// <summary>
    /// Flushes any queued scan events for the device (called on heartbeat / reconnect).
    /// </summary>
    Task FlushOfflineQueueAsync(Guid nfcDeviceId, CancellationToken ct);
}
