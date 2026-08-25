using Farm.Web.Api.Services.Calibration.Generation;

namespace Farm.Web.Api.Tests.Services.Calibration.Generation;

/// <summary>
/// Runs the deterministic generation core end to end for a method, without any HTTP, worker, storage
/// or queue dependency.
/// </summary>
internal static class CalibrationGenerationPipeline
{
    /// <summary>The outcome of one pipeline run.</summary>
    /// <param name="Specification">The compiled specification.</param>
    /// <param name="Model">The validated model.</param>
    /// <param name="Plan">The compiled plan.</param>
    /// <param name="Program">The generated program.</param>
    /// <param name="Annotated">The annotated program and manifest.</param>
    /// <param name="Problems">Ordered rejection reasons; empty on success.</param>
    internal sealed record Result(
        CalibrationSpecification? Specification,
        CalibrationValidatedModel? Model,
        OrcaCalibrationPlan? Plan,
        KlipperCalibrationProgram? Program,
        AnnotatedCalibrationGcode? Annotated,
        IReadOnlyList<CalibrationGenerationProblem> Problems);

    public static CalibrationMethodOptions Options(CalibrationMethod method) => method switch
    {
        CalibrationMethod.Temperature => new TemperatureCalibrationOptions(),
        CalibrationMethod.FlowRatioCoarse or
        CalibrationMethod.FlowRatioFine or
        CalibrationMethod.FlowRatioHighRange => new FlowRatioCalibrationOptions(method),
        CalibrationMethod.FlowVerification => new FlowVerificationCalibrationOptions(),
        CalibrationMethod.PressureAdvanceTower => new PressureAdvanceTowerCalibrationOptions(),
        CalibrationMethod.PressureAdvanceLine => new PressureAdvanceLineCalibrationOptions(),
        CalibrationMethod.PressureAdvancePattern => new PressureAdvancePatternCalibrationOptions(),
        CalibrationMethod.Retraction => new RetractionCalibrationOptions(),
        CalibrationMethod.MaximumVolumetricSpeed =>
            new MaximumVolumetricSpeedCalibrationOptions(),
        CalibrationMethod.Shrinkage => new ShrinkageCalibrationOptions(),
        CalibrationMethod.FinalVerification => new FinalVerificationCalibrationOptions
        {
            Model3DId = CalibrationGenerationTestData.ModelId,
            ExpectedSha256 = ModelSha256,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unsupported method."),
    };

    public static byte[] ModelContent { get; } =
        CalibrationGenerationTestData.BinaryStlCuboid(20f, 20f, 10f);

    public static string ModelSha256 { get; } =
        CalibrationCanonicalJson.ComputeBytesSha256(ModelContent);

    public static CalibrationGenerationContext ContextFor(
        CalibrationMethod method,
        decimal nozzleDiameter = 0.4m,
        bool directDrive = true,
        int toolheadIndex = 0)
    {
        CalibrationGenerationContext context = CalibrationGenerationTestData.Context(
            nozzleDiameter,
            toolheadIndex,
            directDrive);

        return method == CalibrationMethod.FinalVerification
            ? context with
            {
                ImportedAsset = new CalibrationModelReference(
                    CalibrationGenerationTestData.ModelId,
                    ModelSha256,
                    CalibrationModelFormats.Stl,
                    "calibration-cube.stl",
                    ModelContent.Length,
                    "imported"),
            }
            : context;
    }

    public static CalibrationGenerationResult<CalibrationSpecification> CompileSpecification(
        CalibrationMethod method,
        decimal nozzleDiameter = 0.4m,
        bool directDrive = true,
        int toolheadIndex = 0) =>
        CalibrationGenerationTestData.Compiler().Compile(
            ContextFor(method, nozzleDiameter, directDrive, toolheadIndex),
            Options(method));

    public static Result Run(
        CalibrationMethod method,
        decimal nozzleDiameter = 0.4m,
        bool directDrive = true,
        int toolheadIndex = 0)
    {
        CalibrationGenerationResult<CalibrationSpecification> compiled =
            CompileSpecification(method, nozzleDiameter, directDrive, toolheadIndex);
        if (!compiled.IsValid)
        {
            return new Result(null, null, null, null, null, compiled.Problems);
        }

        CalibrationSpecification specification = compiled.Value!;
        CalibrationValidatedModel model = ValidateModel(specification, method);

        CalibrationGenerationResult<OrcaCalibrationPlan> planned =
            new OrcaCalibrationPlanCompiler().Compile(specification, model);
        if (!planned.IsValid)
        {
            return new Result(specification, model, null, null, null, planned.Problems);
        }

        CalibrationGenerationResult<KlipperCalibrationProgram> generated =
            new KlipperCalibrationGcodeGenerator().Generate(specification, planned.Value!);
        if (!generated.IsValid)
        {
            return new Result(
                specification,
                model,
                planned.Value,
                null,
                null,
                generated.Problems);
        }

        CalibrationGenerationResult<AnnotatedCalibrationGcode> annotated =
            new CalibrationGcodeAnnotator().Annotate(
                specification,
                planned.Value!,
                model,
                generated.Value!);

        return new Result(
            specification,
            model,
            planned.Value,
            generated.Value,
            annotated.Value,
            annotated.Problems);
    }

    public static CalibrationGenerationResult<CalibrationGcodeSafetyReport> Validate(
        Result run,
        CalibrationSafetyCheckpoint checkpoint,
        string? gcodeOverride = null,
        long? currentRevision = null) =>
        new CalibrationGcodeProgramValidator(
            new Farm.Web.Api.Services.Gcode.Safety.GcodeSafetyValidator())
            .Validate(new CalibrationGcodeSafetyRequest(
            run.Specification!,
            run.Plan!,
            run.Annotated!.Manifest,
            gcodeOverride ?? run.Annotated.Gcode,
            checkpoint,
            currentRevision ?? run.Specification!.Document.PrinterConfigurationRevision,
            CalibrationGenerationTestData.NowUtc));

    private static CalibrationValidatedModel ValidateModel(
        CalibrationSpecification specification,
        CalibrationMethod method)
    {
        CalibrationModelValidator validator = new();
        if (method == CalibrationMethod.FinalVerification)
        {
            CalibrationGenerationResult<CalibrationValidatedModel> imported =
                validator.ValidateImportedAssetAsync(
                    new FakeModelContentSource(
                        CalibrationGenerationTestData.ModelId,
                        ModelContent,
                        CalibrationModelFormats.Stl),
                    specification,
                    CancellationToken.None).GetAwaiter().GetResult();
            return imported.Value ?? throw new InvalidOperationException(
                "The pipeline could not validate the linked calibration asset.");
        }

        CalibrationGenerationResult<CalibrationValidatedModel> geometry =
            validator.ValidateGeneratedGeometryAsync(
                new CalibrationGeneratedGeometry(ModelContent, "calibration-body.stl"),
                specification,
                CancellationToken.None).GetAwaiter().GetResult();
        return geometry.Value ?? throw new InvalidOperationException(
            "The pipeline could not validate the generated calibration geometry.");
    }
}
