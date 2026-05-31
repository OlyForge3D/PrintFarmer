using Farm.Infrastructure.Settings;

namespace Farm.Infrastructure.Services;

/// <summary>
/// Exposes farm-wide configuration as a single consolidated DTO.
/// Farm settings are shared across all users; writes are admin-only.
/// </summary>
public interface IFarmSettingsService
{
    /// <summary>Gets the consolidated farm-wide settings.</summary>
    FarmSettingsDto GetFarmSettings();

    /// <summary>Gets the row version of the underlying AppSettingsEntity for concurrency control.</summary>
    string? GetFarmSettingsRowVersion();

    /// <summary>Updates the farm-wide cost-tracking settings.</summary>
    void UpdateFarmSettings(UpdateFarmSettingsRequest request, string? expectedRowVersion = null);
}

/// <summary>
/// Farm-wide settings as returned by GET /api/settings/farm.
/// <para>
/// <c>canWrite</c> is <c>true</c> only for admin callers (set by the controller).
/// </para>
/// </summary>
public record FarmSettingsDto(
    decimal ElectricityRatePerKwh,
    decimal DefaultMachineHourlyRate,
    decimal AveragePrinterWattage,
    bool CanWrite);

/// <summary>Payload for PUT /api/settings/farm.</summary>
public record UpdateFarmSettingsRequest(
    decimal? ElectricityRatePerKwh,
    decimal? DefaultMachineHourlyRate,
    decimal? AveragePrinterWattage);
