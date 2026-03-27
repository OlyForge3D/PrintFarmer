using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Cost;

/// <summary>
/// Service for calculating detailed cost breakdowns for print jobs.
/// Calculates material, energy, machine time, and labor costs.
/// </summary>
public class JobCostCalculationService : IJobCostCalculationService
{
    private readonly AppDbContext _db;
    private readonly ISpoolmanService _spoolmanService;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<JobCostCalculationService> _logger;

    public JobCostCalculationService(
        AppDbContext db,
        ISpoolmanService spoolmanService,
        ISettingsService settingsService,
        ILogger<JobCostCalculationService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _spoolmanService = spoolmanService ?? throw new ArgumentNullException(nameof(spoolmanService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<bool> CalculateAndStoreCostsAsync(Guid jobId, CancellationToken ct = default)
    {
        CostTrackingSettings? settings = _settingsService.Get<CostTrackingSettings>();

        if (settings == null || !settings.EnableAutomaticCostCalculation)
        {
            _logger.LogDebug("Automatic cost calculation is disabled. Skipping cost calculation for job {JobId}.", jobId);
            return false;
        }

        PrintJob? job = await _db.PrintJobs
            .Include(j => j.AssignedPrinter)
                .ThenInclude(p => p!.Model)
            .FirstOrDefaultAsync(j => j.Id == jobId, ct);

        if (job == null)
        {
            _logger.LogWarning("Job {JobId} not found. Cannot calculate costs.", jobId);
            return false;
        }

        // Calculate material cost
        decimal? materialCost = await CalculateMaterialCostAsync(job, ct);

        // Calculate energy cost
        decimal? energyCost = CalculateEnergyCost(job, settings);

        // Calculate machine time cost
        decimal? machineTimeCost = CalculateMachineTimeCost(job, settings);

        // Calculate subtotal (material + energy + machine)
        decimal subtotal = (materialCost ?? 0m) + (energyCost ?? 0m) + (machineTimeCost ?? 0m);

        // Calculate labor cost (subtotal × labor markup %)
        decimal? laborCost = CalculateLaborCost(subtotal, settings);

        // Calculate total cost
        decimal? totalCost = subtotal + (laborCost ?? 0m);

        // Store results
        job.MaterialCostUsd = materialCost;
        job.EnergyCostUsd = energyCost;
        job.MachineTimeCostUsd = machineTimeCost;
        job.LaborCostUsd = laborCost;
        job.TotalCostUsd = totalCost > 0 ? totalCost : null;
        job.CostCalculatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Cost calculated for job {JobId}: Material={Material:C2}, Energy={Energy:C2}, Machine={Machine:C2}, Labor={Labor:C2}, Total={Total:C2}",
            jobId,
            materialCost ?? 0m,
            energyCost ?? 0m,
            machineTimeCost ?? 0m,
            laborCost ?? 0m,
            totalCost ?? 0m);

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RecalculateCostsWithOverridesAsync(
        Guid jobId,
        decimal? materialCost = null,
        decimal? energyCost = null,
        decimal? machineTimeCost = null,
        decimal? laborCost = null,
        CancellationToken ct = default)
    {
        PrintJob? job = await _db.PrintJobs
            .Include(j => j.AssignedPrinter)
                .ThenInclude(p => p!.Model)
            .FirstOrDefaultAsync(j => j.Id == jobId, ct);

        if (job == null)
        {
            _logger.LogWarning("Job {JobId} not found. Cannot recalculate costs.", jobId);
            return false;
        }

        CostTrackingSettings? settings = _settingsService.Get<CostTrackingSettings>();

        // Use overrides if provided, otherwise recalculate
        decimal? finalMaterialCost = materialCost ?? await CalculateMaterialCostAsync(job, ct);
        decimal? finalEnergyCost = energyCost ?? CalculateEnergyCost(job, settings);
        decimal? finalMachineTimeCost = machineTimeCost ?? CalculateMachineTimeCost(job, settings);

        // Calculate subtotal
        decimal subtotal = (finalMaterialCost ?? 0m) + (finalEnergyCost ?? 0m) + (finalMachineTimeCost ?? 0m);

        // Use labor override if provided, otherwise recalculate
        decimal? finalLaborCost = laborCost ?? CalculateLaborCost(subtotal, settings);

        // Calculate total
        decimal? totalCost = subtotal + (finalLaborCost ?? 0m);

        // Store results
        job.MaterialCostUsd = finalMaterialCost;
        job.EnergyCostUsd = finalEnergyCost;
        job.MachineTimeCostUsd = finalMachineTimeCost;
        job.LaborCostUsd = finalLaborCost;
        job.TotalCostUsd = totalCost > 0 ? totalCost : null;
        job.CostCalculatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Cost recalculated for job {JobId} with overrides.", jobId);

        return true;
    }

    /// <summary>
    /// Calculates material cost from filament usage and pricing.
    /// Price cascade: spool.Price → filament.Price → material type default → global default.
    /// Weight cascade: spool.InitialWeightG → filament.Weight → 1000g default.
    /// Formula: (actualFilamentUsage / spoolWeight) × pricePerKg
    /// </summary>
    private async Task<decimal?> CalculateMaterialCostAsync(PrintJob job, CancellationToken ct)
    {
        if (!job.ActualFilamentUsage.HasValue || job.ActualFilamentUsage.Value <= 0)
        {
            return null;
        }

        CostTrackingSettings? settings = _settingsService.Get<CostTrackingSettings>();

        double? effectivePrice = null;
        double spoolWeightGrams = 1000.0;
        string? materialType = null;

        // Try Spoolman data first when available
        if (job.SpoolmanFilamentId.HasValue)
        {
            try
            {
                SpoolmanFilamentDto? filament = await _spoolmanService.GetFilamentByIdAsync(job.SpoolmanFilamentId.Value, ct);

                if (filament != null)
                {
                    materialType = filament.Material;

                    // Backfill FilamentName from Spoolman if missing
                    if (string.IsNullOrEmpty(job.FilamentName) && !string.IsNullOrEmpty(filament.Name))
                    {
                        job.FilamentName = filament.Name;
                    }

                    // Look up spool instance for per-spool price and weight overrides
                    double? spoolPrice = null;
                    double? spoolInitialWeight = null;
                    if (job.SpoolmanSpoolId.HasValue)
                    {
                        try
                        {
                            SpoolmanSpoolDto? spool = await _spoolmanService.GetSpoolByIdAsync(job.SpoolmanSpoolId.Value, ct);
                            if (spool != null)
                            {
                                if (spool.Price is > 0)
                                {
                                    spoolPrice = spool.Price;
                                }

                                if (spool.InitialWeightG is > 0)
                                {
                                    spoolInitialWeight = spool.InitialWeightG;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Failed to look up spool {SpoolId}. Falling back to filament defaults.", job.SpoolmanSpoolId.Value);
                        }
                    }

                    // Price cascade level 1-2: spool price → filament product price
                    effectivePrice = spoolPrice ?? filament.Price;

                    // Weight cascade: spool initial weight → filament product weight → 1kg default
                    spoolWeightGrams = spoolInitialWeight ?? filament.Weight ?? 1000.0;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to look up Spoolman filament {FilamentId}. Falling back to defaults.", job.SpoolmanFilamentId.Value);
            }
        }

        // Price cascade level 3: material type default from settings
        if ((!effectivePrice.HasValue || effectivePrice.Value <= 0) && settings != null)
        {
            // Resolve material type from: Spoolman → job.RequiredMaterialType → job.FilamentName
            string? resolvedMaterial = materialType
                ?? job.RequiredMaterialType
                ?? job.FilamentName;

            if (!string.IsNullOrEmpty(resolvedMaterial))
            {
                decimal matchedPrice = LookupMaterialPrice(resolvedMaterial, settings.MaterialPriceDefaults);
                if (matchedPrice > 0)
                {
                    effectivePrice = (double)matchedPrice;
                }
            }
        }

        // Price cascade level 4: global fallback from settings
        if ((!effectivePrice.HasValue || effectivePrice.Value <= 0)
            && settings != null
            && settings.DefaultFilamentPricePerKg > 0)
        {
            effectivePrice = (double)settings.DefaultFilamentPricePerKg;
        }

        if (!effectivePrice.HasValue || effectivePrice.Value <= 0)
        {
            _logger.LogDebug(
                "No price data available for job {JobId} (FilamentId={FilamentId}, SpoolId={SpoolId}). Material cost will be null.",
                job.Id,
                job.SpoolmanFilamentId,
                job.SpoolmanSpoolId);
            return null;
        }

        if (spoolWeightGrams <= 0)
        {
            spoolWeightGrams = 1000.0;
        }

        decimal cost = (decimal)(job.ActualFilamentUsage.Value / spoolWeightGrams) * (decimal)effectivePrice.Value;

        return Math.Round(cost, 2);
    }

    /// <summary>
    /// Looks up a material price from the defaults dictionary using case-insensitive
    /// substring matching. For example, "PolyTerra PLA Charcoal Black" matches "PLA".
    /// </summary>
    private static decimal LookupMaterialPrice(string materialName, Dictionary<string, decimal> defaults)
    {
        // Try exact match first (case-insensitive via dictionary comparer)
        if (defaults.TryGetValue(materialName, out decimal exactPrice))
        {
            return exactPrice;
        }

        // Try substring match: check if materialName contains any known material key
        foreach (KeyValuePair<string, decimal> entry in defaults)
        {
            if (materialName.Contains(entry.Key, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Value;
            }
        }

        return 0m;
    }

    /// <summary>
    /// Calculates energy cost from print duration and electricity rate.
    /// Wattage cascade: printer.Wattage → printer.Model.DefaultWattage → settings.AveragePrinterWattage.
    /// Formula: (printDurationHours × printerWattage / 1000) × electricityRatePerKwh
    /// </summary>
    /// <param name="job">The print job with AssignedPrinter and Model loaded.</param>
    /// <param name="settings">Global cost tracking settings for fallback values.</param>
    private decimal? CalculateEnergyCost(PrintJob job, CostTrackingSettings? settings)
    {
        if (!job.ActualPrintTime.HasValue || job.ActualPrintTime.Value.TotalHours <= 0 || settings == null)
        {
            return null;
        }

        decimal printDurationHours = (decimal)job.ActualPrintTime.Value.TotalHours;
        decimal printerWattage = job.AssignedPrinter?.Wattage
            ?? job.AssignedPrinter?.Model?.DefaultWattage
            ?? settings.AveragePrinterWattage;
        decimal electricityRate = settings.ElectricityRatePerKwh;

        // Convert watts to kilowatts and multiply by hours and rate
        decimal energyCost = (printDurationHours * printerWattage / 1000m) * electricityRate;

        return Math.Round(energyCost, 2);
    }

    /// <summary>
    /// Calculates machine time cost from print duration and hourly rate.
    /// Formula: printDurationHours × machineHourlyRate
    /// </summary>
    private decimal? CalculateMachineTimeCost(PrintJob job, CostTrackingSettings? settings)
    {
        if (!job.ActualPrintTime.HasValue || job.ActualPrintTime.Value.TotalHours <= 0 || settings == null)
        {
            return null;
        }

        decimal printDurationHours = (decimal)job.ActualPrintTime.Value.TotalHours;

        // Use per-printer rate if available, then model default, then global setting
        decimal machineHourlyRate = job.AssignedPrinter?.MachineHourlyRate
            ?? job.AssignedPrinter?.Model?.DefaultHourlyRate
            ?? settings.DefaultMachineHourlyRate;

        decimal machineTimeCost = printDurationHours * machineHourlyRate;

        return Math.Round(machineTimeCost, 2);
    }

    /// <summary>
    /// Calculates labor cost as a percentage of the subtotal.
    /// Formula: subtotal × (laborMarkupPercent / 100)
    /// </summary>
    private decimal? CalculateLaborCost(decimal subtotal, CostTrackingSettings? settings)
    {
        if (settings == null || settings.LaborMarkupPercent <= 0 || subtotal <= 0)
        {
            return null;
        }

        decimal laborCost = subtotal * (settings.LaborMarkupPercent / 100m);

        return Math.Round(laborCost, 2);
    }
}
