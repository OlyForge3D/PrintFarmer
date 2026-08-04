using System.Security.Cryptography;
using System.Text;
using Farm.Slicer.Module.Api.Filters;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services.Configuration;

namespace Farm.Slicer.Module.Api.Services;

/// <summary>Validates shared registration keys and service-bound lifecycle keys.</summary>
public sealed class SlicerApiKeyValidator(
    IConfiguration configuration,
    ISlicersRepository slicersRepository) : ISlicerApiKeyValidator
{
    private readonly string? _sharedKey =
        WorkerAuthConfiguration.ResolveSharedKey(configuration)?.Value;

    private readonly ISlicersRepository _slicersRepository = slicersRepository;

    /// <inheritdoc />
    public Task<bool> ValidateSharedKeyAsync(
        string? apiKey,
        CancellationToken ct = default)
    {
        _ = ct;
        if (string.IsNullOrWhiteSpace(_sharedKey))
        {
            return Task.FromResult(false);
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
}
