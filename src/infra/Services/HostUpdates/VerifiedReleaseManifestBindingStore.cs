using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Repositories.Settings;

namespace Farm.Infrastructure.Services.HostUpdates;

/// <summary>
/// Persists the immutable association between a canonical release ID and the exact signed
/// manifest digest accepted for it. Records use the existing generic application-settings
/// table under internal keys and are not exposed as an editable settings section.
/// </summary>
public interface IVerifiedReleaseManifestBindingStore
{
    /// <summary>
    /// Creates the binding when absent, accepts an identical persisted binding, and rejects a
    /// different digest for the same canonical release ID.
    /// </summary>
    Task EnsureBoundAsync(string releaseId, string manifestDigest, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IVerifiedReleaseManifestBindingStore"/>
public sealed class VerifiedReleaseManifestBindingStore(IAppSettingsRepository repository)
    : IVerifiedReleaseManifestBindingStore
{
    private const string KeyPrefix = "VerifiedReleaseManifestBinding:";
    private readonly IAppSettingsRepository _repository =
        repository ?? throw new ArgumentNullException(nameof(repository));

    /// <inheritdoc/>
    public async Task EnsureBoundAsync(
        string releaseId,
        string manifestDigest,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestDigest);

        string releaseKey = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(releaseId))).ToLowerInvariant();
        string key = KeyPrefix + releaseKey;
        AppSettingsEntity? existing = await _repository.GetReadOnlyAsync(key, cancellationToken);
        if (existing is not null)
        {
            ManifestBinding? binding;
            try
            {
                binding = JsonSerializer.Deserialize<ManifestBinding>(existing.SettingsJson);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(
                    $"Persisted manifest binding for release '{releaseId}' is invalid.",
                    ex);
            }

            if (binding is null
                || binding.ReleaseId != releaseId
                || string.IsNullOrWhiteSpace(binding.ManifestDigest))
            {
                throw new InvalidDataException(
                    $"Persisted manifest binding for release '{releaseId}' is invalid.");
            }

            if (binding.ManifestDigest != manifestDigest)
            {
                throw new InvalidDataException(
                    $"Manifest digest conflict for immutable release '{releaseId}'.");
            }

            return;
        }

        string json = JsonSerializer.Serialize(new ManifestBinding(releaseId, manifestDigest));
        await _repository.SetAsync(key, json, cancellationToken);
        await _repository.SaveChangesAsync(cancellationToken);
    }

    private sealed record ManifestBinding(string ReleaseId, string ManifestDigest);
}
