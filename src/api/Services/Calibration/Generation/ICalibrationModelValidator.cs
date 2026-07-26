namespace Farm.Web.Api.Services.Calibration.Generation;

/// <summary>Canonical model formats accepted for calibration geometry.</summary>
public enum CalibrationModelFormat
{
    /// <summary>No format. Never a valid request.</summary>
    Unspecified = 0,

    /// <summary>Binary or ASCII stereolithography mesh.</summary>
    Stl = 1,

    /// <summary>3D Manufacturing Format package.</summary>
    ThreeMf = 2,
}

/// <summary>Canonical format tokens for <see cref="CalibrationModelFormat"/>.</summary>
public static class CalibrationModelFormats
{
    /// <summary>Stereolithography mesh.</summary>
    public const string Stl = "stl";

    /// <summary>3D Manufacturing Format package.</summary>
    public const string ThreeMf = "3mf";

    /// <summary>Parses a canonical format token; aliases and extensions are not accepted.</summary>
    /// <param name="value">The candidate token.</param>
    /// <param name="format">The parsed format when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the token is exactly <c>stl</c> or <c>3mf</c>.</returns>
    public static bool TryParse(string? value, out CalibrationModelFormat format)
    {
        switch (value)
        {
            case Stl:
                format = CalibrationModelFormat.Stl;
                return true;
            case ThreeMf:
                format = CalibrationModelFormat.ThreeMf;
                return true;
            default:
                format = CalibrationModelFormat.Unspecified;
                return false;
        }
    }
}

/// <summary>Axis-aligned mesh bounds computed from actual geometry, in millimetres.</summary>
/// <param name="MinX">Minimum X.</param>
/// <param name="MinY">Minimum Y.</param>
/// <param name="MinZ">Minimum Z.</param>
/// <param name="MaxX">Maximum X.</param>
/// <param name="MaxY">Maximum Y.</param>
/// <param name="MaxZ">Maximum Z.</param>
public sealed record CalibrationModelBounds(
    decimal MinX,
    decimal MinY,
    decimal MinZ,
    decimal MaxX,
    decimal MaxY,
    decimal MaxZ)
{
    /// <summary>Gets the X extent, in millimetres.</summary>
    public decimal SizeX => MaxX - MinX;

    /// <summary>Gets the Y extent, in millimetres.</summary>
    public decimal SizeY => MaxY - MinY;

    /// <summary>Gets the Z extent, in millimetres.</summary>
    public decimal SizeZ => MaxZ - MinZ;
}

/// <summary>
/// A validated calibration model, carrying preserved source provenance and computed bounds.
/// </summary>
/// <param name="Model3DId">Preserved stored model identity.</param>
/// <param name="Sha256">Preserved authoritative content digest.</param>
/// <param name="Format">Canonical format token.</param>
/// <param name="SafeFileName">Preserved sanitized file name; never a path.</param>
/// <param name="SizeBytes">Observed content size, in bytes.</param>
/// <param name="Provenance">Preserved provenance token.</param>
/// <param name="ObjectCount">Number of printable objects found.</param>
/// <param name="TriangleCount">Number of triangles found.</param>
/// <param name="Bounds">Bounds computed from the actual geometry.</param>
/// <param name="Unit">Declared unit; always <c>millimeter</c> once validated.</param>
public sealed record CalibrationValidatedModel(
    Guid Model3DId,
    string Sha256,
    string Format,
    string SafeFileName,
    long SizeBytes,
    string Provenance,
    int ObjectCount,
    int TriangleCount,
    CalibrationModelBounds Bounds,
    string Unit);

/// <summary>
/// Supplies the bytes of a stored model that an authorized caller already resolved.
/// </summary>
/// <remarks>
/// The validator never opens a path, follows a URL or contacts a host. The caller resolves storage,
/// enforces ownership, and hands over an opened stream, which keeps SSRF, path traversal and local
/// file reads entirely outside this service.
/// </remarks>
public interface ICalibrationModelContentSource
{
    /// <summary>Gets the stored model identity.</summary>
    Guid Model3DId { get; }

    /// <summary>Gets the authoritative content digest recorded for the stored model.</summary>
    string? Sha256 { get; }

    /// <summary>Gets the canonical format token: <c>stl</c> or <c>3mf</c>.</summary>
    string? Format { get; }

    /// <summary>Gets the sanitized display file name; never a path.</summary>
    string? SafeFileName { get; }

    /// <summary>Gets the provenance token, for example <c>imported</c> or <c>generated</c>.</summary>
    string? Provenance { get; }

    /// <summary>Opens the stored content for reading.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A readable, seekable-or-forward-only content stream owned by the caller.</returns>
    Task<Stream> OpenAsync(CancellationToken cancellationToken);
}

/// <summary>Trusted geometry the generator produced itself, supplied as canonical binary STL.</summary>
/// <param name="Content">The canonical binary STL bytes.</param>
/// <param name="SafeFileName">Sanitized file name; never a path.</param>
public sealed record CalibrationGeneratedGeometry(ReadOnlyMemory<byte> Content, string SafeFileName);

/// <summary>
/// Validates trusted generated geometry and linked imported models before a plan is compiled.
/// </summary>
public interface ICalibrationModelValidator
{
    /// <summary>
    /// Validates trusted geometry this server generated, without touching storage.
    /// </summary>
    /// <param name="geometry">The generated canonical binary STL.</param>
    /// <param name="specification">The compiled specification that describes the placement.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The validated model, or the ordered rejection reasons.</returns>
    Task<CalibrationGenerationResult<CalibrationValidatedModel>> ValidateGeneratedGeometryAsync(
        CalibrationGeneratedGeometry geometry,
        CalibrationSpecification specification,
        CancellationToken cancellationToken);

    /// <summary>
    /// Validates a linked imported model by storage identity, digest and provenance.
    /// </summary>
    /// <param name="source">An authorized content source resolved by the caller.</param>
    /// <param name="specification">The compiled specification that references the asset.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The validated model, or the ordered rejection reasons.</returns>
    Task<CalibrationGenerationResult<CalibrationValidatedModel>> ValidateImportedAssetAsync(
        ICalibrationModelContentSource source,
        CalibrationSpecification specification,
        CancellationToken cancellationToken);
}
