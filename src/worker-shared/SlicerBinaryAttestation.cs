namespace Farm.Slicer.Worker.Core;

/// <summary>
/// The slicer binary identity a worker is allowed to report at registration.
/// </summary>
/// <param name="BinarySha256">
/// The verified digest of the installed slicer binary, or <see langword="null"/> when this image
/// carries no verified binary.
/// </param>
/// <param name="RealBinary">
/// <see langword="true"/> only when a real, verified binary is installed. A stub image can never
/// report <see langword="true"/>, so the API keeps calibration capabilities false for it.
/// </param>
public sealed record SlicerBinaryIdentity(string? BinarySha256, bool RealBinary)
{
    /// <summary>An image whose installed binary is a stub or cannot be attested.</summary>
    public static SlicerBinaryIdentity Unverified { get; } = new(null, false);
}

/// <summary>
/// Resolves the slicer binary identity a worker reports, from evidence rather than from the build
/// argument that requested it.
/// </summary>
/// <remarks>
/// The image build writes an attestation file next to the installed binary and leaves it empty when
/// the stub fallback replaced the real binary. The declared build argument is never sufficient on its
/// own: it says what the build asked for, not what was installed.
/// </remarks>
public static class SlicerBinaryAttestation
{
    /// <summary>Default location the image build writes the installed binary attestation to.</summary>
    public const string DefaultAttestationPath = "/etc/printfarmer/orcaslicer.sha256";

    private const int DigestLength = 64;

    /// <summary>
    /// Resolves the reportable identity from the build attestation and the installed binary.
    /// </summary>
    /// <param name="attestedSha256">Content of the image attestation file; empty means "stub".</param>
    /// <param name="declaredSha256">
    /// The digest the build argument declared. It is only ever used to detect disagreement with the
    /// attestation, never as the reported identity.
    /// </param>
    /// <param name="realBinaryInstalled">Whether a real, runnable binary is installed in this image.</param>
    /// <returns>The identity the worker may report.</returns>
    public static SlicerBinaryIdentity Resolve(
        string? attestedSha256,
        string? declaredSha256,
        bool realBinaryInstalled)
    {
        if (!realBinaryInstalled)
        {
            // A stub is present: no attestation may promote it to a pinned, real binary.
            return SlicerBinaryIdentity.Unverified;
        }

        string? attested = NormalizeDigest(attestedSha256);
        if (attested is null)
        {
            return SlicerBinaryIdentity.Unverified;
        }

        string? declared = NormalizeDigest(declaredSha256);
        return declared is not null && !string.Equals(declared, attested, StringComparison.Ordinal)
            ? SlicerBinaryIdentity.Unverified
            : new SlicerBinaryIdentity(attested, true);
    }

    /// <summary>
    /// Reads the image attestation file and resolves the reportable identity from it.
    /// </summary>
    /// <param name="attestationPath">Path of the attestation file written by the image build.</param>
    /// <param name="declaredSha256">The digest declared by the build argument.</param>
    /// <param name="realBinaryInstalled">Whether a real, runnable binary is installed in this image.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The identity the worker may report.</returns>
    public static async Task<SlicerBinaryIdentity> ResolveFromFileAsync(
        string? attestationPath,
        string? declaredSha256,
        bool realBinaryInstalled,
        CancellationToken cancellationToken = default)
    {
        string path = string.IsNullOrWhiteSpace(attestationPath) ? DefaultAttestationPath : attestationPath.Trim();
        string? attested = null;
        if (File.Exists(path))
        {
            try
            {
                attested = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // An unreadable attestation is treated exactly like a missing one: unverified.
                attested = null;
            }
            catch (UnauthorizedAccessException)
            {
                attested = null;
            }
        }

        return Resolve(attested, declaredSha256, realBinaryInstalled);
    }

    /// <summary>Trims a digest and rejects anything that is not a SHA-256 hex string.</summary>
    /// <param name="value">The raw configured or attested value.</param>
    /// <returns>The upper-case digest, or <see langword="null"/> when it is absent or malformed.</returns>
    private static string? NormalizeDigest(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return trimmed.Length != DigestLength || !trimmed.All(Uri.IsHexDigit)
            ? null
            : trimmed.ToUpperInvariant();
    }
}
