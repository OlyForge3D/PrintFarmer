using System.Text.Json;

namespace Farm.Slicer.Module.Domain;

/// <summary>
/// Authoritative claim identity derived from the worker's persisted registration.
/// </summary>
public sealed record WorkerClaimIdentity
{
    private static readonly string[] PlaceholderVersions = ["current", "latest", "unknown"];

    private WorkerClaimIdentity(
        Guid workerId,
        string[] capabilities,
        string? version,
        string? distribution,
        string? containerDigest,
        string? binarySha256,
        bool isAttested)
    {
        WorkerId = workerId;
        Capabilities = capabilities;
        Version = version;
        Distribution = distribution;
        ContainerDigest = containerDigest;
        BinarySha256 = binarySha256;
        IsAttested = isAttested;
    }

    public Guid WorkerId { get; }

    public string[] Capabilities { get; }

    public string? Version { get; }

    public string? Distribution { get; }

    public string? ContainerDigest { get; }

    public string? BinarySha256 { get; }

    public bool IsAttested { get; }

    /// <summary>Builds an identity for an in-process queue that has no persisted attestation.</summary>
    public static WorkerClaimIdentity CreateUnattested(Guid workerId, string[]? capabilities) =>
        new(
            workerId,
            NormalizeCapabilities(capabilities ?? []),
            null,
            null,
            null,
            null,
            isAttested: false);

    /// <summary>Builds an identity exclusively from a registered worker row.</summary>
    public static WorkerClaimIdentity FromRegisteredWorker(Worker worker)
    {
        ArgumentNullException.ThrowIfNull(worker);

        string[] capabilities = [];
        string? attestedVersion = null;
        string? engineVersion = null;
        string? distribution = null;
        string? containerDigest = null;
        string? binarySha256 = null;
        bool realBinary = false;

        try
        {
            using JsonDocument document = JsonDocument.Parse(worker.CapabilitiesJson);
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                capabilities = ReadCapabilities(root);
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                capabilities = root.TryGetProperty("capabilities", out JsonElement values)
                    ? ReadCapabilities(values)
                    : [];
                attestedVersion = ReadString(root, "slicerVersion");
                engineVersion = ReadString(root, "engineVersion");
                distribution = ReadString(root, "slicerDistribution");
                containerDigest = ReadString(root, "slicerContainerDigest");
                binarySha256 = ReadString(root, "slicerBinarySha256");
                realBinary = root.TryGetProperty("realBinary", out JsonElement realBinaryValue) &&
                    realBinaryValue.ValueKind is JsonValueKind.True;
            }
        }
        catch (JsonException)
        {
            capabilities = [];
        }

        string? registeredVersion = Normalize(worker.Version);
        bool versionIsConcrete =
            registeredVersion is not null &&
            !PlaceholderVersions.Contains(registeredVersion, StringComparer.OrdinalIgnoreCase);
        bool isAttested =
            versionIsConcrete &&
            string.Equals(registeredVersion, attestedVersion, StringComparison.Ordinal) &&
            string.Equals(registeredVersion, engineVersion, StringComparison.Ordinal) &&
            distribution is not null &&
            IsContainerDigest(containerDigest) &&
            IsSha256(binarySha256) &&
            realBinary;

        return new WorkerClaimIdentity(
            worker.Id,
            capabilities,
            registeredVersion,
            distribution,
            containerDigest,
            binarySha256,
            isAttested);
    }

    private static string[] ReadCapabilities(JsonElement element) =>
        element.ValueKind == JsonValueKind.Array
            ? NormalizeCapabilities(element
                .EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString()!))
            : [];

    private static string[] NormalizeCapabilities(IEnumerable<string> capabilities) =>
        capabilities
            .Select(Normalize)
            .Where(capability => capability is not null)
            .Select(capability => capability!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? Normalize(value.GetString())
            : null;

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsContainerDigest(string? value) =>
        value is not null &&
        value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) &&
        IsSha256(value["sha256:".Length..]);

    private static bool IsSha256(string? value) =>
        value is not null &&
        value.Length == 64 &&
        value.All(Uri.IsHexDigit);
}
