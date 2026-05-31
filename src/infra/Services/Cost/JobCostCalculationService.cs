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

    // Optional: when registered, provides cached Spoolman price lookups.
    private readonly IFilamentCostProvider? _filamentCostProvider;

    public JobCostCalculationService(
        AppDbContext db,
        ISpoolmanService spoolmanService,
        ISettingsService settingsService,
        ILogger<JobCostCalculationService> logger,
        IFilamentCostProvider? filamentCostProvider = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _spoolmanService = spoolmanService ?? throw new ArgumentNullException(nameof(spoolmanService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _filamentCostProvider = filamentCostProvider;
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
            .Include(j => j.ToolheadUsages)
            .FirstOrDefaultAsync(j => j.Id == jobId, ct);

        if (job == null)
        {
            _logger.LogWarning("Job {JobId} not found. Cannot calculate costs.", jobId);
            return false;
        }

        // Calculate material cost (per-toolhead if usage records exist, single-spool otherwise)
        decimal? materialCost = await CalculateMaterialCostAsync(job, ct);

        // Calculate energy cost — uses measured KwhUsed when available, otherwise wattage estimate
        decimal? energyCost = await CalculateEnergyCostAsync(job, settings, ct);

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
            .Include(j => j.ToolheadUsages)
            .FirstOrDefaultAsync(j => j.Id == jobId, ct);

        if (job == null)
        {
            _logger.LogWarning("Job {JobId} not found. Cannot recalculate costs.", jobId);
            return false;
        }

        CostTrackingSettings? settings = _settingsService.Get<CostTrackingSettings>();

        // Use overrides if provided, otherwise recalculate
        decimal? finalMaterialCost = materialCost ?? await CalculateMaterialCostAsync(job, ct);
        decimal? finalEnergyCost = energyCost ?? await CalculateEnergyCostAsync(job, settings, ct);
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
    /// Usage cascade: ActualFilamentUsage → EstimatedFilamentUsage.
    /// Price cascade: spool.Price → filament.Price → material type default → global default.
    /// Weight cascade: spool.InitialWeightG → filament.Weight → 1000g default.
    /// Formula: (filamentUsageGrams / spoolWeight) × pricePerKg
    /// </summary>
    private async Task<decimal?> CalculateMaterialCostAsync(PrintJob job, CancellationToken ct)
    {
        // Multi-toolhead path: if per-toolhead usage records exist, calculate per-toolhead costs
        if (job.ToolheadUsages is { Count: > 0 })
        {
            decimal totalMaterialCost = 0m;
            bool anyCalculated = false;

            foreach (PrintJobToolheadUsage usage in job.ToolheadUsages)
            {
                if (usage.FilamentUsageGrams is not > 0)
                {
                    continue;
                }

                decimal? perToolheadCost = await CalculateSingleSpoolCostAsync(
                    usage.SpoolmanSpoolId, usage.FilamentUsageGrams.Value, job, ct);

                if (perToolheadCost.HasValue)
                {
                    usage.MaterialCostUsd = perToolheadCost.Value;
                    totalMaterialCost += perToolheadCost.Value;
                    anyCalculated = true;
                }
            }

            return anyCalculated ? totalMaterialCost : null;
        }

        // Single-spool path (existing behavior)
        // Usage cascade: actual → estimated
        double? filamentUsageGrams = job.ActualFilamentUsage is > 0
            ? job.ActualFilamentUsage
            : job.EstimatedFilamentUsage;

        if (!filamentUsageGrams.HasValue || filamentUsageGrams.Value <= 0)
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

        decimal cost = (decimal)(filamentUsageGrams.Value / spoolWeightGrams) * (decimal)effectivePrice.Value;

        return Math.Round(cost, 2);
    }

    /// <summary>
    /// Calculates material cost for a single spool given its Spoolman ID and filament usage.
    /// Price cascade:
    ///   1. <see cref="IFilamentCostProvider"/> (cached Spoolman spool price / initial weight)
    ///   2. Direct Spoolman filament lookup for material type (settings fallback only)
    ///   3. Per-material-type default from settings
    ///   4. Global default filament price from settings
    /// Used by the multi-toolhead path to calculate per-toolhead costs independently.
    /// </summary>
    private async Task<decimal?> CalculateSingleSpoolCostAsync(
        int? spoolmanSpoolId, double usageGrams, PrintJob job, CancellationToken ct)
    {
        CostTrackingSettings? settings = _settingsService.Get<CostTrackingSettings>();
        string? materialType = null;

        if (spoolmanSpoolId.HasValue)
        {
            // Fast path: cached cost provider returns cost per gram when Spoolman has price data.
            if (_filamentCostProvider is not null)
            {
                decimal? costPerGram = await _filamentCostProvider.GetSpoolCostPerGramAsync(spoolmanSpoolId.Value, ct);
                if (costPerGram.HasValue)
                {
                    return Math.Round(costPerGram.Value * (decimal)usageGrams, 2);
                }
            }

            // No Spoolman pricing available — fetch material type for settings-based fallback.
            try
            {
                SpoolmanSpoolDto? spool = await _spoolmanService.GetSpoolByIdAsync(spoolmanSpoolId.Value, ct);
                if (spool?.FilamentId is int filamentId)
                {
                    SpoolmanFilamentDto? filament = await _spoolmanService.GetFilamentByIdAsync(filamentId, ct);
                    materialType = filament?.Material;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to look up spool {SpoolId} for per-toolhead material type.", spoolmanSpoolId.Value);
            }
        }

        // Settings price cascade: material type default → global default (pricePerKg × usageKg).
        if (settings is not null)
        {
            string? resolvedMaterial = materialType ?? job.RequiredMaterialType ?? job.FilamentName;
            if (!string.IsNullOrEmpty(resolvedMaterial))
            {
                decimal matchedPrice = LookupMaterialPrice(resolvedMaterial, settings.MaterialPriceDefaults);
                if (matchedPrice > 0)
                {
                    return Math.Round((decimal)(usageGrams / 1000.0) * matchedPrice, 2);
                }
            }
        }

        if (settings?.DefaultFilamentPricePerKg > 0)
        {
            return Math.Round((decimal)(usageGrams / 1000.0) * settings.DefaultFilamentPricePerKg, 2);
        }

        return null;
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
    /// Calculates energy cost from power consumption data.
    /// When <see cref="PrintJob.KwhUsed"/> is set (measured by a smart plug), uses that directly.
    /// The electricity rate is taken from the printer's <see cref="PowerMonitor"/> if one is
    /// configured (and its rate is non-zero); otherwise falls back to
    /// <see cref="CostTrackingSettings.ElectricityRatePerKwh"/>.
    /// When <see cref="PrintJob.KwhUsed"/> is null, falls back to a wattage-based estimate:
    /// (printDurationHours × printerWattage / 1000) × electricityRatePerKwh.
    /// </summary>
    private async Task<decimal?> CalculateEnergyCostAsync(
        PrintJob job,
        CostTrackingSettings? settings,
        CancellationToken ct)
    {
        if (settings == null)
        {
            return null;
        }

        if (job.KwhUsed.HasValue && job.KwhUsed.Value > 0)
        {
            // Prefer per-monitor rate; fall back to farm-wide rate
            decimal rate = settings.ElectricityRatePerKwh;

            if (job.AssignedPrinterId.HasValue)
            {
                PowerMonitor? monitor = await _db.Set<PowerMonitor>()
                    .Where(m => m.PrinterId == job.AssignedPrinterId.Value && m.IsEnabled)
                    .FirstOrDefaultAsync(ct);

                if (monitor is { ElectricityRateUsdPerKwh: > 0 })
                {
                    rate = monitor.ElectricityRateUsdPerKwh;
                }
            }

            return Math.Round(job.KwhUsed.Value * rate, 2);
        }

        // Wattage-based estimate when no measured energy is available
        if (!job.ActualPrintTime.HasValue || job.ActualPrintTime.Value.TotalHours <= 0)
        {
            return null;
        }

        decimal printDurationHours = (decimal)job.ActualPrintTime.Value.TotalHours;
        decimal printerWattage = job.AssignedPrinter?.Wattage
            ?? job.AssignedPrinter?.Model?.DefaultWattage
            ?? settings.AveragePrinterWattage;
        decimal electricityRate = settings.ElectricityRatePerKwh;

        return Math.Round((printDurationHours * printerWattage / 1000m) * electricityRate, 2);
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

    /// <inheritdoc />
    public async Task<int> RecalculateAllAsync(CancellationToken ct = default)
    {
        List<Guid> jobIds = await _db.PrintJobs
            .Where(j => j.Status == PrintJobStatus.Completed)
            .Where(j => j.TotalCostUsd == null || j.MaterialCostUsd == null)
            .Select(j => j.Id)
            .ToListAsync(ct);

        _logger.LogInformation("Recalculating costs for {Count} completed jobs.", jobIds.Count);

        int recalculated = 0;
        foreach (Guid jobId in jobIds)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                bool success = await RecalculateCostsWithOverridesAsync(jobId, ct: ct);
                if (success)
                {
                    recalculated++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to recalculate costs for job {JobId}. Skipping.", jobId);
            }
        }

        _logger.LogInformation("Successfully recalculated costs for {Recalculated}/{Total} jobs.", recalculated, jobIds.Count);
        return recalculated;
    }
}
