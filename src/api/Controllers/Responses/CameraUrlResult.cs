namespace Farm.Web.Api.Controllers.Responses;

// URL-like values are represented as strings by design for transport compatibility.
#pragma warning disable CA1056 // URI-like properties should not be strings
public sealed record CameraUrlResult(string? StreamUrl, string? SnapshotUrl);
#pragma warning restore CA1056
