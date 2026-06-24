using Farm.Infrastructure.Data;
using Farm.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Services;

/// <summary>
/// Consolidates access to farm-wide settings backed by <see cref="CostTrackingSettings"/>
/// (the primary source for electricity rate, wattage, and hourly rate).
/// </summary>
public class FarmSettingsService(ISettingsService settingsService, IDbContextFactory<AppDbContext> dbContextFactory) : IFarmSettingsService
{
    private readonly ISettingsService _settings = settingsService;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory = dbContextFactory;

    /// <inheritdoc />
    public FarmSettingsDto GetFarmSettings()
    {
        CostTrackingSettings cost = _settings.Get<CostTrackingSettings>();
        SlicerSettings slicer = _settings.Get<SlicerSettings>();

        // canWrite is always false here; the controller sets it based on role.
        return new FarmSettingsDto(
            ElectricityRatePerKwh: cost.ElectricityRatePerKwh,
            DefaultMachineHourlyRate: cost.DefaultMachineHourlyRate,
            AveragePrinterWattage: cost.AveragePrinterWattage,
            CanWrite: false,
            SlicerMode: slicer.SlicerMode);
    }

    /// <inheritdoc />
    public string? GetFarmSettingsRowVersion()
    {
        using AppDbContext db = _dbContextFactory.CreateDbContext();
        AppSettingsEntity? entity = db.AppSettingsEntities
            .AsNoTracking()
            .FirstOrDefault(e => e.Key == CostTrackingSettings.SectionName);
        return entity?.RowVersion is { Length: > 0 } rv ? Convert.ToBase64String(rv) : null;
    }

    /// <inheritdoc />
    public void UpdateFarmSettings(UpdateFarmSettingsRequest request, string? expectedRowVersion = null)
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

        SlicerSettings slicer = _settings.Get<SlicerSettings>();
        if (request.SlicerMode.HasValue)
        {
            slicer.SlicerMode = request.SlicerMode.Value;
        }

        if (expectedRowVersion is not null)
        {
            SaveWithConcurrencyCheck(cost, expectedRowVersion);
        }
        else
        {
            _settings.Save(cost);
        }

        if (request.SlicerMode.HasValue)
        {
            _settings.Save(slicer);
        }
    }

    private void SaveWithConcurrencyCheck(CostTrackingSettings cost, string expectedRowVersion)
    {
        using AppDbContext db = _dbContextFactory.CreateDbContext();
        AppSettingsEntity? entity = db.AppSettingsEntities
            .FirstOrDefault(e => e.Key == CostTrackingSettings.SectionName);

        if (entity is null)
        {
            // No existing entity — fall back to normal save (no conflict possible)
            _settings.Save(cost);
            return;
        }

        // Set the original row version so EF enforces the concurrency check
        byte[] expectedBytes = Convert.FromBase64String(expectedRowVersion);
        db.Entry(entity).Property(e => e.RowVersion).OriginalValue = expectedBytes;

        entity.SettingsJson = System.Text.Json.JsonSerializer.Serialize(cost);
        entity.UpdatedAt = DateTime.UtcNow;

        db.SaveChanges(); // Throws DbUpdateConcurrencyException on stale token

        // Update in-memory cache
        _settings.Save(cost);
    }
}
