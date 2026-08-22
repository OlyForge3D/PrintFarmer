using System.Text.Json;
using System.Text.Json.Serialization;

namespace Farm.Infrastructure.PrinterCalibration;

/// <summary>
/// The pinned upstream slicer identity a registered worker actually attested, as reported over the
/// wire by the slicer host that owns the worker registry.
/// </summary>
/// <param name="Version">Reported slicer version.</param>
/// <param name="Distribution">Reported slicer distribution.</param>
/// <param name="ContainerDigest">Reported container digest of the pinned image.</param>
/// <param name="BinarySha256">Reported digest of the pinned slicer binary.</param>
/// <param name="WorkerId">Worker that published the attestation.</param>
public sealed record WorkerCompatibilityPinnedIdentityDto(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("distribution")] string Distribution,
    [property: JsonPropertyName("containerDigest")] string ContainerDigest,
    [property: JsonPropertyName("binarySha256")] string BinarySha256,
    [property: JsonPropertyName("workerId")] Guid WorkerId);

/// <summary>
/// Wire snapshot of worker/version compatibility served by the slicer host's internal capability
/// endpoint (issue #1848). Mirrors the shape the main API would build itself from a local
/// <c>IDbContextFactory&lt;SlicerDbContext&gt;</c> in a monolith deployment, so the probe can treat
/// both topologies identically once it has one of these.
/// </summary>
/// <param name="PinnedIdentity">
/// The eligible worker's attested identity, or <see langword="null"/> when no registered worker is
/// online, in good standing and attesting an allow-listed upstream slicer identity.
/// </param>
/// <param name="ObservedVersions">
/// Distinct, ordered upstream OrcaSlicer versions currently attested by fresh, online services.
/// </param>
/// <param name="HasSupportedVersion">
/// Whether at least one observed version is within the configured supported allow-list.
/// </param>
public sealed record WorkerCompatibilitySnapshotDto(
    [property: JsonPropertyName("pinnedIdentity")] WorkerCompatibilityPinnedIdentityDto? PinnedIdentity,
    [property: JsonPropertyName("observedVersions")] IReadOnlyList<string> ObservedVersions,
    [property: JsonPropertyName("hasSupportedVersion")] bool HasSupportedVersion)
{
    /// <summary>The empty snapshot returned when nothing is eligible or the probe could not run.</summary>
    public static WorkerCompatibilitySnapshotDto Empty { get; } = new(null, [], false);
}

/// <summary>
/// Stable wire contract shared by the main API's split-deployment worker-compatibility client and the
/// slicer-host endpoint that serves it (issue #1848). Keeping the route, query parameter name, header
/// name and response shape in one place stops the two sides from drifting.
/// </summary>
public static class WorkerCompatibilityContract
{
    /// <summary>Controller route prefix of the slicer-host worker-compatibility endpoint.</summary>
    public const string RoutePrefix = "api/internal/capabilities";

    /// <summary>Action segment of the slicer-host worker-compatibility endpoint.</summary>
    public const string WorkerCompatibilityActionRoute = "worker-compatibility";

    /// <summary>Fixed relative route the main API client requests. Never caller-controlled.</summary>
    public const string WorkerCompatibilityRelativeRoute =
        RoutePrefix + "/" + WorkerCompatibilityActionRoute;

    /// <summary>Query parameter naming an optional exact slicer version to require.</summary>
    public const string RequiredSlicerVersionQueryParam = "requiredSlicerVersion";

    /// <summary>
    /// Header carrying the shared worker-authentication key on this internal hop. Must match
    /// <c>RequireSlicerApiKeyAttribute.HeaderName</c>, which the main API cannot reference directly
    /// (it does not compile-time-reference <c>Farm.Slicer.Module.Api</c>).
    /// </summary>
    public const string ApiKeyHeaderName = "X-Slicer-ApiKey";

    /// <summary>
    /// Hard upper bound on the response buffered from the slicer host. The snapshot is a handful of
    /// short strings and a GUID, so this bound is generous while still preventing unbounded allocation.
    /// </summary>
    public const int MaxResponseBytes = 64 * 1024;

    /// <summary>
    /// Serializer options used on both sides of the hop: camelCase, no case-insensitive matching and
    /// no tolerance for members the contract does not define.
    /// </summary>
    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        MaxDepth = 8,
    };
}
