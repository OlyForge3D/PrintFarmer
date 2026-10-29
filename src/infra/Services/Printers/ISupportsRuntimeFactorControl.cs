using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>Independent runtime overrides; not persistent slicer or printer configuration.</summary>
public interface ISupportsRuntimeFactorControl
{
    /// <summary>Sets the speed override percentage without changing flow.</summary>
    Task<bool> SetSpeedFactorAsync(string baseUrl, double percent, PrinterCredential? credential, CancellationToken ct = default);

    /// <summary>Sets the extrusion override percentage without changing speed.</summary>
    Task<bool> SetFlowFactorAsync(string baseUrl, double percent, PrinterCredential? credential, CancellationToken ct = default);
}

/// <summary>Application write policy, deliberately narrower than firmware's positive-number range.</summary>
public static class PrinterRuntimeFactorLimits
{
    public const double MinimumPercent = 1;
    public const double MaximumPercent = 200;

    /// <summary>Accepts finite percentages with at most two decimal places.</summary>
    public static bool IsValid(double percent) =>
        double.IsFinite(percent) &&
        percent is >= MinimumPercent and <= MaximumPercent &&
        decimal.Round((decimal)percent, 2) == (decimal)percent;
}
