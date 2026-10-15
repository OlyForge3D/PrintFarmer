using System.Text.Json.Serialization;
using Farm.Infrastructure.Domain;

namespace Farm.Modules.PrintQueue.Controllers.Responses;

/// <summary>
/// Authenticated same-origin camera proxy routes for a printer.
/// </summary>
// URL-like values are represented as strings for transport compatibility.
#pragma warning disable CA1056 // URI-like properties should not be strings
public sealed record CameraUrlResult
{
    private const string PrinterRoutePrefix = "/api/printers/";
    private const string SnapshotRouteSuffix = "/camera/snapshot";
    private const string StreamRouteSuffix = "/camera/stream";

    public CameraUrlResult(string? streamUrl, string? snapshotUrl)
    {
        StreamUrl = ValidateProxyRoute(streamUrl, StreamRouteSuffix, nameof(streamUrl));
        SnapshotUrl = ValidateProxyRoute(snapshotUrl, SnapshotRouteSuffix, nameof(snapshotUrl));
    }

    public string? StreamUrl { get; }

    public string? SnapshotUrl { get; }

    /// <summary>
    /// Optional, additive contract hint for the printer's primary camera. Omitted when unknown so
    /// older clients that only decode the proxy URLs keep working.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CameraAccessMode? AccessMode { get; init; }

    /// <summary>Optional stream transport of the primary camera (string enum).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CameraStreamFormat? StreamFormat { get; init; }

    /// <summary>Optional snapshot acquisition strategy of the primary camera (string enum).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CameraSnapshotStrategy? SnapshotStrategy { get; init; }

    public void Deconstruct(out string? streamUrl, out string? snapshotUrl)
    {
        streamUrl = StreamUrl;
        snapshotUrl = SnapshotUrl;
    }

    private static string? ValidateProxyRoute(
        string? value,
        string expectedSuffix,
        string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        ReadOnlySpan<char> route = value.AsSpan();
        if (!route.StartsWith(PrinterRoutePrefix, StringComparison.Ordinal) ||
            !route.EndsWith(expectedSuffix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Camera routes must be relative PrintFarmer printer API routes.",
                parameterName);
        }

        ReadOnlySpan<char> printerId = route[
            PrinterRoutePrefix.Length..^expectedSuffix.Length];
        if (!Guid.TryParseExact(printerId, "D", out _))
        {
            throw new ArgumentException(
                "Camera routes must be relative PrintFarmer printer API routes.",
                parameterName);
        }

        return value;
    }
}
#pragma warning restore CA1056
