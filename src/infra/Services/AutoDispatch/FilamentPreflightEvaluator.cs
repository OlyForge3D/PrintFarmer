using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Spoolman;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.AutoDispatch;

/// <summary>
/// Evaluates and fingerprints the filament evidence used by ready-gate dispatch.
/// </summary>
public static class FilamentPreflightEvaluator
{
    /// <summary>
    /// Evaluates the current assigned spool against the exact reviewed job.
    /// </summary>
    public static async Task<FilamentCheckResult> CheckAsync(
        Printer printer,
        PrintJob job,
        ISpoolmanService? spoolmanService,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(printer);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(logger);

        var result = new FilamentCheckResult
        {
            RequiredWeightG = job.EstimatedFilamentUsage,
            RequiredMaterial = job.RequiredMaterialType,
        };

        if (printer.CurrentSpoolId is null)
        {
            result.Outcome = FilamentCheckOutcome.Unknown;
            result.Message = "No spool is assigned to the printer.";
            return result;
        }

        if (spoolmanService is null)
        {
            result.Outcome = FilamentCheckOutcome.Unknown;
            result.Message =
                "Spoolman is unavailable, so the assigned spool could not be verified.";
            return result;
        }

        try
        {
            SpoolmanSpoolDto? spool = await spoolmanService.GetSpoolByIdAsync(
                printer.CurrentSpoolId.Value,
                ct);
            if (spool is null)
            {
                result.Outcome = FilamentCheckOutcome.Unknown;
                result.Message =
                    $"Spool {printer.CurrentSpoolId.Value} data is unavailable.";
                return result;
            }

            result.RemainingWeightG = spool.RemainingWeightG;
            result.LoadedMaterial = spool.Material;

            if (string.IsNullOrWhiteSpace(job.RequiredMaterialType))
            {
                result.Outcome = FilamentCheckOutcome.Unknown;
                result.Message =
                    "The queued job does not specify a required filament material.";
                return result;
            }

            if (string.IsNullOrWhiteSpace(spool.Material))
            {
                result.Outcome = FilamentCheckOutcome.Unknown;
                result.Message =
                    "The assigned spool does not specify a filament material.";
                return result;
            }

            if (!string.Equals(
                    job.RequiredMaterialType,
                    spool.Material,
                    StringComparison.OrdinalIgnoreCase))
            {
                result.Outcome = FilamentCheckOutcome.Incompatible;
                result.MaterialMismatch = true;
                result.Message =
                    $"Material mismatch: loaded {spool.Material}, job requires {job.RequiredMaterialType}";
                return result;
            }

            if (!job.EstimatedFilamentUsage.HasValue)
            {
                result.Outcome = FilamentCheckOutcome.Unknown;
                result.Message =
                    "The queued job does not include an estimated filament requirement.";
                return result;
            }

            if (!spool.RemainingWeightG.HasValue)
            {
                result.Outcome = FilamentCheckOutcome.Unknown;
                result.Message =
                    "The assigned spool does not include a remaining filament weight.";
                return result;
            }

            result.Sufficient =
                spool.RemainingWeightG.Value >= job.EstimatedFilamentUsage.Value;
            result.Outcome = result.Sufficient
                ? FilamentCheckOutcome.Compatible
                : FilamentCheckOutcome.Incompatible;
            result.Message = result.Sufficient
                ? $"Filament OK: {spool.RemainingWeightG:F1}g remaining, {job.EstimatedFilamentUsage:F1}g required"
                : $"Insufficient filament: {spool.RemainingWeightG:F1}g remaining, {job.EstimatedFilamentUsage:F1}g required";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "[AutoDispatchReadyGate] Filament check failed for printer {PrinterId}",
                printer.Id);
            result.Outcome = FilamentCheckOutcome.Unknown;
            result.Sufficient = false;
            result.Message =
                "Filament verification failed because Spoolman could not be reached.";
        }

        return result;
    }

    /// <summary>
    /// Produces a stable fingerprint of every operator-visible filament-check field.
    /// </summary>
    public static byte[] ComputeVersion(FilamentCheckResult check)
    {
        ArgumentNullException.ThrowIfNull(check);

        string remainingWeight = check.RemainingWeightG?.ToString(
            "R",
            CultureInfo.InvariantCulture) ?? string.Empty;
        string requiredWeight = check.RequiredWeightG?.ToString(
            "R",
            CultureInfo.InvariantCulture) ?? string.Empty;
        string canonical = string.Join(
            "\u001f",
            ((int)check.Outcome).ToString(CultureInfo.InvariantCulture),
            check.Sufficient ? "1" : "0",
            check.MaterialMismatch ? "1" : "0",
            check.LoadedMaterial ?? string.Empty,
            check.RequiredMaterial ?? string.Empty,
            remainingWeight,
            requiredWeight,
            check.Message ?? string.Empty);
        return SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
    }
}
