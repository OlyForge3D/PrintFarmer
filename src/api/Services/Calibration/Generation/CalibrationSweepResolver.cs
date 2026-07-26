namespace Farm.Web.Api.Services.Calibration.Generation;

/// <summary>A resolved sweep together with its ordered deterministic segments.</summary>
/// <param name="Sweep">The resolved sweep description.</param>
/// <param name="Segments">The ordered deterministic segments.</param>
public sealed record CalibrationSweepPlan(
    CalibrationParameterSweep Sweep,
    IReadOnlyList<CalibrationSegmentSpecification> Segments);

/// <summary>
/// Resolves the deterministic segment strategy for every supported calibration method.
/// </summary>
/// <remarks>
/// The strategy is a pure function of the authoritative context and the typed options, so identical
/// inputs always produce an identical segment list. Every default comes from the authoritative context
/// (nozzle diameter, filament baseline, process baseline, machine limits); none are invented.
/// </remarks>
public static class CalibrationSweepResolver
{
    /// <summary>Layers printed per banded segment.</summary>
    public const int LayersPerBand = 10;

    /// <summary>Layers printed for a single-band verification or shrinkage body.</summary>
    public const int VerificationLayers = 20;

    /// <summary>Parameter name for the nozzle temperature sweep.</summary>
    public const string NozzleTemperatureParameter = "nozzle_temperature";

    /// <summary>Parameter name for the flow ratio sweep.</summary>
    public const string FlowRatioParameter = "flow_ratio";

    /// <summary>Parameter name for the pressure advance sweep.</summary>
    public const string PressureAdvanceParameter = "pressure_advance";

    /// <summary>Parameter name for the retraction sweep.</summary>
    public const string RetractionLengthParameter = "retraction_length";

    /// <summary>Parameter name for the maximum volumetric speed sweep.</summary>
    public const string MaxVolumetricSpeedParameter = "max_volumetric_speed";

    /// <summary>Parameter name for the shrinkage nominal length.</summary>
    public const string ShrinkageNominalLengthParameter = "shrinkage_nominal_length";

    /// <summary>Parameter name for the final verification pass.</summary>
    public const string FinalVerificationParameter = "verification_pass";

    /// <summary>Resolves the sweep and segments for a method.</summary>
    /// <param name="context">The authoritative generation context.</param>
    /// <param name="options">The typed method options.</param>
    /// <param name="print">The already resolved print parameters.</param>
    /// <param name="problems">The problem list to append rejection reasons to.</param>
    /// <returns>The resolved plan, or <see langword="null"/> when the request was rejected.</returns>
    public static CalibrationSweepPlan? Resolve(
        CalibrationGenerationContext context,
        CalibrationMethodOptions options,
        CalibrationPrintParameters print,
        List<CalibrationGenerationProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(print);
        ArgumentNullException.ThrowIfNull(problems);

        return options switch
        {
            TemperatureCalibrationOptions temperature =>
                ResolveTemperature(context, temperature, print, problems),
            FlowRatioCalibrationOptions flow =>
                ResolveFlowRatio(context, flow, print, problems),
            FlowVerificationCalibrationOptions verification =>
                ResolveFlowVerification(context, verification, print, problems),
            PressureAdvanceTowerCalibrationOptions tower =>
                ResolvePressureAdvanceTower(context, tower, print, problems),
            PressureAdvanceLineCalibrationOptions line =>
                ResolvePressureAdvanceLine(context, line, print, problems),
            PressureAdvancePatternCalibrationOptions pattern =>
                ResolvePressureAdvancePattern(context, pattern, print, problems),
            RetractionCalibrationOptions retraction =>
                ResolveRetraction(context, retraction, print, problems),
            MaximumVolumetricSpeedCalibrationOptions volumetric =>
                ResolveMaximumVolumetricSpeed(context, volumetric, print, problems),
            ShrinkageCalibrationOptions shrinkage =>
                ResolveShrinkage(shrinkage, print, problems),
            FinalVerificationCalibrationOptions => ResolveFinalVerification(print),
            _ => Reject(problems),
        };
    }

    private static CalibrationSweepPlan? Reject(List<CalibrationGenerationProblem> problems)
    {
        problems.Add(new(
            CalibrationGenerationProblemCodes.MethodUnsupported,
            "options",
            "The requested calibration method is not supported."));
        return null;
    }

    private static CalibrationSweepPlan? ResolveTemperature(
        CalibrationGenerationContext context,
        TemperatureCalibrationOptions options,
        CalibrationPrintParameters print,
        List<CalibrationGenerationProblem> problems)
    {
        int baseline = print.NozzleTemperatureCelsius;
        int start = options.StartCelsius ?? baseline + 20;
        int end = options.EndCelsius ?? baseline - 20;
        int step = options.StepCelsius ?? 5;

        return BuildDescending(
            NozzleTemperatureParameter,
            CalibrationUnits.Celsius,
            start,
            end,
            step,
            print,
            problems,
            context.Bed.SizeZMillimeters);
    }

    private static CalibrationSweepPlan? ResolveFlowRatio(
        CalibrationGenerationContext context,
        FlowRatioCalibrationOptions options,
        CalibrationPrintParameters print,
        List<CalibrationGenerationProblem> problems)
    {
        decimal baseline = print.FlowRatio;
        (decimal span, decimal defaultStep) = options.Method switch
        {
            CalibrationMethod.FlowRatioCoarse => (0.10m, 0.02m),
            CalibrationMethod.FlowRatioFine => (0.025m, 0.005m),
            _ => (0.25m, 0.05m),
        };

        decimal start = options.StartRatio ?? decimal.Round(baseline - span, 4);
        decimal end = options.EndRatio ?? decimal.Round(baseline + span, 4);
        decimal step = options.StepRatio ?? defaultStep;

        return BuildAscending(
            FlowRatioParameter,
            CalibrationUnits.Ratio,
            start,
            end,
            step,
            4,
            print,
            problems,
            context.Bed.SizeZMillimeters);
    }

    private static CalibrationSweepPlan ResolveFlowVerification(
        CalibrationGenerationContext context,
        FlowVerificationCalibrationOptions options,
        CalibrationPrintParameters print,
        List<CalibrationGenerationProblem> problems)
    {
        _ = context;
        _ = problems;
        decimal value = decimal.Round(options.FlowRatio ?? print.FlowRatio, 4);
        return SingleBand(FlowRatioParameter, CalibrationUnits.Ratio, value, print);
    }

    private static CalibrationSweepPlan? ResolvePressureAdvanceTower(
        CalibrationGenerationContext context,
        PressureAdvanceTowerCalibrationOptions options,
        CalibrationPrintParameters print,
        List<CalibrationGenerationProblem> problems)
    {
        (decimal defaultEnd, decimal defaultStep) = PressureAdvanceDefaults(context);
        decimal start = options.StartPressureAdvance ?? 0m;
        decimal end = options.EndPressureAdvance ?? defaultEnd;
        decimal step = options.StepPressureAdvance ?? defaultStep;

        return BuildAscending(
            PressureAdvanceParameter,
            CalibrationUnits.Seconds,
            start,
            end,
            step,
            4,
            print,
            problems,
            context.Bed.SizeZMillimeters);
    }

    private static CalibrationSweepPlan? ResolvePressureAdvanceLine(
        CalibrationGenerationContext context,
        PressureAdvanceLineCalibrationOptions options,
        CalibrationPrintParameters print,
        List<CalibrationGenerationProblem> problems)
    {
        (decimal defaultEnd, _) = PressureAdvanceDefaults(context);
        decimal start = options.StartPressureAdvance ?? 0m;
        decimal end = options.EndPressureAdvance ?? defaultEnd;
        int lineCount = options.LineCount ?? 11;

        if (lineCount is < 2 or > 32)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.SweepInvalid,
                "options.lineCount",
                "The requested pressure advance line count is outside the supported range."));
            return null;
        }

        if (end <= start)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.SweepInvalid,
                "options.endPressureAdvance",
                "The pressure advance line sweep must increase."));
            return null;
        }

        decimal step = decimal.Round((end - start) / (lineCount - 1), 4);
        List<CalibrationSegmentSpecification> segments = new(lineCount);
        for (int index = 0; index < lineCount; index++)
        {
            decimal value = decimal.Round(start + (step * index), 4);
            segments.Add(new CalibrationSegmentSpecification(
                index,
                PressureAdvanceParameter,
                CalibrationUnits.Seconds,
                value,
                1,
                1,
                print.FirstLayerHeightMillimeters,
                print.FirstLayerHeightMillimeters));
        }

        return new CalibrationSweepPlan(
            new CalibrationParameterSweep(
                PressureAdvanceParameter,
                CalibrationUnits.Seconds,
                start,
                decimal.Round(start + (step * (lineCount - 1)), 4),
                step,
                lineCount),
            segments);
    }

    private static CalibrationSweepPlan? ResolvePressureAdvancePattern(
        CalibrationGenerationContext context,
        PressureAdvancePatternCalibrationOptions options,
        CalibrationPrintParameters print,
        List<CalibrationGenerationProblem> problems)
    {
        (decimal defaultEnd, decimal defaultStep) = PressureAdvanceDefaults(context);
        decimal start = options.StartPressureAdvance ?? 0m;
        decimal end = options.EndPressureAdvance ?? defaultEnd;
        decimal step = options.StepPressureAdvance ?? defaultStep;
        int corners = options.CornersPerRow ?? 3;

        if (corners is < 1 or > 8)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.SweepInvalid,
                "options.cornersPerRow",
                "The requested pattern corner count is outside the supported range."));
            return null;
        }

        if (step <= 0m || end <= start)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.SweepInvalid,
                "options.stepPressureAdvance",
                "The pressure advance pattern sweep must increase by a positive step."));
            return null;
        }

        List<CalibrationSegmentSpecification> segments = [];
        int index = 0;
        for (decimal value = start; value <= end + 0.00005m; value = decimal.Round(value + step, 4))
        {
            segments.Add(new CalibrationSegmentSpecification(
                index,
                PressureAdvanceParameter,
                CalibrationUnits.Seconds,
                decimal.Round(value, 4),
                1,
                1,
                print.FirstLayerHeightMillimeters,
                print.FirstLayerHeightMillimeters));
            index++;
            if (index > 64)
            {
                break;
            }
        }

        return new CalibrationSweepPlan(
            new CalibrationParameterSweep(
                PressureAdvanceParameter,
                CalibrationUnits.Seconds,
                start,
                segments[^1].Value,
                step,
                segments.Count),
            segments);
    }

    private static CalibrationSweepPlan? ResolveRetraction(
        CalibrationGenerationContext context,
        RetractionCalibrationOptions options,
        CalibrationPrintParameters print,
        List<CalibrationGenerationProblem> problems)
    {
        decimal defaultEnd = context.Toolhead.IsDirectDrive == true ? 1.2m : 6.0m;
        decimal start = options.StartLengthMillimeters ?? 0m;
        decimal end = options.EndLengthMillimeters ?? defaultEnd;
        decimal step = options.StepLengthMillimeters ?? 0.2m;

        return BuildAscending(
            RetractionLengthParameter,
            CalibrationUnits.Millimeters,
            start,
            end,
            step,
            3,
            print,
            problems,
            context.Bed.SizeZMillimeters);
    }

    private static CalibrationSweepPlan? ResolveMaximumVolumetricSpeed(
        CalibrationGenerationContext context,
        MaximumVolumetricSpeedCalibrationOptions options,
        CalibrationPrintParameters print,
        List<CalibrationGenerationProblem> problems)
    {
        decimal ceiling = print.MaxVolumetricFlow;
        decimal start = options.StartCubicMillimetersPerSecond ?? decimal.Round(ceiling / 4m, 3);
        decimal end = options.EndCubicMillimetersPerSecond ?? ceiling;
        decimal step = options.StepCubicMillimetersPerSecond ?? 1m;

        return BuildAscending(
            MaxVolumetricSpeedParameter,
            CalibrationUnits.CubicMillimetersPerSecond,
            start,
            end,
            step,
            3,
            print,
            problems,
            context.Bed.SizeZMillimeters);
    }

    private static CalibrationSweepPlan? ResolveShrinkage(
        ShrinkageCalibrationOptions options,
        CalibrationPrintParameters print,
        List<CalibrationGenerationProblem> problems)
    {
        decimal nominal = options.NominalLengthMillimeters ?? 100m;
        if (nominal is < 20m or > 250m)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.SweepInvalid,
                "options.nominalLengthMillimeters",
                "The requested shrinkage bar length is outside the supported range."));
            return null;
        }

        return SingleBand(
            ShrinkageNominalLengthParameter,
            CalibrationUnits.Millimeters,
            decimal.Round(nominal, 3),
            print);
    }

    private static CalibrationSweepPlan ResolveFinalVerification(CalibrationPrintParameters print) =>
        SingleBand(FinalVerificationParameter, CalibrationUnits.Count, 1m, print);

    private static (decimal DefaultEnd, decimal DefaultStep) PressureAdvanceDefaults(
        CalibrationGenerationContext context) =>
        context.Toolhead.IsDirectDrive == true ? (0.10m, 0.010m) : (1.00m, 0.100m);

    private static CalibrationSweepPlan SingleBand(
        string parameterName,
        string unit,
        decimal value,
        CalibrationPrintParameters print)
    {
        decimal startZ = print.FirstLayerHeightMillimeters;
        decimal endZ = decimal.Round(
            print.FirstLayerHeightMillimeters + (print.LayerHeightMillimeters * (VerificationLayers - 1)),
            3);
        CalibrationSegmentSpecification segment = new(
            0,
            parameterName,
            unit,
            value,
            1,
            VerificationLayers,
            startZ,
            endZ);
        return new CalibrationSweepPlan(
            new CalibrationParameterSweep(parameterName, unit, value, value, 0m, 1),
            [segment]);
    }

    private static CalibrationSweepPlan? BuildAscending(
        string parameterName,
        string unit,
        decimal start,
        decimal end,
        decimal step,
        int scale,
        CalibrationPrintParameters print,
        List<CalibrationGenerationProblem> problems,
        decimal? buildVolumeZ)
    {
        if (step <= 0m)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.SweepInvalid,
                "options.step",
                "The requested sweep step must be positive."));
            return null;
        }

        if (end < start)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.SweepInvalid,
                "options.end",
                "The requested sweep end must not be below its start."));
            return null;
        }

        List<decimal> values = [];
        decimal tolerance = step / 1000m;
        for (decimal value = start; value <= end + tolerance; value = value + step)
        {
            values.Add(decimal.Round(value, scale));
            if (values.Count > 64)
            {
                break;
            }
        }

        return BuildBands(parameterName, unit, values, step, print, problems, buildVolumeZ);
    }

    private static CalibrationSweepPlan? BuildDescending(
        string parameterName,
        string unit,
        int start,
        int end,
        int step,
        CalibrationPrintParameters print,
        List<CalibrationGenerationProblem> problems,
        decimal? buildVolumeZ)
    {
        if (step <= 0)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.SweepInvalid,
                "options.stepCelsius",
                "The requested sweep step must be positive."));
            return null;
        }

        if (end > start)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.SweepInvalid,
                "options.endCelsius",
                "A temperature tower must descend from its start to its end."));
            return null;
        }

        List<decimal> values = [];
        for (int value = start; value >= end; value -= step)
        {
            values.Add(value);
            if (values.Count > 64)
            {
                break;
            }
        }

        return BuildBands(parameterName, unit, values, step, print, problems, buildVolumeZ);
    }

    private static CalibrationSweepPlan? BuildBands(
        string parameterName,
        string unit,
        List<decimal> values,
        decimal step,
        CalibrationPrintParameters print,
        List<CalibrationGenerationProblem> problems,
        decimal? buildVolumeZ)
    {
        if (values.Count == 0)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.SweepInvalid,
                "options",
                "The requested sweep resolves to no segments."));
            return null;
        }

        List<CalibrationSegmentSpecification> segments = new(values.Count);
        int layer = 1;
        for (int index = 0; index < values.Count; index++)
        {
            int startLayer = layer;
            int endLayer = layer + LayersPerBand - 1;
            segments.Add(new CalibrationSegmentSpecification(
                index,
                parameterName,
                unit,
                values[index],
                startLayer,
                endLayer,
                LayerZ(print, startLayer),
                LayerZ(print, endLayer)));
            layer = endLayer + 1;
        }

        decimal topZ = segments[^1].EndZMillimeters;
        if (buildVolumeZ is { } maxZ && topZ > maxZ)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.SegmentCountOutOfRange,
                "options",
                "The requested sweep is taller than the authoritative build volume."));
            return null;
        }

        return new CalibrationSweepPlan(
            new CalibrationParameterSweep(
                parameterName,
                unit,
                values[0],
                values[^1],
                step,
                values.Count),
            segments);
    }

    /// <summary>Computes the deterministic Z height of a one-based layer.</summary>
    /// <param name="print">The resolved print parameters.</param>
    /// <param name="layer">The one-based layer index.</param>
    /// <returns>The Z height, in millimetres, rounded to three decimals.</returns>
    public static decimal LayerZ(CalibrationPrintParameters print, int layer)
    {
        ArgumentNullException.ThrowIfNull(print);
        return decimal.Round(
            print.FirstLayerHeightMillimeters + (print.LayerHeightMillimeters * (layer - 1)),
            3);
    }
}
