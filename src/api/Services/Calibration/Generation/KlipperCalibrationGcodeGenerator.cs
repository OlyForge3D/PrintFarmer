using System.Globalization;
using System.Text;
using Farm.Infrastructure.PrinterCalibration;

namespace Farm.Web.Api.Services.Calibration.Generation;

/// <summary>Default <see cref="IKlipperCalibrationGcodeGenerator"/>.</summary>
/// <remarks>
/// Every number is converted with <see cref="CultureInfo.InvariantCulture"/> at a fixed decimal
/// precision before it is interpolated, every block is emitted in a fixed order, and the writer only
/// ever appends <c>\n</c>. The generator therefore produces byte-identical output for identical inputs
/// on any host, in any locale.
/// </remarks>
public sealed class KlipperCalibrationGcodeGenerator(
    CalibrationSlicerCompatibilityPolicy? compatibilityPolicy = null)
    : IKlipperCalibrationGcodeGenerator
{
    private readonly CalibrationSlicerCompatibilityPolicy _compatibilityPolicy =
        compatibilityPolicy ?? CalibrationSlicerCompatibilityPolicy.Default;

    /// <summary>Wall loops printed per calibration layer.</summary>
    public const int WallLoops = 2;

    /// <summary>Z lift, in millimetres, applied during finalization.</summary>
    public const decimal FinalizeLiftMillimeters = 5m;

    /// <summary>Gap, in millimetres, between the two towers of a retraction test.</summary>
    public const decimal RetractionTowerGapMillimeters = 20m;

    private const decimal Pi = 3.1415926535897932384626433833m;
    private const decimal MinimumSegmentLength = 0.05m;

    /// <inheritdoc/>
    public CalibrationGenerationResult<KlipperCalibrationProgram> Generate(
        CalibrationSpecification specification,
        OrcaCalibrationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(specification);
        ArgumentNullException.ThrowIfNull(plan);

        CalibrationSpecificationDocument document = specification.Document;
        List<CalibrationGenerationProblem> problems = [];

        if (!CalibrationCanonicalJson.DigestsMatch(
            plan.Manifest.SpecificationSha256,
            specification.Sha256))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.SpecificationHashMismatch,
                "plan.manifest.specificationSha256",
                "The plan was compiled from a different specification."));
        }

        CalibrationSupportedTupleValidator.Validate(
            document.Compatibility,
            problems,
            _compatibilityPolicy);
        VerifyFootprintPlacement(document, problems);
        if (problems.Count > 0)
        {
            return CalibrationGenerationResults.Failure<KlipperCalibrationProgram>(problems);
        }

        if (!CalibrationMethodNames.TryParse(document.Method, out CalibrationMethod method))
        {
            return CalibrationGenerationResults.Failure<KlipperCalibrationProgram>(
                CalibrationGenerationProblemCodes.MethodUnsupported,
                "specification.method",
                "The specification declares an unsupported calibration method.");
        }

        StringBuilder builder = new(16 * 1024);
        CalibrationGcodeBodySource bodySource = method == CalibrationMethod.FinalVerification
            ? CalibrationGcodeBodySource.SlicedFromLinkedAsset
            : CalibrationGcodeBodySource.ServerGenerated;

        Append(builder, CalibrationGcodeMarkers.ProgramBegin);
        WriteInitialization(builder, document);

        for (int index = 0; index < document.Segments.Count; index++)
        {
            CalibrationSegmentSpecification segment = document.Segments[index];
            if (index > 0)
            {
                WriteTransition(builder, document, document.Segments[index - 1], segment);
            }

            WriteSegmentBegin(builder, document, segment);
            WriteSegmentBody(builder, document, method, segment, problems);
            WriteSegmentEnd(builder, segment);
        }

        WriteFinalization(builder, document);
        Append(builder, CalibrationGcodeMarkers.ProgramEnd);

        if (problems.Count > 0)
        {
            return CalibrationGenerationResults.Failure<KlipperCalibrationProgram>(problems);
        }

        string text = builder.ToString();
        return CalibrationGenerationResults.Success(new KlipperCalibrationProgram(
            text,
            bodySource,
            document.Segments.Count,
            CalibrationCanonicalJson.ComputeTextSha256(text)));
    }

    /// <summary>Computes the deterministic extrusion length required per millimetre of travel.</summary>
    /// <param name="print">The resolved print parameters.</param>
    /// <param name="flowRatio">The flow ratio applied to the move.</param>
    /// <returns>Filament length, in millimetres, per millimetre of travel.</returns>
    public static decimal ExtrusionPerMillimeter(CalibrationPrintParameters print, decimal flowRatio)
    {
        ArgumentNullException.ThrowIfNull(print);
        decimal radius = print.FilamentDiameterMillimeters / 2m;
        decimal filamentArea = Pi * radius * radius;
        return filamentArea <= 0m
            ? 0m
            : decimal.Round(CrossSection(print) * flowRatio / filamentArea, 6);
    }

    /// <summary>Computes the deterministic extruded cross-section area, in square millimetres.</summary>
    /// <param name="print">The resolved print parameters.</param>
    /// <returns>The cross-section area.</returns>
    public static decimal CrossSection(CalibrationPrintParameters print)
    {
        ArgumentNullException.ThrowIfNull(print);
        return decimal.Round(print.LineWidthMillimeters * print.LayerHeightMillimeters, 6);
    }

    private static void VerifyFootprintPlacement(
        CalibrationSpecificationDocument document,
        List<CalibrationGenerationProblem> problems)
    {
        CalibrationFootprint footprint = document.Footprint;
        IReadOnlyList<CalibrationBedPoint> polygon = document.Bed.PrintablePolygon.Count >= 3
            ? document.Bed.PrintablePolygon
            : CalibrationGeometry.BuildVolumeRectangle(document.Bed);

        if (polygon.Count >= 3 &&
            !CalibrationGeometry.ContainsRectangle(
                polygon,
                footprint.MinX,
                footprint.MinY,
                footprint.MaxX,
                footprint.MaxY))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.GcodeMotionOutsidePrintablePolygon,
                "specification.footprint",
                "The calibration footprint falls outside the authoritative printable polygon."));
        }

        if (document.Bed.ExcludedRegions.Any(region =>
                region.Polygon.Count >= 3 &&
                CalibrationGeometry.IntersectsRectangle(
                    region.Polygon,
                    footprint.MinX,
                    footprint.MinY,
                    footprint.MaxX,
                    footprint.MaxY)))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.GcodeMotionInsideExcludedRegion,
                "specification.footprint",
                "The calibration footprint overlaps an authoritative excluded region."));
        }
    }

    private static void WriteInitialization(
        StringBuilder builder,
        CalibrationSpecificationDocument document)
    {
        CalibrationPrintParameters print = document.Print;
        string firstTemperature = Whole(FirstNozzleTemperature(document));

        Append(builder, CalibrationGcodeMarkers.Initialize);
        Append(builder, "G21");
        Append(builder, "G90");
        Append(builder, "M83");
        Append(builder, $"M104 S{firstTemperature}");
        Append(builder, $"M140 S{Whole(print.BedTemperatureCelsius)}");
        if (print.ChamberTemperatureCelsius is { } chamber)
        {
            Append(builder, $"M141 S{Whole(chamber)}");
        }

        Append(builder, $"M190 S{Whole(print.BedTemperatureCelsius)}");
        if (print.ChamberTemperatureCelsius is { } waitChamber)
        {
            Append(builder, $"M191 S{Whole(waitChamber)}");
        }

        Append(builder, "G28");
        Append(builder, $"M109 S{firstTemperature}");
        Append(builder, $"M204 S{Whole(print.AccelerationMillimetersPerSecondSquared)}");
        string velocityLimit =
            $"SET_VELOCITY_LIMIT VELOCITY={Whole(print.TravelSpeedMillimetersPerSecond)} " +
            $"ACCEL={Whole(print.AccelerationMillimetersPerSecondSquared)}";
        Append(builder, velocityLimit);
        Append(builder, $"SET_PRESSURE_ADVANCE ADVANCE={Format(print.PressureAdvance, 4)}");
        Append(builder, "M220 S100");
        Append(builder, "M221 S100");
        Append(builder, "M107");
        Append(builder, "G92 E0");
        WritePrimeLine(builder, document);
    }

    private static void WritePrimeLine(
        StringBuilder builder,
        CalibrationSpecificationDocument document)
    {
        CalibrationPrintParameters print = document.Print;
        CalibrationFootprint footprint = document.Footprint;
        decimal primeY = footprint.MinY;
        decimal startX = footprint.MinX;
        decimal endX = footprint.MaxX;
        decimal extrusion = ExtrusionPerMillimeter(print, print.FlowRatio) * (endX - startX);

        Travel(builder, startX, primeY, print.FirstLayerHeightMillimeters, print);
        Extrude(builder, endX, primeY, extrusion, print.FirstLayerSpeedMillimetersPerSecond);
        Retract(builder, print);
    }

    private static void WriteTransition(
        StringBuilder builder,
        CalibrationSpecificationDocument document,
        CalibrationSegmentSpecification previous,
        CalibrationSegmentSpecification next)
    {
        string transition =
            $"{CalibrationGcodeMarkers.SegmentTransition} FROM={Whole(previous.Index)} " +
            $"TO={Whole(next.Index)}";
        Append(builder, transition);
        Retract(builder, document.Print);
        Append(builder, "G92 E0");
    }

    private static void WriteSegmentBegin(
        StringBuilder builder,
        CalibrationSpecificationDocument document,
        CalibrationSegmentSpecification segment)
    {
        string marker =
            $"{CalibrationGcodeMarkers.SegmentBegin} INDEX={Whole(segment.Index)} " +
            $"METHOD={document.Method} PARAM={segment.ParameterName} " +
            $"VALUE={Format(segment.Value, 4)} UNIT={segment.Unit} " +
            $"LAYERS={Whole(segment.StartLayer)}-{Whole(segment.EndLayer)} " +
            $"Z={Format(segment.StartZMillimeters, 3)}-{Format(segment.EndZMillimeters, 3)}";
        Append(builder, marker);
        WriteSegmentSetup(builder, segment);
    }

    private static void WriteSegmentEnd(
        StringBuilder builder,
        CalibrationSegmentSpecification segment) =>
        Append(builder, $"{CalibrationGcodeMarkers.SegmentEnd} INDEX={Whole(segment.Index)}");

    private static void WriteSegmentSetup(
        StringBuilder builder,
        CalibrationSegmentSpecification segment)
    {
        switch (segment.ParameterName)
        {
            case CalibrationSweepResolver.NozzleTemperatureParameter:
                Append(builder, $"M104 S{Whole((int)segment.Value)}");
                Append(builder, $"M109 S{Whole((int)segment.Value)}");
                break;
            case CalibrationSweepResolver.PressureAdvanceParameter:
                Append(builder, $"SET_PRESSURE_ADVANCE ADVANCE={Format(segment.Value, 4)}");
                break;
            case CalibrationSweepResolver.FlowRatioParameter:
                Append(builder, $"M221 S{Format(segment.Value * 100m, 2)}");
                break;
            default:
                // Retraction, volumetric speed, shrinkage and verification segments carry their value
                // in motion arithmetic instead of a machine state command.
                break;
        }
    }

    private static void WriteSegmentBody(
        StringBuilder builder,
        CalibrationSpecificationDocument document,
        CalibrationMethod method,
        CalibrationSegmentSpecification segment,
        List<CalibrationGenerationProblem> problems)
    {
        switch (method)
        {
            case CalibrationMethod.PressureAdvanceLine:
                WritePressureAdvanceLine(builder, document, segment, problems);
                break;
            case CalibrationMethod.PressureAdvancePattern:
                WritePressureAdvancePattern(builder, document, segment, problems);
                break;
            case CalibrationMethod.Retraction:
                WriteRetractionTowers(builder, document, segment, problems);
                break;
            case CalibrationMethod.Shrinkage:
                WriteShrinkageBars(builder, document, segment, problems);
                break;
            case CalibrationMethod.FinalVerification:
                WriteLinkedAssetDeclaration(builder, document, segment);
                break;
            default:
                WriteTower(builder, document, method, segment, problems);
                break;
        }
    }

    private static void WriteTower(
        StringBuilder builder,
        CalibrationSpecificationDocument document,
        CalibrationMethod method,
        CalibrationSegmentSpecification segment,
        List<CalibrationGenerationProblem> problems)
    {
        CalibrationPrintParameters print = document.Print;
        CalibrationFootprint footprint = document.Footprint;
        decimal flowRatio = method is CalibrationMethod.FlowRatioCoarse or
            CalibrationMethod.FlowRatioFine or
            CalibrationMethod.FlowRatioHighRange or
            CalibrationMethod.FlowVerification
            ? segment.Value
            : print.FlowRatio;

        int speed = method == CalibrationMethod.MaximumVolumetricSpeed
            ? ResolveVolumetricSpeed(print, segment.Value, problems)
            : print.PrintSpeedMillimetersPerSecond;

        for (int layer = segment.StartLayer; layer <= segment.EndLayer; layer++)
        {
            decimal z = CalibrationSweepResolver.LayerZ(print, layer);
            int layerSpeed = layer == 1 ? print.FirstLayerSpeedMillimetersPerSecond : speed;
            WriteRectangleLayer(
                builder,
                print,
                footprint.CenterXMillimeters,
                footprint.CenterYMillimeters,
                footprint.SizeXMillimeters,
                footprint.SizeYMillimeters,
                z,
                layerSpeed,
                flowRatio);
        }
    }

    private static void WriteRetractionTowers(
        StringBuilder builder,
        CalibrationSpecificationDocument document,
        CalibrationSegmentSpecification segment,
        List<CalibrationGenerationProblem> problems)
    {
        CalibrationPrintParameters print = document.Print;
        CalibrationFootprint footprint = document.Footprint;
        decimal towerSize = (footprint.SizeXMillimeters - RetractionTowerGapMillimeters) / 2m;
        if (towerSize < MinimumSegmentLength)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.GcodeMalformed,
                "specification.footprint",
                "The calibration footprint is too small to print a retraction pair."));
            return;
        }

        decimal offset = (RetractionTowerGapMillimeters + towerSize) / 2m;
        decimal leftCenter = footprint.CenterXMillimeters - offset;
        decimal rightCenter = footprint.CenterXMillimeters + offset;

        for (int layer = segment.StartLayer; layer <= segment.EndLayer; layer++)
        {
            decimal z = CalibrationSweepResolver.LayerZ(print, layer);
            int layerSpeed = layer == 1
                ? print.FirstLayerSpeedMillimetersPerSecond
                : print.PrintSpeedMillimetersPerSecond;

            WriteRectangleLayer(
                builder,
                print,
                leftCenter,
                footprint.CenterYMillimeters,
                towerSize,
                towerSize,
                z,
                layerSpeed,
                print.FlowRatio);
            WriteRetractionCycle(builder, print, segment.Value);
            WriteRectangleLayer(
                builder,
                print,
                rightCenter,
                footprint.CenterYMillimeters,
                towerSize,
                towerSize,
                z,
                layerSpeed,
                print.FlowRatio);
            WriteRetractionCycle(builder, print, segment.Value);
        }
    }

    private static void WriteShrinkageBars(
        StringBuilder builder,
        CalibrationSpecificationDocument document,
        CalibrationSegmentSpecification segment,
        List<CalibrationGenerationProblem> problems)
    {
        CalibrationPrintParameters print = document.Print;
        CalibrationFootprint footprint = document.Footprint;
        decimal nominal = segment.Value;
        decimal barWidth = print.LineWidthMillimeters * 4m;
        if (nominal < MinimumSegmentLength || nominal > footprint.SizeXMillimeters)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.GcodeMalformed,
                "specification.sweep",
                "The shrinkage bar length does not fit the calibration footprint."));
            return;
        }

        for (int layer = segment.StartLayer; layer <= segment.EndLayer; layer++)
        {
            decimal z = CalibrationSweepResolver.LayerZ(print, layer);
            int layerSpeed = layer == 1
                ? print.FirstLayerSpeedMillimetersPerSecond
                : print.PrintSpeedMillimetersPerSecond;

            WriteRectangleLayer(
                builder,
                print,
                footprint.CenterXMillimeters,
                footprint.CenterYMillimeters - (nominal / 4m),
                nominal,
                barWidth,
                z,
                layerSpeed,
                print.FlowRatio);
            Retract(builder, print);
            WriteRectangleLayer(
                builder,
                print,
                footprint.CenterXMillimeters,
                footprint.CenterYMillimeters + (nominal / 4m),
                barWidth,
                nominal,
                z,
                layerSpeed,
                print.FlowRatio);
            Retract(builder, print);
        }
    }

    private static void WritePressureAdvanceLine(
        StringBuilder builder,
        CalibrationSpecificationDocument document,
        CalibrationSegmentSpecification segment,
        List<CalibrationGenerationProblem> problems)
    {
        CalibrationPrintParameters print = document.Print;
        CalibrationFootprint footprint = document.Footprint;
        decimal lineLength = footprint.SizeXMillimeters - (4m * print.LineWidthMillimeters);
        if (lineLength < MinimumSegmentLength)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.GcodeMalformed,
                "specification.footprint",
                "The calibration footprint is too small to print a pressure advance line."));
            return;
        }

        decimal spacing = CalibrationSpecificationCompiler.PressureAdvanceLineSpacingFactor *
            print.LineWidthMillimeters;
        decimal y = decimal.Round(footprint.MinY + (spacing * (segment.Index + 1)), 3);
        if (y > footprint.MaxY)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.GcodeMotionOutsidePrintablePolygon,
                "specification.footprint",
                "A pressure advance line falls outside the calibration footprint."));
            return;
        }

        decimal startX = decimal.Round(footprint.MinX + (2m * print.LineWidthMillimeters), 3);
        decimal slowLength = decimal.Round(lineLength / 4m, 3);
        decimal fastLength = decimal.Round(lineLength / 2m, 3);
        decimal z = print.FirstLayerHeightMillimeters;
        decimal perMillimeter = ExtrusionPerMillimeter(print, print.FlowRatio);
        int slowSpeed = Math.Max(5, print.PrintSpeedMillimetersPerSecond / 4);
        int fastSpeed = print.PrintSpeedMillimetersPerSecond;

        Travel(builder, startX, y, z, print);
        Append(builder, "G92 E0");
        Extrude(
            builder,
            decimal.Round(startX + slowLength, 3),
            y,
            perMillimeter * slowLength,
            slowSpeed);
        Extrude(
            builder,
            decimal.Round(startX + slowLength + fastLength, 3),
            y,
            perMillimeter * fastLength,
            fastSpeed);
        Extrude(
            builder,
            decimal.Round(startX + lineLength, 3),
            y,
            perMillimeter * slowLength,
            slowSpeed);
        Retract(builder, print);
    }

    private static void WritePressureAdvancePattern(
        StringBuilder builder,
        CalibrationSpecificationDocument document,
        CalibrationSegmentSpecification segment,
        List<CalibrationGenerationProblem> problems)
    {
        CalibrationPrintParameters print = document.Print;
        CalibrationFootprint footprint = document.Footprint;
        decimal corner = CalibrationSpecificationCompiler.PatternCornerSizeMillimeters;
        decimal rowSpacing = CalibrationSpecificationCompiler.PatternRowSpacingMillimeters;
        decimal y = decimal.Round(footprint.MinY + (rowSpacing * (segment.Index + 1)), 3);
        if (y + (corner / 2m) > footprint.MaxY)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.GcodeMotionOutsidePrintablePolygon,
                "specification.footprint",
                "A pressure advance pattern row falls outside the calibration footprint."));
            return;
        }

        decimal usableWidth = footprint.SizeXMillimeters - (4m * print.LineWidthMillimeters);
        int corners = (int)Math.Floor(usableWidth / corner);
        if (corners < 1)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.GcodeMalformed,
                "specification.footprint",
                "The calibration footprint is too small to print a pressure advance pattern."));
            return;
        }

        decimal z = print.FirstLayerHeightMillimeters;
        decimal perMillimeter = ExtrusionPerMillimeter(print, print.FlowRatio);
        decimal x = decimal.Round(footprint.MinX + (2m * print.LineWidthMillimeters), 3);
        int speed = print.PrintSpeedMillimetersPerSecond;

        Travel(builder, x, y, z, print);
        Append(builder, "G92 E0");
        for (int index = 0; index < corners; index++)
        {
            decimal apexX = decimal.Round(x + (corner / 2m), 3);
            decimal apexY = decimal.Round(y + (corner / 2m), 3);
            decimal endX = decimal.Round(x + corner, 3);
            Extrude(builder, apexX, apexY, perMillimeter * (corner / 2m), speed);
            Extrude(builder, endX, y, perMillimeter * (corner / 2m), speed);
            x = endX;
        }

        Retract(builder, print);
    }

    private static void WriteLinkedAssetDeclaration(
        StringBuilder builder,
        CalibrationSpecificationDocument document,
        CalibrationSegmentSpecification segment)
    {
        // Final verification prints the linked, validated asset. The trusted generator emits only the
        // deterministic envelope plus a machine-readable declaration of the asset identity, so the
        // orchestration can splice the pinned upstream slicer output between these markers. No
        // geometry is invented here and no caller-supplied G-code is ever accepted.
        string model = document.ImportedAsset?.Model3DId.ToString() ?? Guid.Empty.ToString();
        string digest = document.ImportedAsset?.Sha256 ?? string.Empty;
        string declaration =
            $";PF_SEG_BODY INDEX={Whole(segment.Index)} SOURCE=sliced-asset " +
            $"MODEL={model} MODEL_SHA256={digest}";
        Append(builder, declaration);
    }

    private static void WriteRectangleLayer(
        StringBuilder builder,
        CalibrationPrintParameters print,
        decimal centerX,
        decimal centerY,
        decimal sizeX,
        decimal sizeY,
        decimal z,
        int speed,
        decimal flowRatio)
    {
        decimal perMillimeter = ExtrusionPerMillimeter(print, flowRatio);
        for (int loop = 0; loop < WallLoops; loop++)
        {
            decimal inset = print.LineWidthMillimeters * loop;
            decimal halfX = (sizeX / 2m) - inset;
            decimal halfY = (sizeY / 2m) - inset;
            if (halfX <= MinimumSegmentLength || halfY <= MinimumSegmentLength)
            {
                break;
            }

            decimal minX = decimal.Round(centerX - halfX, 3);
            decimal maxX = decimal.Round(centerX + halfX, 3);
            decimal minY = decimal.Round(centerY - halfY, 3);
            decimal maxY = decimal.Round(centerY + halfY, 3);
            decimal width = maxX - minX;
            decimal height = maxY - minY;

            Travel(builder, minX, minY, z, print);
            if (loop == 0)
            {
                Append(builder, "G92 E0");
            }

            Extrude(builder, maxX, minY, perMillimeter * width, speed);
            Extrude(builder, maxX, maxY, perMillimeter * height, speed);
            Extrude(builder, minX, maxY, perMillimeter * width, speed);
            Extrude(builder, minX, minY, perMillimeter * height, speed);
        }
    }

    private static void WriteRetractionCycle(
        StringBuilder builder,
        CalibrationPrintParameters print,
        decimal length)
    {
        string feed = Whole(FeedRate(print.RetractionSpeedMillimetersPerSecond));
        Append(builder, $"G1 E-{Format(length, 3)} F{feed}");
        Append(builder, $"G1 E{Format(length, 3)} F{feed}");
    }

    private static void WriteFinalization(
        StringBuilder builder,
        CalibrationSpecificationDocument document)
    {
        CalibrationPrintParameters print = document.Print;
        Append(builder, CalibrationGcodeMarkers.Finalize);
        Retract(builder, print);
        Append(builder, "G91");
        Append(builder, $"G1 Z{Format(FinalizeLiftMillimeters, 3)} F{Whole(FeedRate(10))}");
        Append(builder, "G90");
        Append(builder, $"SET_PRESSURE_ADVANCE ADVANCE={Format(0m, 4)}");
        Append(builder, "M221 S100");
        Append(builder, "M220 S100");
        Append(builder, "TURN_OFF_HEATERS");
        Append(builder, "M107");
        Append(builder, "M84");
    }

    private static void Retract(StringBuilder builder, CalibrationPrintParameters print)
    {
        string line = $"G1 E-{Format(print.RetractionLengthMillimeters, 3)} " +
            $"F{Whole(FeedRate(print.RetractionSpeedMillimetersPerSecond))}";
        Append(builder, line);
    }

    private static void Travel(
        StringBuilder builder,
        decimal x,
        decimal y,
        decimal z,
        CalibrationPrintParameters print)
    {
        string line = $"G0 X{Format(x, 3)} Y{Format(y, 3)} Z{Format(z, 3)} " +
            $"F{Whole(FeedRate(print.TravelSpeedMillimetersPerSecond))}";
        Append(builder, line);
    }

    private static void Extrude(
        StringBuilder builder,
        decimal x,
        decimal y,
        decimal extrusion,
        int speed)
    {
        string line = $"G1 X{Format(x, 3)} Y{Format(y, 3)} E{Format(extrusion, 5)} " +
            $"F{Whole(FeedRate(speed))}";
        Append(builder, line);
    }

    private static int ResolveVolumetricSpeed(
        CalibrationPrintParameters print,
        decimal volumetricFlow,
        List<CalibrationGenerationProblem> problems)
    {
        decimal crossSection = CrossSection(print);
        if (crossSection <= 0m)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.GcodeMalformed,
                "specification.print",
                "The resolved extrusion cross-section is not positive."));
            return print.PrintSpeedMillimetersPerSecond;
        }

        int speed = (int)Math.Floor(volumetricFlow / crossSection);
        if (speed < 1)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.VolumetricFlowOutOfRange,
                "specification.sweep",
                "The requested volumetric speed resolves to a sub-millimetre feed rate."));
            return print.PrintSpeedMillimetersPerSecond;
        }

        if (speed > print.TravelSpeedMillimetersPerSecond)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.GcodeSpeedAboveLimit,
                "specification.sweep",
                "The requested volumetric speed exceeds the authoritative travel speed ceiling."));
            return print.TravelSpeedMillimetersPerSecond;
        }

        return speed;
    }

    private static int FirstNozzleTemperature(CalibrationSpecificationDocument document)
    {
        CalibrationSegmentSpecification first = document.Segments[0];
        return string.Equals(
            first.ParameterName,
            CalibrationSweepResolver.NozzleTemperatureParameter,
            StringComparison.Ordinal)
            ? (int)first.Value
            : document.Print.NozzleTemperatureCelsius;
    }

    private static string Format(decimal value, int decimals) =>
        decimal.Round(value, decimals).ToString(
            decimals switch
            {
                2 => "0.00",
                3 => "0.000",
                4 => "0.0000",
                _ => "0.00000",
            },
            CultureInfo.InvariantCulture);

    private static string Whole(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static int FeedRate(int millimetersPerSecond) => millimetersPerSecond * 60;

    private static void Append(StringBuilder builder, string line) =>
        builder.Append(line).Append('\n');
}
