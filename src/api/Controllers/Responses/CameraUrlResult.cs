namespace Farm.Web.Api.Controllers.Responses;

/// <summary>
/// Authenticated same-origin camera proxy routes for a printer.
/// </summary>
// URL-like values are represented as strings for transport compatibility.
#pragma warning disable CA1056 // URI-like properties should not be strings
public sealed record CameraUrlResult
{
    public CameraUrlResult(string? streamUrl, string? snapshotUrl)
    {
        StreamUrl = ValidateProxyRoute(streamUrl, nameof(streamUrl));
        SnapshotUrl = ValidateProxyRoute(snapshotUrl, nameof(snapshotUrl));
    }

    public string? StreamUrl { get; }

    public string? SnapshotUrl { get; }

    public void Deconstruct(out string? streamUrl, out string? snapshotUrl)
    {
        streamUrl = StreamUrl;
        snapshotUrl = SnapshotUrl;
    }

    private static string? ValidateProxyRoute(string? value, string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        if (!value.StartsWith("/api/printers/", StringComparison.Ordinal) ||
            Uri.TryCreate(value, UriKind.Absolute, out _))
        {
            throw new ArgumentException(
                "Camera routes must be relative PrintFarmer printer API routes.",
                parameterName);
        }

        return value;
    }
}
#pragma warning restore CA1056
