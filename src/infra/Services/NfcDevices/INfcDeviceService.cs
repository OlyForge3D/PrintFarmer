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

    Task<NfcDeviceDto?> ProcessHeartbeatAsync(NfcDeviceHeartbeatDto dto, CancellationToken ct);

    Task<NfcScanHistoryDto?> ProcessScanEventAsync(NfcScanEventDto dto, CancellationToken ct);

    Task<NfcScanHistoryDto[]> GetScanHistoryAsync(Guid deviceId, int limit, int offset, CancellationToken ct);
}
