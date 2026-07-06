using System.Security.Cryptography;
using System.Text;
using Farm.Slicer.Module.Api.Filters;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.Api.Services;

/// <summary>
/// Validates slicer registry shared keys and per-service worker keys.
/// </summary>
public sealed class SlicerApiKeyValidator(
    IConfiguration configuration,
    SlicerDbContext db,
    IHostEnvironment env,
    ILogger<SlicerApiKeyValidator> logger) : ISlicerApiKeyValidator
{
    private readonly string? _sharedKey = FirstNonBlank(
        configuration.GetSection(WorkerAuthSettings.SectionName)["SharedKey"],
        configuration.GetSection(WorkerAuthSettings.SectionName)["SharedApiKey"],
        configuration["SlicerRegistry:ApiKey"],
        Environment.GetEnvironmentVariable("WORKER_SHARED_API_KEY"),
        Environment.GetEnvironmentVariable("SLICER_REGISTRATION_KEY"));

    private readonly SlicerDbContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly IHostEnvironment _env = env ?? throw new ArgumentNullException(nameof(env));
    private readonly ILogger<SlicerApiKeyValidator> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public Task<bool> ValidateSharedKeyAsync(string? apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_sharedKey))
        {
            bool bypass = _env.IsDevelopment() || _env.IsEnvironment("Testing");
            if (bypass)
            {
                _logger.LogWarning("No slicer shared API key is configured; allowing request only because environment is {EnvironmentName}.", _env.EnvironmentName);
            }

            return Task.FromResult(bypass);
        }

        return Task.FromResult(FixedTimeEquals(apiKey, _sharedKey));
    }

    /// <inheritdoc />
    public async Task<bool> ValidateServiceKeyAsync(string? apiKey, Guid? serviceId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || serviceId is null)
        {
            return false;
        }

        SlicerService? service = await _db.SlicerServices
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == serviceId.Value, ct);

        return FixedTimeEquals(apiKey, service?.ApiKey);
    }

    private static bool FixedTimeEquals(string? presented, string? expected)
    {
        if (string.IsNullOrWhiteSpace(presented) || string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        byte[] presentedBytes = Encoding.UTF8.GetBytes(presented);
        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
        return presentedBytes.Length == expectedBytes.Length
            && CryptographicOperations.FixedTimeEquals(presentedBytes, expectedBytes);
    }

    private static string? FirstNonBlank(params string?[] candidates)
    {
        return candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
    }
}
