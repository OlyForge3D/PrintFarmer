using System.Data.Common;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Repositories.Settings;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Services.HostUpdates;

/// <summary>
/// Persists the immutable association between a canonical release ID and the exact signed
/// manifest digest accepted for it. Records use the existing generic application-settings
/// table under internal keys and are not exposed as an editable settings section. The binding
/// survives an ordinary process restart, but this generic database persistence cannot detect an
/// older database restoration or replayed settings storage; issues #2666 and #2663 own protected
/// anti-replay continuity.
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
        AppSettingsEntity? existing = await ReadAsync(key, cancellationToken);
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
        if (await CreateAsync(key, json, cancellationToken).ConfigureAwait(false) == AppSettingsCreateResult.Created)
        {
            return;
        }

        AppSettingsEntity? raced = await ReadAsync(key, cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException($"Manifest binding race for release '{releaseId}' could not be resolved.");

        ManifestBinding? racedBinding;
        try
        {
            racedBinding = JsonSerializer.Deserialize<ManifestBinding>(raced.SettingsJson);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Persisted manifest binding for release '{releaseId}' is invalid.",
                ex);
        }

        if (racedBinding is null || racedBinding.ReleaseId != releaseId || string.IsNullOrWhiteSpace(racedBinding.ManifestDigest))
        {
            throw new InvalidDataException($"Persisted manifest binding for release '{releaseId}' is invalid.");
        }

        if (racedBinding.ManifestDigest != manifestDigest)
        {
            throw new InvalidDataException($"Manifest digest conflict for immutable release '{releaseId}'.");
        }
    }

    private async Task<AppSettingsEntity?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            return await _repository.GetReadOnlyAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException or IOException or UnauthorizedAccessException or SecurityException)
        {
            throw new HostUpdateSubsystemUnavailableException(
                "host_update_manifest_binding_database_unavailable", exception);
        }
    }

    private async Task<AppSettingsCreateResult> CreateAsync(string key, string json, CancellationToken cancellationToken)
    {
        try
        {
            return await _repository.TryCreateDetailedAsync(key, json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException or IOException or UnauthorizedAccessException or SecurityException)
        {
            throw new HostUpdateSubsystemUnavailableException(
                "host_update_manifest_binding_database_unavailable", exception);
        }
    }

    private sealed record ManifestBinding(string ReleaseId, string ManifestDigest);
}
