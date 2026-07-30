using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Normalization;

namespace Farm.Infrastructure.Services.Spoolman;

/// <summary>
/// Identifies the namespace that owns a Spoolman spool identifier.
/// </summary>
public enum SpoolSourceKind
{
    /// <summary>The centrally configured Spoolman server.</summary>
    Central = 0,

    /// <summary>A Moonraker printer's native Spoolman server.</summary>
    MoonrakerNative = 1,
}

/// <summary>
/// Immutable source-qualified identity for one spool.
/// </summary>
public readonly record struct CanonicalSpoolIdentity
{
    /// <summary>Maximum persisted length of a normalized source identity.</summary>
    public const int MaxSourceIdentityLength = 256;

    /// <summary>
    /// Creates a canonical source-qualified spool identity.
    /// </summary>
    public CanonicalSpoolIdentity(
        SpoolSourceKind sourceKind,
        string sourceIdentity,
        int spoolId)
    {
        if (!Enum.IsDefined(sourceKind))
        {
            throw new ArgumentOutOfRangeException(nameof(sourceKind));
        }

        if (spoolId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(spoolId), "Spool ID must be positive.");
        }

        string normalizedIdentity = NormalizeSourceIdentity(sourceIdentity);
        if (normalizedIdentity.Length > MaxSourceIdentityLength)
        {
            throw new ArgumentException(
                $"Normalized source identity cannot exceed {MaxSourceIdentityLength} characters.",
                nameof(sourceIdentity));
        }

        SourceKind = sourceKind;
        SourceIdentity = normalizedIdentity;
        SpoolId = spoolId;
    }

    /// <summary>The source namespace kind.</summary>
    public SpoolSourceKind SourceKind { get; }

    /// <summary>The normalized source URL.</summary>
    public string SourceIdentity { get; }

    /// <summary>The numeric spool ID within the source namespace.</summary>
    public int SpoolId { get; }

    /// <summary>
    /// Creates an identity from the source currently owned by a printer.
    /// </summary>
    public static CanonicalSpoolIdentity? FromPrinter(
        Printer printer,
        int spoolId,
        string? centralSpoolmanBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(printer);

        string? sourceIdentity;
        SpoolSourceKind sourceKind;
        if (printer.Backend == (int)PrinterBackend.Moonraker)
        {
            sourceKind = SpoolSourceKind.MoonrakerNative;
            sourceIdentity = printer.ServerUrl;
        }
        else
        {
            sourceKind = SpoolSourceKind.Central;
            sourceIdentity = centralSpoolmanBaseUrl;
        }

        if (string.IsNullOrWhiteSpace(sourceIdentity))
        {
            return null;
        }

        try
        {
            return new CanonicalSpoolIdentity(sourceKind, sourceIdentity, spoolId);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Normalizes equivalent source URLs while preserving case-sensitive paths.
    /// </summary>
    public static string NormalizeSourceIdentity(string sourceIdentity)
    {
        Uri sourceUri = UrlNormalizer.EnsureBaseUri(sourceIdentity);
        if (sourceUri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException(
                "Spool source identity must use HTTP or HTTPS.",
                nameof(sourceIdentity));
        }

        UriBuilder builder = new(sourceUri)
        {
            Scheme = sourceUri.Scheme.ToLowerInvariant(),
            Host = sourceUri.IdnHost.ToLowerInvariant(),
            Query = string.Empty,
            Fragment = string.Empty,
        };
        if (sourceUri.IsDefaultPort)
        {
            builder.Port = -1;
        }

        return builder.Uri
            .GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped)
            .TrimEnd('/');
    }
}
