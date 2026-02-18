using InfraIRateLimit = Farm.Infrastructure.Services.RateLimiting.IRateLimitService;
using InfraResult = Farm.Infrastructure.Services.RateLimiting.RateLimitResult;
using ModuleIRateLimit = Farm.Slicer.Module.Services.IRateLimitService;
using ModuleResult = Farm.Slicer.Module.Services.SlicerRateLimitResult;

namespace Farm.Web.Api.Services.Adapters;

/// <summary>
/// Bridges <see cref="Farm.Slicer.Module.Services.IRateLimitService"/> (module) to the
/// infrastructure's <see cref="Farm.Infrastructure.Services.RateLimiting.IRateLimitService"/>
/// for slice-job rate limiting. The generic <c>CheckAsync(key)</c> delegates to
/// <see cref="InfraIRateLimit.CheckSliceJobSubmitLimitAsync"/> when the key contains a parseable user ID.
/// </summary>
internal sealed class ModuleRateLimitAdapter(InfraIRateLimit infraService) : ModuleIRateLimit
{
    private readonly InfraIRateLimit _infraService = infraService ?? throw new ArgumentNullException(nameof(infraService));

    /// <inheritdoc />
    public async Task<ModuleResult> CheckAsync(string key, CancellationToken ct = default)
    {
        // Keys follow the pattern "slice-job:{userId}" — extract the GUID suffix.
        Guid userId = ExtractUserId(key);

        InfraResult infraResult = await _infraService.CheckSliceJobSubmitLimitAsync(userId, ct);

        int? retryAfterSeconds = infraResult.RetryAfter.HasValue
            ? (int)Math.Ceiling(infraResult.RetryAfter.Value.TotalSeconds)
            : null;

        return new ModuleResult(infraResult.IsAllowed, retryAfterSeconds);
    }

    private static Guid ExtractUserId(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return Guid.Empty;
        }

        // Try to extract the last segment after ':'
        int colonIndex = key.LastIndexOf(':');
        string candidate = colonIndex >= 0 ? key[(colonIndex + 1)..] : key;

        return Guid.TryParse(candidate, out Guid userId) ? userId : Guid.Empty;
    }
}
