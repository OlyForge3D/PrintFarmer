namespace Farm.Web.Api.Services.Calibration.Generation;

/// <summary>
/// Base type for typed, versioned calibration method input.
/// </summary>
/// <remarks>
/// Options are deliberately closed: every derived type exposes only explicitly typed, unit-bearing
/// numeric members. There is no extension dictionary, no free-form settings map, no file path, URL,
/// mesh, archive, command, G-code or CLI member anywhere in this hierarchy, so a caller cannot smuggle
/// untrusted content into the generator through method options. Any member left <see langword="null"/>
/// is filled from the authoritative context, never from a hard-coded machine assumption.
/// </remarks>
public abstract record CalibrationMethodOptions
{
    /// <summary>The definition version supported by this build.</summary>
    public const string CurrentDefinitionVersion = "1.0";

    /// <summary>Gets the method these options belong to.</summary>
    public abstract CalibrationMethod Method { get; }

    /// <summary>Gets the versioned schema identity of these options.</summary>
    public string DefinitionVersion { get; init; } = CurrentDefinitionVersion;
}

/// <summary>Nozzle temperature tower input, in degrees Celsius.</summary>
public sealed record TemperatureCalibrationOptions : CalibrationMethodOptions
{
    /// <inheritdoc/>
    public override CalibrationMethod Method => CalibrationMethod.Temperature;

    /// <summary>Gets the first tower temperature; defaults to the filament baseline plus 20 °C.</summary>
    public int? StartCelsius { get; init; }

    /// <summary>Gets the last tower temperature; defaults to the filament baseline minus 20 °C.</summary>
    public int? EndCelsius { get; init; }

    /// <summary>Gets the step between bands; defaults to 5 °C.</summary>
    public int? StepCelsius { get; init; }
}

/// <summary>Flow ratio sweep input, expressed as a dimensionless multiplier.</summary>
public sealed record FlowRatioCalibrationOptions : CalibrationMethodOptions
{
    private readonly CalibrationMethod _method = CalibrationMethod.FlowRatioCoarse;

    /// <summary>Initializes a new instance of the <see cref="FlowRatioCalibrationOptions"/> class.</summary>
    /// <param name="method">
    /// The flow ratio pass: coarse, fine or high range. Any other method is rejected because each pass
    /// has its own bounds and default step.
    /// </param>
    public FlowRatioCalibrationOptions(CalibrationMethod method)
    {
        if (method is not (CalibrationMethod.FlowRatioCoarse or
            CalibrationMethod.FlowRatioFine or
            CalibrationMethod.FlowRatioHighRange))
        {
            throw new ArgumentOutOfRangeException(
                nameof(method),
                method,
                "Flow ratio options accept only the coarse, fine or high-range pass.");
        }

        _method = method;
    }

    /// <inheritdoc/>
    public override CalibrationMethod Method => _method;

    /// <summary>Gets the first flow ratio; defaults to the baseline minus the pass span.</summary>
    public decimal? StartRatio { get; init; }

    /// <summary>Gets the last flow ratio; defaults to the baseline plus the pass span.</summary>
    public decimal? EndRatio { get; init; }

    /// <summary>Gets the step between bands; defaults to the per-pass step.</summary>
    public decimal? StepRatio { get; init; }
}

/// <summary>Single-value flow verification input.</summary>
public sealed record FlowVerificationCalibrationOptions : CalibrationMethodOptions
{
    /// <inheritdoc/>
    public override CalibrationMethod Method => CalibrationMethod.FlowVerification;

    /// <summary>Gets the flow ratio under test; defaults to the filament baseline.</summary>
    public decimal? FlowRatio { get; init; }
}

/// <summary>Pressure advance tower input, in seconds.</summary>
public sealed record PressureAdvanceTowerCalibrationOptions : CalibrationMethodOptions
{
    /// <inheritdoc/>
    public override CalibrationMethod Method => CalibrationMethod.PressureAdvanceTower;

    /// <summary>Gets the first pressure advance value; defaults to zero.</summary>
    public decimal? StartPressureAdvance { get; init; }

    /// <summary>Gets the last pressure advance value; defaults to the direct-drive or bowden ceiling.</summary>
    public decimal? EndPressureAdvance { get; init; }

    /// <summary>Gets the step between bands; defaults to the direct-drive or bowden step.</summary>
    public decimal? StepPressureAdvance { get; init; }
}

/// <summary>Trusted server-generated pressure advance line input, in seconds.</summary>
public sealed record PressureAdvanceLineCalibrationOptions : CalibrationMethodOptions
{
    /// <inheritdoc/>
    public override CalibrationMethod Method => CalibrationMethod.PressureAdvanceLine;

    /// <summary>Gets the first pressure advance value; defaults to zero.</summary>
    public decimal? StartPressureAdvance { get; init; }

    /// <summary>Gets the last pressure advance value; defaults to the direct-drive or bowden ceiling.</summary>
    public decimal? EndPressureAdvance { get; init; }

    /// <summary>Gets the number of printed lines; defaults to eleven.</summary>
    public int? LineCount { get; init; }

    /// <summary>Gets the printed line length in millimetres; defaults to a nozzle-derived length.</summary>
    public decimal? LineLengthMillimeters { get; init; }
}

/// <summary>Trusted server-generated pressure advance pattern input, in seconds.</summary>
public sealed record PressureAdvancePatternCalibrationOptions : CalibrationMethodOptions
{
    /// <inheritdoc/>
    public override CalibrationMethod Method => CalibrationMethod.PressureAdvancePattern;

    /// <summary>Gets the first pressure advance value; defaults to zero.</summary>
    public decimal? StartPressureAdvance { get; init; }

    /// <summary>Gets the last pressure advance value; defaults to the direct-drive or bowden ceiling.</summary>
    public decimal? EndPressureAdvance { get; init; }

    /// <summary>Gets the step between pattern rows; defaults to the direct-drive or bowden step.</summary>
    public decimal? StepPressureAdvance { get; init; }

    /// <summary>Gets the number of corner pairs printed per row; defaults to three.</summary>
    public int? CornersPerRow { get; init; }
}

/// <summary>Retraction length sweep input, in millimetres.</summary>
public sealed record RetractionCalibrationOptions : CalibrationMethodOptions
{
    /// <inheritdoc/>
    public override CalibrationMethod Method => CalibrationMethod.Retraction;

    /// <summary>Gets the first retraction length; defaults to zero.</summary>
    public decimal? StartLengthMillimeters { get; init; }

    /// <summary>Gets the last retraction length; defaults to the direct-drive or bowden ceiling.</summary>
    public decimal? EndLengthMillimeters { get; init; }

    /// <summary>Gets the step between bands; defaults to 0.2 mm.</summary>
    public decimal? StepLengthMillimeters { get; init; }

    /// <summary>Gets the retraction speed used for every band; defaults to the process baseline.</summary>
    public int? RetractionSpeedMillimetersPerSecond { get; init; }
}

/// <summary>Maximum volumetric speed sweep input, in cubic millimetres per second.</summary>
public sealed record MaximumVolumetricSpeedCalibrationOptions : CalibrationMethodOptions
{
    /// <inheritdoc/>
    public override CalibrationMethod Method => CalibrationMethod.MaximumVolumetricSpeed;

    /// <summary>Gets the first volumetric speed; defaults to a quarter of the authoritative ceiling.</summary>
    public decimal? StartCubicMillimetersPerSecond { get; init; }

    /// <summary>Gets the last volumetric speed; defaults to the authoritative ceiling.</summary>
    public decimal? EndCubicMillimetersPerSecond { get; init; }

    /// <summary>Gets the step between bands; defaults to 1 mm³/s.</summary>
    public decimal? StepCubicMillimetersPerSecond { get; init; }
}

/// <summary>Shrinkage compensation input, in millimetres.</summary>
public sealed record ShrinkageCalibrationOptions : CalibrationMethodOptions
{
    /// <inheritdoc/>
    public override CalibrationMethod Method => CalibrationMethod.Shrinkage;

    /// <summary>Gets the nominal bar length measured after printing; defaults to 100 mm.</summary>
    public decimal? NominalLengthMillimeters { get; init; }

    /// <summary>Gets the bar width; defaults to eight extrusion widths.</summary>
    public decimal? BarWidthMillimeters { get; init; }
}

/// <summary>
/// Final verification input. The printed body comes from the linked asset, not from method options.
/// </summary>
public sealed record FinalVerificationCalibrationOptions : CalibrationMethodOptions
{
    /// <inheritdoc/>
    public override CalibrationMethod Method => CalibrationMethod.FinalVerification;

    /// <summary>Gets the stored model identity that must match the authoritative linked asset.</summary>
    public Guid Model3DId { get; init; }

    /// <summary>Gets the expected content digest that must match the authoritative linked asset.</summary>
    public string? ExpectedSha256 { get; init; }
}
