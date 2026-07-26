using Farm.Web.Api.Contracts;

namespace Farm.Web.Api.Services.Calibration.Generation;

/// <summary>
/// Turns the flat, typed request payload into the versioned method options record.
/// </summary>
/// <remarks>
/// The binder is fail closed in both directions: an unknown method, an unknown definition version, an
/// option that the selected method does not define, or a malformed digest is a rejection with the
/// offending field. Nothing is coerced, defaulted or ignored, so a caller can never smuggle a value
/// into a method that does not declare it.
/// </remarks>
public static class CalibrationMethodOptionsBinder
{
    private static readonly Dictionary<CalibrationMethod, string[]> AllowedFields =
        new()
        {
            [CalibrationMethod.Temperature] = ["startCelsius", "endCelsius", "stepCelsius"],
            [CalibrationMethod.FlowRatioCoarse] = ["startRatio", "endRatio", "stepRatio"],
            [CalibrationMethod.FlowRatioFine] = ["startRatio", "endRatio", "stepRatio"],
            [CalibrationMethod.FlowRatioHighRange] = ["startRatio", "endRatio", "stepRatio"],
            [CalibrationMethod.FlowVerification] = ["flowRatio"],
            [CalibrationMethod.PressureAdvanceTower] =
                ["startPressureAdvance", "endPressureAdvance", "stepPressureAdvance"],
            [CalibrationMethod.PressureAdvanceLine] =
                ["startPressureAdvance", "endPressureAdvance", "lineCount", "lineLengthMillimeters"],
            [CalibrationMethod.PressureAdvancePattern] =
                ["startPressureAdvance", "endPressureAdvance", "stepPressureAdvance", "cornersPerRow"],
            [CalibrationMethod.Retraction] =
            [
                "startLengthMillimeters",
                "endLengthMillimeters",
                "stepLengthMillimeters",
                "retractionSpeedMillimetersPerSecond",
            ],
            [CalibrationMethod.MaximumVolumetricSpeed] =
            [
                "startCubicMillimetersPerSecond",
                "endCubicMillimetersPerSecond",
                "stepCubicMillimetersPerSecond",
            ],
            [CalibrationMethod.Shrinkage] = ["nominalLengthMillimeters", "barWidthMillimeters"],
            [CalibrationMethod.FinalVerification] = ["model3DId", "expectedSha256"],
        };

    /// <summary>
    /// Binds a request payload to typed method options.
    /// </summary>
    /// <param name="method">Canonical calibration method name.</param>
    /// <param name="definitionVersion">Method definition version supplied by the caller.</param>
    /// <param name="options">The typed option payload; may be <see langword="null"/> for defaults.</param>
    /// <returns>The bound options, or the ordered rejection reasons.</returns>
    /// <example>
    /// <code>
    /// CalibrationGenerationResult&lt;CalibrationMethodOptions&gt; bound =
    ///     CalibrationMethodOptionsBinder.Bind("temperature", "1.0", request.Options);
    /// </code>
    /// </example>
    public static CalibrationGenerationResult<CalibrationMethodOptions> Bind(
        string? method,
        string? definitionVersion,
        CalibrationMethodOptionsRequest? options)
    {
        if (!CalibrationMethodNames.TryParse(method, out CalibrationMethod parsed))
        {
            return CalibrationGenerationResults.Failure<CalibrationMethodOptions>(
                CalibrationGenerationProblemCodes.MethodUnsupported,
                "method",
                "The calibration method is not one of the supported canonical method names.");
        }

        if (!string.Equals(
                definitionVersion,
                CalibrationMethodOptions.CurrentDefinitionVersion,
                StringComparison.Ordinal))
        {
            return CalibrationGenerationResults.Failure<CalibrationMethodOptions>(
                CalibrationGenerationProblemCodes.MethodDefinitionVersionUnsupported,
                "definitionVersion",
                "The method definition version is not supported by this server build.");
        }

        CalibrationMethodOptionsRequest payload = options ?? new CalibrationMethodOptionsRequest();
        List<CalibrationGenerationProblem> problems = [];
        RejectForeignFields(parsed, payload, problems);
        if (problems.Count > 0)
        {
            return CalibrationGenerationResults.Failure<CalibrationMethodOptions>(problems);
        }

        CalibrationMethodOptions bound = Build(parsed, payload, problems);
        return problems.Count > 0
            ? CalibrationGenerationResults.Failure<CalibrationMethodOptions>(problems)
            : CalibrationGenerationResults.Success(bound);
    }

    private static void RejectForeignFields(
        CalibrationMethod method,
        CalibrationMethodOptionsRequest payload,
        List<CalibrationGenerationProblem> problems)
    {
        HashSet<string> allowed = new(AllowedFields[method], StringComparer.Ordinal);
        foreach ((string field, bool supplied) in Supplied(payload))
        {
            if (supplied && !allowed.Contains(field))
            {
                problems.Add(new(
                    CalibrationGenerationProblemCodes.OptionNotAllowedForMethod,
                    $"options.{field}",
                    "The selected calibration method does not define this option."));
            }
        }
    }

    private static IEnumerable<(string Field, bool Supplied)> Supplied(
        CalibrationMethodOptionsRequest payload)
    {
        yield return ("startCelsius", payload.StartCelsius.HasValue);
        yield return ("endCelsius", payload.EndCelsius.HasValue);
        yield return ("stepCelsius", payload.StepCelsius.HasValue);
        yield return ("startRatio", payload.StartRatio.HasValue);
        yield return ("endRatio", payload.EndRatio.HasValue);
        yield return ("stepRatio", payload.StepRatio.HasValue);
        yield return ("flowRatio", payload.FlowRatio.HasValue);
        yield return ("startPressureAdvance", payload.StartPressureAdvance.HasValue);
        yield return ("endPressureAdvance", payload.EndPressureAdvance.HasValue);
        yield return ("stepPressureAdvance", payload.StepPressureAdvance.HasValue);
        yield return ("lineCount", payload.LineCount.HasValue);
        yield return ("lineLengthMillimeters", payload.LineLengthMillimeters.HasValue);
        yield return ("cornersPerRow", payload.CornersPerRow.HasValue);
        yield return ("startLengthMillimeters", payload.StartLengthMillimeters.HasValue);
        yield return ("endLengthMillimeters", payload.EndLengthMillimeters.HasValue);
        yield return ("stepLengthMillimeters", payload.StepLengthMillimeters.HasValue);
        yield return (
            "retractionSpeedMillimetersPerSecond",
            payload.RetractionSpeedMillimetersPerSecond.HasValue);
        yield return ("startCubicMillimetersPerSecond", payload.StartCubicMillimetersPerSecond.HasValue);
        yield return ("endCubicMillimetersPerSecond", payload.EndCubicMillimetersPerSecond.HasValue);
        yield return ("stepCubicMillimetersPerSecond", payload.StepCubicMillimetersPerSecond.HasValue);
        yield return ("nominalLengthMillimeters", payload.NominalLengthMillimeters.HasValue);
        yield return ("barWidthMillimeters", payload.BarWidthMillimeters.HasValue);
        yield return ("model3DId", payload.Model3DId.HasValue);
        yield return ("expectedSha256", !string.IsNullOrWhiteSpace(payload.ExpectedSha256));
    }

    private static CalibrationMethodOptions Build(
        CalibrationMethod method,
        CalibrationMethodOptionsRequest payload,
        List<CalibrationGenerationProblem> problems) => method switch
        {
            CalibrationMethod.Temperature => new TemperatureCalibrationOptions
            {
                StartCelsius = payload.StartCelsius,
                EndCelsius = payload.EndCelsius,
                StepCelsius = payload.StepCelsius,
            },
            CalibrationMethod.FlowRatioCoarse or
            CalibrationMethod.FlowRatioFine or
            CalibrationMethod.FlowRatioHighRange => new FlowRatioCalibrationOptions(method)
            {
                StartRatio = payload.StartRatio,
                EndRatio = payload.EndRatio,
                StepRatio = payload.StepRatio,
            },
            CalibrationMethod.FlowVerification => new FlowVerificationCalibrationOptions
            {
                FlowRatio = payload.FlowRatio,
            },
            CalibrationMethod.PressureAdvanceTower => new PressureAdvanceTowerCalibrationOptions
            {
                StartPressureAdvance = payload.StartPressureAdvance,
                EndPressureAdvance = payload.EndPressureAdvance,
                StepPressureAdvance = payload.StepPressureAdvance,
            },
            CalibrationMethod.PressureAdvanceLine => new PressureAdvanceLineCalibrationOptions
            {
                StartPressureAdvance = payload.StartPressureAdvance,
                EndPressureAdvance = payload.EndPressureAdvance,
                LineCount = payload.LineCount,
                LineLengthMillimeters = payload.LineLengthMillimeters,
            },
            CalibrationMethod.PressureAdvancePattern => new PressureAdvancePatternCalibrationOptions
            {
                StartPressureAdvance = payload.StartPressureAdvance,
                EndPressureAdvance = payload.EndPressureAdvance,
                StepPressureAdvance = payload.StepPressureAdvance,
                CornersPerRow = payload.CornersPerRow,
            },
            CalibrationMethod.Retraction => new RetractionCalibrationOptions
            {
                StartLengthMillimeters = payload.StartLengthMillimeters,
                EndLengthMillimeters = payload.EndLengthMillimeters,
                StepLengthMillimeters = payload.StepLengthMillimeters,
                RetractionSpeedMillimetersPerSecond = payload.RetractionSpeedMillimetersPerSecond,
            },
            CalibrationMethod.MaximumVolumetricSpeed => new MaximumVolumetricSpeedCalibrationOptions
            {
                StartCubicMillimetersPerSecond = payload.StartCubicMillimetersPerSecond,
                EndCubicMillimetersPerSecond = payload.EndCubicMillimetersPerSecond,
                StepCubicMillimetersPerSecond = payload.StepCubicMillimetersPerSecond,
            },
            CalibrationMethod.Shrinkage => new ShrinkageCalibrationOptions
            {
                NominalLengthMillimeters = payload.NominalLengthMillimeters,
                BarWidthMillimeters = payload.BarWidthMillimeters,
            },
            _ => BuildFinalVerification(payload, problems),
        };

    private static FinalVerificationCalibrationOptions BuildFinalVerification(
        CalibrationMethodOptionsRequest payload,
        List<CalibrationGenerationProblem> problems)
    {
        if (payload.Model3DId is not { } modelId || modelId == Guid.Empty)
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.LinkedAssetMissing,
                "options.model3DId",
                "Final verification requires the stored model identity it prints."));
        }

        if (!string.IsNullOrWhiteSpace(payload.ExpectedSha256) && !IsHexDigest(payload.ExpectedSha256))
        {
            problems.Add(new(
                CalibrationGenerationProblemCodes.OptionValueInvalid,
                "options.expectedSha256",
                "The expected content digest must be a 64 character hexadecimal SHA-256."));
        }

        return new FinalVerificationCalibrationOptions
        {
            Model3DId = payload.Model3DId ?? Guid.Empty,
            ExpectedSha256 = string.IsNullOrWhiteSpace(payload.ExpectedSha256)
                ? null
                : payload.ExpectedSha256.Trim().ToLowerInvariant(),
        };
    }

    private static bool IsHexDigest(string value)
    {
        string trimmed = value.Trim();
        return trimmed.Length == 64 && trimmed.All(Uri.IsHexDigit);
    }
}
