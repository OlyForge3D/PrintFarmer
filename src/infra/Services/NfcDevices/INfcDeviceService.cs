namespace Farm.Infrastructure.Services.NfcDevices;

/// <summary>
/// Service for managing NFC reader devices and their scan history.
/// </summary>
public interface INfcDeviceService
{
    Task<NfcDeviceDto[]> GetAllAsync(CancellationToken ct);

    Task<NfcDeviceDto?> GetByIdAsync(Guid id, CancellationToken ct);

    Task<NfcDeviceDto> CreateAsync(CreateNfcDeviceDto dto, CancellationToken ct);

    Task<NfcDeviceDto?> UpdateAsync(Guid id, UpdateNfcDeviceDto dto, CancellationToken ct);

    Task<bool> DeleteAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Approves a pending (or previously approved) device, issuing a fresh device token.
    /// The raw token is returned once and must be provisioned into the device firmware;
    /// only its hash is persisted.
    /// </summary>
    Task<NfcDeviceApprovalResultDto?> ApproveAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Processes a heartbeat. Unknown printer IDs create a new, unapproved (pending) device —
    /// a claim only, not a credential. Heartbeats for an already-approved device must present
    /// a valid device token or are rejected (<c>Unauthorized</c> = true).
    /// </summary>
    Task<(NfcDeviceDto? Device, bool Unauthorized)> ProcessHeartbeatAsync(
        NfcDeviceHeartbeatDto dto,
        string? presentedToken,
        CancellationToken ct);

    /// <summary>
    /// Processes a scan event. Requires an approved device presenting a valid device token;
    /// otherwise the request is rejected (<c>Unauthorized</c> = true) and no event is recorded.
    /// </summary>
    Task<(NfcScanHistoryDto? Result, bool Unauthorized)> ProcessScanEventAsync(
        NfcScanEventDto dto,
        string? presentedToken,
        CancellationToken ct);

    Task<NfcScanHistoryDto[]> GetScanHistoryAsync(Guid deviceId, int limit, int offset, CancellationToken ct);
}
