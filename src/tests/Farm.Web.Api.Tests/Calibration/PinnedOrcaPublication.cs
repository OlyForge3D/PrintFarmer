using System.Text.RegularExpressions;

namespace Farm.Web.Api.Tests.Calibration;

/// <summary>
/// Validates the immutable registry identity of a published pinned OrcaSlicer worker image and
/// decides whether the mandatory pinned-worker smoke gate has to run in the current environment.
/// </summary>
/// <remarks>
/// <para>
/// The publishing workflow resolves the manifest digest from the registry after the push and hands it
/// to the smoke gate. Nothing here invents, shortens or normalises a digest: an identity that is not
/// already an immutable <c>sha256:&lt;64 lowercase hex&gt;</c> manifest digest is refused, because a
/// mutable tag cannot prove which bytes were executed.
/// </para>
/// <para>
/// This type is compiled into both the unit test assembly and
/// <c>Farm.Web.IntegrationTests</c> so the gate the workflow depends on is the same code the focused
/// tests cover.
/// </para>
/// <para>
/// This type was originally authored alongside the calibration generation saga's own pinned-worker
/// smoke test (removed in #1979), but it is generic pinned-worker publication/gate infrastructure
/// with no dependency on the saga itself, and it is still relied on by
/// <c>PinnedOrcaCliRotationTests</c> in <c>Farm.Web.IntegrationTests</c> — so it was relocated here
/// rather than deleted with the rest of the saga.
/// </para>
/// </remarks>
internal static partial class PinnedOrcaPublication
{
    /// <summary>
    /// The xUnit trait category shared by every test that requires the real, pinned OrcaSlicer
    /// worker container to be pullable and runnable in the current environment.
    /// </summary>
    public const string SmokeCategory = "PinnedOrcaSmoke";

    /// <summary>Environment variable carrying the published repository, without tag or digest.</summary>
    public const string ImageVariable = "PRINTFARMER_ORCASLICER_IMAGE";

    /// <summary>Environment variable carrying the registry manifest digest of the published image.</summary>
    public const string ImageDigestVariable = "PRINTFARMER_ORCASLICER_IMAGE_DIGEST";

    /// <summary>Environment variable that makes the smoke gate mandatory rather than advisory.</summary>
    public const string SmokeModeVariable = "PRINTFARMER_ORCA_SMOKE";

    /// <summary>Value of <see cref="SmokeModeVariable"/> that makes the gate mandatory.</summary>
    public const string RequiredSmokeMode = "required";

    /// <summary>
    /// Determines whether a value is an immutable registry manifest digest.
    /// </summary>
    /// <param name="value">The candidate digest.</param>
    /// <returns><see langword="true"/> only for <c>sha256:</c> plus 64 lowercase hex characters.</returns>
    public static bool IsManifestDigest(string? value) =>
        value is not null && ManifestDigestPattern().IsMatch(value);

    /// <summary>
    /// Builds the immutable pull reference for a published repository and its manifest digest.
    /// </summary>
    /// <param name="image">The published repository, without tag or digest.</param>
    /// <param name="digest">The registry manifest digest.</param>
    /// <returns>The <c>repository@sha256:...</c> reference.</returns>
    /// <exception cref="ArgumentException">Thrown when either part is not usable by digest.</exception>
    public static string BuildImageReference(string image, string digest)
    {
        if (DescribeRepositoryProblem(image) is { } repositoryProblem)
        {
            throw new ArgumentException(repositoryProblem, nameof(image));
        }

        return !IsManifestDigest(digest)
            ? throw new ArgumentException(
                "The image identity must be an immutable registry manifest digest of the form " +
                "sha256:<64 lowercase hex characters>.",
                nameof(digest))
            : $"{image.Trim()}@{digest.Trim()}";
    }

    /// <summary>
    /// Resolves the smoke gate from environment configuration.
    /// </summary>
    /// <param name="readEnvironment">Reads a named environment variable.</param>
    /// <returns>The resolved gate, including a concrete blocker when it cannot execute.</returns>
    public static PinnedOrcaSmokeGate ResolveGate(Func<string, string?> readEnvironment)
    {
        ArgumentNullException.ThrowIfNull(readEnvironment);

        string? image = Normalize(readEnvironment(ImageVariable));
        string? digest = Normalize(readEnvironment(ImageDigestVariable));
        bool required = string.Equals(
            Normalize(readEnvironment(SmokeModeVariable)),
            RequiredSmokeMode,
            StringComparison.OrdinalIgnoreCase);

        if (image is null)
        {
            return Blocked(
                required,
                $"{ImageVariable} is not set, so no published pinned worker image is available to run.");
        }

        if (DescribeRepositoryProblem(image) is { } repositoryProblem)
        {
            return Blocked(required, $"{ImageVariable} {repositoryProblem}");
        }

        if (digest is null)
        {
            return Blocked(
                required,
                $"{ImageDigestVariable} is not set, so the published image cannot be executed by digest.");
        }

        return !IsManifestDigest(digest)
            ? Blocked(
                required,
                $"{ImageDigestVariable} is not an immutable registry manifest digest of the form " +
                "sha256:<64 lowercase hex characters>.")
            : new PinnedOrcaSmokeGate(image, digest, required, BlockReason: null);
    }

    /// <summary>
    /// Explains why a repository value cannot be pulled by digest, if it cannot.
    /// </summary>
    /// <param name="image">The candidate repository.</param>
    /// <returns>The problem sentence, or <see langword="null"/> when the value is usable.</returns>
    private static string? DescribeRepositoryProblem(string? image)
    {
        string? candidate = Normalize(image);
        if (candidate is null)
        {
            return "must name a published container repository.";
        }

        if (candidate.Contains('@', StringComparison.Ordinal))
        {
            return "must be the bare repository; the digest is supplied separately so it can be validated.";
        }

        int lastSegment = candidate.LastIndexOf('/');
        string finalSegment = lastSegment < 0 ? candidate : candidate[(lastSegment + 1)..];
        return finalSegment.Contains(':', StringComparison.Ordinal)
            ? "must not carry a mutable tag; the pinned worker is only ever executed by digest."
            : null;
    }

    private static PinnedOrcaSmokeGate Blocked(bool required, string reason) =>
        new(Image: null, Digest: null, required, reason);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ManifestDigestPattern();
}

/// <summary>
/// Whether the pinned-worker smoke gate can run here, and what blocks it when it cannot.
/// </summary>
/// <param name="Image">The published repository, when one was configured and is usable.</param>
/// <param name="Digest">The validated registry manifest digest, when one was configured.</param>
/// <param name="IsRequired">Whether a blocked gate must fail rather than report a blocker.</param>
/// <param name="BlockReason">The concrete reason the gate cannot execute.</param>
internal sealed record PinnedOrcaSmokeGate(
    string? Image,
    string? Digest,
    bool IsRequired,
    string? BlockReason)
{
    /// <summary>Gets whether the published pinned worker can be executed in this environment.</summary>
    public bool CanRun => BlockReason is null;

    /// <summary>Gets the immutable pull reference of the published pinned worker.</summary>
    /// <exception cref="InvalidOperationException">Thrown when the gate is blocked.</exception>
    public string ImageReference => CanRun
        ? PinnedOrcaPublication.BuildImageReference(Image!, Digest!)
        : throw new InvalidOperationException(
            "A blocked pinned worker smoke gate has no immutable image reference: " + BlockReason);

    /// <summary>
    /// Describes the gate for operator-facing test output.
    /// </summary>
    /// <returns>A single line that contains only public registry identity, never a credential.</returns>
    public string Describe() => CanRun
        ? $"Pinned OrcaSlicer worker smoke gate will execute {ImageReference} (required={IsRequired})."
        : $"Pinned OrcaSlicer worker smoke gate did not execute (required={IsRequired}). Blocker: {BlockReason}";
}
