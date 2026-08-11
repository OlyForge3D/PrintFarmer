using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Farm.Infrastructure.PrinterCalibration;

/// <summary>
/// Exact request accepted by the slicer-host calibration profile resolution endpoint.
/// </summary>
/// <remarks>
/// The caller supplies identifiers only. Ownership scope is never accepted from the request body —
/// the slicer host derives <see cref="CalibrationProfileAccessScope"/> from its own validated JWT.
/// </remarks>
public sealed record ResolveCalibrationProfilesRequest(
    [property: JsonPropertyName("machineProfileId")] Guid MachineProfileId,
    [property: JsonPropertyName("processProfileId")] Guid ProcessProfileId,
    [property: JsonPropertyName("filamentProfileId")] Guid FilamentProfileId);

/// <summary>
/// Stable wire contract shared by the main API resolver adapter and the slicer-host endpoint that
/// serves it. Keeping the route, size bound and request shape in one place stops the two sides from
/// drifting and keeps request validation identical on both ends of the hop.
/// </summary>
public static class CalibrationProfileResolutionContract
{
    /// <summary>Controller route prefix of the slicer-host resolution endpoint.</summary>
    public const string RoutePrefix = "api/slicer/calibration";

    /// <summary>Action segment of the slicer-host resolution endpoint.</summary>
    public const string ResolveActionRoute = "resolved-profiles";

    /// <summary>Fixed relative route the main API adapter posts to. Never caller-controlled.</summary>
    public const string ResolveRelativeRoute = RoutePrefix + "/" + ResolveActionRoute;

    /// <summary>Relative route of the no-data resolver availability probe.</summary>
    public const string HealthRelativeRoute = "healthz/calibration-resolver";

    /// <summary>Registered name of the slicer-host resolver health check.</summary>
    public const string HealthCheckName = "calibration-profile-resolver";

    /// <summary>Tag used to expose only the resolver probe on its dedicated route.</summary>
    public const string HealthCheckTag = "calibration-resolver";

    /// <summary>
    /// Hard upper bound for the resolution request body. Three GUIDs never approach this, so a
    /// larger body is rejected before it is buffered.
    /// </summary>
    public const int MaxRequestBodyBytes = 1024;

    /// <summary>Stable problem code returned when the request is not exactly three profile ids.</summary>
    public const string InvalidRequestCode = "invalid_profile_resolution_request";

    /// <summary>Stable problem code returned when the local profile store cannot be queried.</summary>
    public const string ResolverUnavailableCode = "profile_service_unavailable";

    private const string MachineProperty = "machineProfileId";
    private const string ProcessProperty = "processProfileId";
    private const string FilamentProperty = "filamentProfileId";

    /// <summary>Property names accepted by the endpoint, in canonical order.</summary>
    public static IReadOnlyList<string> RequiredProperties { get; } =
        [MachineProperty, ProcessProperty, FilamentProperty];

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

    /// <summary>
    /// Validates that <paramref name="root"/> is exactly the three-GUID request and nothing else.
    /// </summary>
    /// <param name="root">The parsed request body.</param>
    /// <param name="request">The validated request when parsing succeeds.</param>
    /// <returns><see langword="true"/> when the body matches the contract exactly.</returns>
    /// <remarks>
    /// Unknown members, duplicate members, non-string values, malformed GUIDs and empty GUIDs are all
    /// rejected. In particular, a caller cannot smuggle <c>userId</c> or <c>bypassOwnership</c> in.
    /// </remarks>
    public static bool TryParseRequest(
        JsonElement root,
        [NotNullWhen(true)] out ResolveCalibrationProfilesRequest? request)
    {
        request = null;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        int propertyCount = 0;
        Guid? machineId = null;
        Guid? processId = null;
        Guid? filamentId = null;
        foreach (JsonProperty property in root.EnumerateObject())
        {
            propertyCount++;
            if (propertyCount > RequiredProperties.Count)
            {
                return false;
            }

            if (!TryReadGuid(property.Value, out Guid value))
            {
                return false;
            }

            switch (property.Name)
            {
                case MachineProperty when machineId is null:
                    machineId = value;
                    break;
                case ProcessProperty when processId is null:
                    processId = value;
                    break;
                case FilamentProperty when filamentId is null:
                    filamentId = value;
                    break;
                default:
                    return false;
            }
        }

        if (machineId is null || processId is null || filamentId is null)
        {
            return false;
        }

        request = new ResolveCalibrationProfilesRequest(
            machineId.Value,
            processId.Value,
            filamentId.Value);
        return true;
    }

    private static bool TryReadGuid(JsonElement element, out Guid value)
    {
        value = Guid.Empty;
        return element.ValueKind == JsonValueKind.String &&
               element.TryGetGuid(out value) &&
               value != Guid.Empty;
    }
}
