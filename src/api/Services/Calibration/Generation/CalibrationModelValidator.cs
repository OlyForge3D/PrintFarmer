namespace Farm.Web.Api.Services.Calibration.Generation;

/// <summary>Default <see cref="ICalibrationModelValidator"/>.</summary>
/// <remarks>
/// The validator only ever sees bytes an authorized caller already resolved. It performs no path, URL
/// or host resolution of its own, so path traversal, SSRF and local-file reads cannot originate here.
/// Content is parsed by <see cref="CalibrationMeshReader"/>, which enforces archive, XML and structural
/// budgets before any attacker-controlled count drives an allocation.
/// </remarks>
public sealed class CalibrationModelValidator : ICalibrationModelValidator
{
    /// <summary>Millimetres of clearance the model must keep from every excluded region.</summary>
    public const decimal ExclusionClearanceMillimeters = 0m;

    /// <inheritdoc/>
    public async Task<CalibrationGenerationResult<CalibrationValidatedModel>>
        ValidateGeneratedGeometryAsync(
            CalibrationGeneratedGeometry geometry,
            CalibrationSpecification specification,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(specification);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsSafeFileName(geometry.SafeFileName))
        {
            return Reject(
                CalibrationGenerationProblemCodes.ModelInputNotAllowed,
                "geometry.safeFileName",
                "The geometry file name is not a safe, path-free name.");
        }

        CalibrationMeshReader.MeshReadResult read =
            CalibrationMeshReader.ReadStl(geometry.Content.Span, "geometry.content");
        if (read.Problem is not null)
        {
            return CalibrationGenerationResults.Failure<CalibrationValidatedModel>([read.Problem]);
        }

        string sha256 = CalibrationCanonicalJson.ComputeBytesSha256(geometry.Content.Span);
        return await Task.FromResult(Finish(
            new CalibrationValidatedModel(
                Guid.Empty,
                sha256,
                CalibrationModelFormats.Stl,
                geometry.SafeFileName,
                geometry.Content.Length,
                "generated",
                read.ObjectCount,
                read.TriangleCount,
                read.Bounds!,
                read.Unit),
            specification,
            "geometry"));
    }

    /// <inheritdoc/>
    public async Task<CalibrationGenerationResult<CalibrationValidatedModel>>
        ValidateImportedAssetAsync(
            ICalibrationModelContentSource source,
            CalibrationSpecification specification,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(specification);

        CalibrationModelReference? expected = specification.Document.ImportedAsset;
        if (expected is null)
        {
            return Reject(
                CalibrationGenerationProblemCodes.LinkedAssetMissing,
                "specification.importedAsset",
                "The specification does not reference a linked model asset.");
        }

        if (source.Model3DId == Guid.Empty || source.Model3DId != expected.Model3DId)
        {
            return Reject(
                CalibrationGenerationProblemCodes.LinkedAssetMismatch,
                "source.model3DId",
                "The supplied model identity does not match the specification.");
        }

        if (string.IsNullOrWhiteSpace(source.Provenance))
        {
            return Reject(
                CalibrationGenerationProblemCodes.ModelProvenanceMissing,
                "source.provenance",
                "The stored model has no recorded provenance.");
        }

        if (!IsSafeFileName(source.SafeFileName))
        {
            return Reject(
                CalibrationGenerationProblemCodes.ModelInputNotAllowed,
                "source.safeFileName",
                "The stored model file name is not a safe, path-free name.");
        }

        if (!CalibrationModelFormats.TryParse(source.Format, out CalibrationModelFormat format) ||
            !string.Equals(source.Format, expected.Format, StringComparison.Ordinal))
        {
            return Reject(
                CalibrationGenerationProblemCodes.ModelFormatUnsupported,
                "source.format",
                "Only canonical STL and 3MF calibration models are supported.");
        }

        byte[] content;
        Stream stream = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using MemoryStream buffer = new();
            content = await ReadBoundedAsync(stream, buffer, cancellationToken)
                .ConfigureAwait(false);
        }

        if (content.LongLength == 0)
        {
            return Reject(
                CalibrationGenerationProblemCodes.ModelContentInvalid,
                "source.content",
                "The stored model has no readable content.");
        }

        if (content.LongLength > CalibrationMeshReader.MaxContentBytes)
        {
            return Reject(
                CalibrationGenerationProblemCodes.ModelTooLarge,
                "source.content",
                "The stored model exceeds the accepted size.");
        }

        string sha256 = CalibrationCanonicalJson.ComputeBytesSha256(content);
        if (!CalibrationCanonicalJson.DigestsMatch(sha256, source.Sha256) ||
            !CalibrationCanonicalJson.DigestsMatch(sha256, expected.Sha256))
        {
            return Reject(
                CalibrationGenerationProblemCodes.ModelHashMismatch,
                "source.sha256",
                "The stored model content does not match its authoritative digest.");
        }

        CalibrationMeshReader.MeshReadResult read = format == CalibrationModelFormat.Stl
            ? CalibrationMeshReader.ReadStl(content, "source.content")
            : CalibrationMeshReader.ReadThreeMf(content, "source.content");
        if (read.Problem is not null)
        {
            return CalibrationGenerationResults.Failure<CalibrationValidatedModel>([read.Problem]);
        }

        return Finish(
            new CalibrationValidatedModel(
                source.Model3DId,
                sha256,
                source.Format!,
                source.SafeFileName!,
                content.LongLength,
                source.Provenance!,
                read.ObjectCount,
                read.TriangleCount,
                read.Bounds!,
                read.Unit),
            specification,
            "source");
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        MemoryStream buffer,
        CancellationToken cancellationToken)
    {
        byte[] chunk = new byte[81920];
        long total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            total += read;
            if (total > CalibrationMeshReader.MaxContentBytes)
            {
                // Stop as soon as the budget is exceeded so a hostile stream cannot exhaust memory.
                return new byte[CalibrationMeshReader.MaxContentBytes + 1];
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return buffer.ToArray();
    }

    private static CalibrationGenerationResult<CalibrationValidatedModel> Finish(
        CalibrationValidatedModel model,
        CalibrationSpecification specification,
        string field)
    {
        List<CalibrationGenerationProblem> problems = [];
        CalibrationSpecificationDocument document = specification.Document;
        CalibrationBedGeometry bed = document.Bed;
        CalibrationModelBounds bounds = model.Bounds;

        if (bounds.SizeX <= 0m || bounds.SizeY <= 0m || bounds.SizeZ <= 0m)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.ModelContentInvalid,
                $"{field}.bounds",
                "The model has a degenerate bounding box."));
            return CalibrationGenerationResults.Failure<CalibrationValidatedModel>(problems);
        }

        decimal originX = bed.OriginXMillimeters ?? 0m;
        decimal originY = bed.OriginYMillimeters ?? 0m;

        // The model is placed centred on the deterministic footprint the specification resolved.
        decimal offsetX = document.Footprint.CenterXMillimeters -
            (bounds.MinX + (bounds.SizeX / 2m));
        decimal offsetY = document.Footprint.CenterYMillimeters -
            (bounds.MinY + (bounds.SizeY / 2m));
        decimal placedMinX = bounds.MinX + offsetX;
        decimal placedMaxX = bounds.MaxX + offsetX;
        decimal placedMinY = bounds.MinY + offsetY;
        decimal placedMaxY = bounds.MaxY + offsetY;

        if (bed.SizeXMillimeters is { } sizeX &&
            bed.SizeYMillimeters is { } sizeY &&
            bed.SizeZMillimeters is { } sizeZ &&
            (placedMinX < originX ||
                placedMaxX > originX + sizeX ||
                placedMinY < originY ||
                placedMaxY > originY + sizeY ||
                bounds.SizeZ > sizeZ))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.ModelOutsideBuildVolume,
                $"{field}.bounds",
                "The model does not fit the authoritative build volume."));
        }

        IReadOnlyList<CalibrationBedPoint> polygon = bed.PrintablePolygon.Count >= 3
            ? bed.PrintablePolygon
            : CalibrationGeometry.BuildVolumeRectangle(bed);
        if (polygon.Count >= 3 &&
            !CalibrationGeometry.ContainsRectangle(
                polygon,
                placedMinX,
                placedMinY,
                placedMaxX,
                placedMaxY))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.ModelOutsidePrintablePolygon,
                $"{field}.bounds",
                "The model does not fit the authoritative printable polygon."));
        }

        foreach (CalibrationExcludedRegion region in bed.ExcludedRegions)
        {
            if (region.Polygon.Count >= 3 &&
                CalibrationGeometry.IntersectsRectangle(
                    region.Polygon,
                    placedMinX - ExclusionClearanceMillimeters,
                    placedMinY - ExclusionClearanceMillimeters,
                    placedMaxX + ExclusionClearanceMillimeters,
                    placedMaxY + ExclusionClearanceMillimeters))
            {
                problems.Add(new(
                    CalibrationGenerationProblemCodes.ModelInsideExcludedRegion,
                    $"{field}.bounds",
                    "The model overlaps an authoritative excluded region."));
                break;
            }
        }

        return problems.Count > 0
            ? CalibrationGenerationResults.Failure<CalibrationValidatedModel>(problems)
            : CalibrationGenerationResults.Success(model);
    }

    private static CalibrationGenerationResult<CalibrationValidatedModel> Reject(
        string code,
        string field,
        string message) =>
        CalibrationGenerationResults.Failure<CalibrationValidatedModel>(code, field, message);

    private static bool IsSafeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
        {
            return false;
        }

        if (name.Contains("..", StringComparison.Ordinal) ||
            name.Contains('/', StringComparison.Ordinal) ||
            name.Contains('\\', StringComparison.Ordinal) ||
            name.Contains(':', StringComparison.Ordinal) ||
            name.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        foreach (char character in name)
        {
            bool allowed = char.IsAsciiLetterOrDigit(character) ||
                character is '.' or '-' or '_' or ' ';
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }
}
