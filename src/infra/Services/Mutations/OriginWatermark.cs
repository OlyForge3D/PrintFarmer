using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Mutations;

/// <summary>
/// Captures and combines nullable mutation-watermark provenance.
/// </summary>
public static class OriginWatermark
{
    /// <summary>
    /// Captures the committed watermark before an observation begins.
    /// </summary>
    public static async Task<long?> CaptureAsync(
        IMutationWatermarkReader? reader,
        ILogger logger,
        string source,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (reader is null)
        {
            return null;
        }

        try
        {
            return await reader.GetCurrentAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Unable to capture mutation watermark before observing {Source}; provenance is unavailable",
                source);
            return null;
        }
    }

    /// <summary>
    /// Returns the oldest proven origin, or null when any required input is unproven.
    /// </summary>
    public static long? Combine(params long?[] origins)
    {
        ArgumentNullException.ThrowIfNull(origins);
        return origins.Length == 0 || origins.Any(origin => !origin.HasValue)
            ? null
            : origins.Min(origin => origin!.Value);
    }
}
