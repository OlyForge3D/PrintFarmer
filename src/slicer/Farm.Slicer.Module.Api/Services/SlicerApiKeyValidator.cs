using System.Security.Cryptography;
using System.Text;
using Farm.Slicer.Module.Api.Filters;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services.Configuration;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.Api.Services;

/// <summary>Validates shared registration keys and service-bound lifecycle keys.</summary>
public sealed class SlicerApiKeyValidator(
    IConfiguration configuration,
    IHostEnvironment environment,
    ISlicersRepository slicersRepository,
    ILogger<SlicerApiKeyValidator> logger) : ISlicerApiKeyValidator
{
    private readonly string? _sharedKey = FirstNonBlank(
        configuration[$"{WorkerAuthSettings.SectionName}:SharedKey"],
        configuration[$"{WorkerAuthSettings.SectionName}:SharedApiKey"],
        configuration["SlicerRegistry:ApiKey"],
        Environment.GetEnvironmentVariable("WORKER_SHARED_API_KEY"),
        Environment.GetEnvironmentVariable("SLICER_REGISTRATION_KEY"));

    private readonly IHostEnvironment _environment = environment;
    private readonly ISlicersRepository _slicersRepository = slicersRepository;
    private readonly ILogger<SlicerApiKeyValidator> _logger = logger;

    /// <inheritdoc />
    public Task<bool> ValidateSharedKeyAsync(
        string? apiKey,
        CancellationToken ct = default)
    {
        _ = ct;
        if (string.IsNullOrWhiteSpace(_sharedKey))
        {
            bool bypass = _environment.IsDevelopment() || _environment.IsEnvironment("Testing");
            if (bypass)
            {
                _logger.LogWarning(
                    "No slicer shared API key is configured; allowing registration only in {EnvironmentName}.",
                    _environment.EnvironmentName);
            }

            return Task.FromResult(bypass);
        }

        return Task.FromResult(FixedTimeEquals(_sharedKey, apiKey));
    }

    /// <inheritdoc />
    public async Task<bool> ValidateServiceKeyAsync(
        Guid serviceId,
        string? apiKey,
        CancellationToken ct = default)
    {
        SlicerService? service = await _slicersRepository.GetByIdAsync(serviceId, ct);
        return service is not null && FixedTimeEquals(service.ApiKey, apiKey);
    }

    private static bool FixedTimeEquals(string? expected, string? presented)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(presented))
        {
            return false;
        }

        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
        byte[] presentedBytes = Encoding.UTF8.GetBytes(presented);
        return expectedBytes.Length == presentedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, presentedBytes);
    }

    private static string? FirstNonBlank(params string?[] candidates) =>
        candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
}
