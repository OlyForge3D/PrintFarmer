using Farm.Infrastructure.Settings;

namespace Farm.Infrastructure.Services;

/// <summary>
/// Consolidates access to farm-wide settings backed by <see cref="CostTrackingSettings"/>
/// (the primary source for electricity rate, wattage, and hourly rate).
/// </summary>
public class FarmSettingsService(ISettingsService settingsService) : IFarmSettingsService
{
    private readonly ISettingsService _settings = settingsService;

    /// <inheritdoc />
    public FarmSettingsDto GetFarmSettings()
    {
        CostTrackingSettings cost = _settings.Get<CostTrackingSettings>();

        // canWrite is always false here; the controller sets it based on role.
        return new FarmSettingsDto(
            ElectricityRatePerKwh: cost.ElectricityRatePerKwh,
            DefaultMachineHourlyRate: cost.DefaultMachineHourlyRate,
            AveragePrinterWattage: cost.AveragePrinterWattage,
            CanWrite: false);
    }

    /// <inheritdoc />
    public void UpdateFarmSettings(UpdateFarmSettingsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        CostTrackingSettings cost = _settings.Get<CostTrackingSettings>();

        if (request.ElectricityRatePerKwh.HasValue)
        {
            cost.ElectricityRatePerKwh = request.ElectricityRatePerKwh.Value;
        }

        if (request.DefaultMachineHourlyRate.HasValue)
        {
            cost.DefaultMachineHourlyRate = request.DefaultMachineHourlyRate.Value;
        }

        if (request.AveragePrinterWattage.HasValue)
        {
            cost.AveragePrinterWattage = request.AveragePrinterWattage.Value;
        }

        _settings.Save(cost);
    }
}
