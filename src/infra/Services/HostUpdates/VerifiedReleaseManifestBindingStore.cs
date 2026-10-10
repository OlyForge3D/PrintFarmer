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

        string key = KeyFor(releaseId);
        AppSettingsEntity? existing = await ReadAsync(key, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (ParseDigest(releaseId, existing.SettingsJson) != manifestDigest)
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

        if (ParseDigest(releaseId, raced.SettingsJson) != manifestDigest)
        {
            throw new InvalidDataException($"Manifest digest conflict for immutable release '{releaseId}'.");
        }
    }

    /// <summary>The application-settings key holding the binding for <paramref name="releaseId"/>.</summary>
    public static string KeyFor(string releaseId) =>
        KeyPrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(releaseId))).ToLowerInvariant();

    /// <summary>Validates a persisted binding for <paramref name="releaseId"/> and returns its manifest digest.</summary>
    public static string ParseDigest(string releaseId, string settingsJson)
    {
        ManifestBinding? binding;
        try
        {
            binding = JsonSerializer.Deserialize<ManifestBinding>(settingsJson);
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

        return binding.ManifestDigest;
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
