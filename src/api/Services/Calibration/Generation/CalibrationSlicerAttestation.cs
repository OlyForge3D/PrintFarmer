using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Farm.Web.Api.Services.Calibration.Generation;

/// <summary>
/// Reads the pinned upstream slicer build identity out of a registered worker attestation.
/// </summary>
/// <remarks>
/// The attestation is only accepted when the worker publishes both digests. A worker that reports a
/// version but no reproducible build identity is treated as unverifiable, which keeps calibration
/// generation unavailable rather than trusting an unpinned binary.
/// </remarks>
public static class CalibrationSlicerAttestation
{
    /// <summary>Capability property carrying the pinned slicer container digest.</summary>
    public const string ContainerDigestProperty = "slicerContainerDigest";

    /// <summary>Capability property carrying the pinned slicer binary digest.</summary>
    public const string BinaryDigestProperty = "slicerBinarySha256";

    /// <summary>
    /// Attempts to read both pinned digests from a capabilities document.
    /// </summary>
    /// <param name="capabilitiesJson">The capabilities document the worker registered with.</param>
    /// <param name="containerDigest">The container digest when the method returns true.</param>
    /// <param name="binarySha256">The binary digest when the method returns true.</param>
    /// <returns><see langword="true"/> only when both digests are present and non-empty.</returns>
    public static bool TryRead(
        string? capabilitiesJson,
        [NotNullWhen(true)] out string? containerDigest,
        [NotNullWhen(true)] out string? binarySha256)
    {
        containerDigest = null;
        binarySha256 = null;
        if (string.IsNullOrWhiteSpace(capabilitiesJson))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(capabilitiesJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            containerDigest = ReadString(document.RootElement, ContainerDigestProperty);
            binarySha256 = ReadString(document.RootElement, BinaryDigestProperty);
            return containerDigest is not null && binarySha256 is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;
}
