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
    /// Calculates material cost from Spoolman filament price and usage.
    /// Formula: (usageGrams / filamentWeightGrams) × filamentPrice
    /// </summary>
    private async Task<decimal?> CalculateMaterialCostAsync(PrintJob job, CancellationToken ct)
    {
        if (!job.SpoolmanFilamentId.HasValue || !job.ActualFilamentUsage.HasValue || job.ActualFilamentUsage.Value <= 0)
        {
            return null;
        }

        try
        {
            SpoolmanFilamentDto? filament = await _spoolmanService.GetFilamentByIdAsync(job.SpoolmanFilamentId.Value, ct);

            if (filament == null || !filament.Price.HasValue || filament.Price.Value <= 0)
            {
                _logger.LogDebug("Spoolman filament {FilamentId} has no price data. Material cost will be null.", job.SpoolmanFilamentId.Value);
                return null;
            }

            double spoolWeightGrams = filament.Weight ?? 1000.0; // Default to 1kg if not set

            if (spoolWeightGrams <= 0)
            {
                spoolWeightGrams = 1000.0;
            }

            decimal cost = (decimal)(job.ActualFilamentUsage.Value / spoolWeightGrams) * (decimal)filament.Price.Value;

            return Math.Round(cost, 2);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to calculate material cost for job {JobId}.", job.Id);
            return null;
        }
    }

    /// <summary>
    /// Calculates energy cost from print duration and electricity rate.
    /// Formula: (printDurationHours × printerWattage / 1000) × electricityRatePerKwh
    /// </summary>
    private decimal? CalculateEnergyCost(PrintJob job, CostTrackingSettings? settings)
    {
        if (!job.ActualPrintTime.HasValue || job.ActualPrintTime.Value.TotalHours <= 0 || settings == null)
        {
            return null;
        }

        decimal printDurationHours = (decimal)job.ActualPrintTime.Value.TotalHours;
        decimal printerWattage = settings.AveragePrinterWattage; // Could be enhanced with per-printer wattage in the future
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

        // Use per-printer rate if available, otherwise use default
        decimal machineHourlyRate = job.AssignedPrinter?.MachineHourlyRate ?? settings.DefaultMachineHourlyRate;

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
