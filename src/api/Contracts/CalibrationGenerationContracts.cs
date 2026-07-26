namespace Farm.Web.Api.Contracts;

/// <summary>
/// The complete set of typed, versioned option values a calibration generation request may carry.
/// </summary>
/// <remarks>
/// Every member is a bounded scalar. The contract deliberately has no member that could carry a
/// command line, a G-code fragment, a slicer setting, a file system path, a URL, a renderer name, an
/// archive or mesh payload, so a caller cannot reach the slicer, the worker host or the file system
/// through this route. Options that do not belong to the selected method must be omitted; supplying
/// one is rejected with <c>422</c> and the offending field, never silently ignored.
/// </remarks>
public sealed class CalibrationMethodOptionsRequest
{
    /// <summary>First nozzle temperature of a temperature tower, in degrees Celsius.</summary>
    public int? StartCelsius { get; init; }

    /// <summary>Last nozzle temperature of a temperature tower, in degrees Celsius.</summary>
    public int? EndCelsius { get; init; }

    /// <summary>Temperature step between tower bands, in degrees Celsius.</summary>
    public int? StepCelsius { get; init; }

    /// <summary>First flow ratio of a flow sweep.</summary>
    public decimal? StartRatio { get; init; }

    /// <summary>Last flow ratio of a flow sweep.</summary>
    public decimal? EndRatio { get; init; }

    /// <summary>Flow ratio step between sweep bands.</summary>
    public decimal? StepRatio { get; init; }

    /// <summary>Single flow ratio printed by a flow verification pass.</summary>
    public decimal? FlowRatio { get; init; }

    /// <summary>First pressure advance value, in seconds.</summary>
    public decimal? StartPressureAdvance { get; init; }

    /// <summary>Last pressure advance value, in seconds.</summary>
    public decimal? EndPressureAdvance { get; init; }

    /// <summary>Pressure advance step, in seconds.</summary>
    public decimal? StepPressureAdvance { get; init; }

    /// <summary>Number of pressure advance lines emitted by the line method.</summary>
    public int? LineCount { get; init; }

    /// <summary>Length of one pressure advance line, in millimetres.</summary>
    public decimal? LineLengthMillimeters { get; init; }

    /// <summary>Number of corner pairs per pressure advance pattern row.</summary>
    public int? CornersPerRow { get; init; }

    /// <summary>First retraction length of a retraction sweep, in millimetres.</summary>
    public decimal? StartLengthMillimeters { get; init; }

    /// <summary>Last retraction length of a retraction sweep, in millimetres.</summary>
    public decimal? EndLengthMillimeters { get; init; }

    /// <summary>Retraction length step, in millimetres.</summary>
    public decimal? StepLengthMillimeters { get; init; }

    /// <summary>Retraction speed used by a retraction sweep, in millimetres per second.</summary>
    public int? RetractionSpeedMillimetersPerSecond { get; init; }

    /// <summary>First volumetric speed of a maximum volumetric speed sweep, in mm³/s.</summary>
    public decimal? StartCubicMillimetersPerSecond { get; init; }

    /// <summary>Last volumetric speed of a maximum volumetric speed sweep, in mm³/s.</summary>
    public decimal? EndCubicMillimetersPerSecond { get; init; }

    /// <summary>Volumetric speed step, in mm³/s.</summary>
    public decimal? StepCubicMillimetersPerSecond { get; init; }

    /// <summary>Nominal shrinkage bar length, in millimetres.</summary>
    public decimal? NominalLengthMillimeters { get; init; }

    /// <summary>Shrinkage bar width, in millimetres.</summary>
    public decimal? BarWidthMillimeters { get; init; }

    /// <summary>Stored model identity a final verification prints.</summary>
    public Guid? Model3DId { get; init; }

    /// <summary>Expected lowercase hexadecimal SHA-256 of the linked stored model content.</summary>
    public string? ExpectedSha256 { get; init; }
}

/// <summary>
/// Request body for starting or resuming the durable calibration generation saga of one attempt.
/// </summary>
/// <remarks>
/// The immutable attempt already fixes the calibration identity, so this request only restates it.
/// Anything that does not recompile to the attempt's stored specification digest is refused rather
/// than applied.
/// </remarks>
public sealed class CalibrationGenerateJobRequest
{
    /// <summary>Canonical calibration method name, for example <c>temperature</c>.</summary>
    public string Method { get; init; } = string.Empty;

    /// <summary>Method definition version the caller compiled its options against.</summary>
    public string DefinitionVersion { get; init; } = string.Empty;

    /// <summary>Typed, versioned method options.</summary>
    public CalibrationMethodOptionsRequest? Options { get; init; }

    /// <summary>
    /// Orchestration revision the caller observed. Supplying a stale revision fails with <c>412</c>.
    /// </summary>
    public long? BaseRevision { get; init; }
}

/// <summary>One structured, redacted reason a generation request was refused.</summary>
/// <param name="Code">Stable snake_case reason code.</param>
/// <param name="Field">Dotted path of the offending input, for example <c>options.startCelsius</c>.</param>
/// <param name="Message">Operator-facing explanation carrying no path, host, key or log text.</param>
public sealed record CalibrationGenerationProblemDto(string Code, string Field, string Message);

/// <summary>
/// Durable, redacted status of one calibration generation orchestration.
/// </summary>
/// <remarks>
/// The document intentionally carries identifiers, digests, counters and timestamps only. It never
/// exposes a storage path, a worker endpoint, an API key, a private URL or raw slicer log text.
/// </remarks>
public sealed record CalibrationOrchestrationStatusDto
{
    /// <summary>Durable orchestration identity.</summary>
    public required Guid Id { get; init; }

    /// <summary>Owning calibration project.</summary>
    public required Guid ProjectId { get; init; }

    /// <summary>Immutable calibration attempt.</summary>
    public required Guid AttemptId { get; init; }

    /// <summary>Idempotency operation key that owns the run.</summary>
    public required string OperationId { get; init; }

    /// <summary>Durable status name, for example <c>Running</c> or <c>Completed</c>.</summary>
    public required string Status { get; init; }

    /// <summary>Durable step name the saga last checkpointed.</summary>
    public required string CurrentStep { get; init; }

    /// <summary>Optimistic concurrency revision of the orchestration row.</summary>
    public required long Revision { get; init; }

    /// <summary>Number of safe retries already applied.</summary>
    public required int RetryCount { get; init; }

    /// <summary>UTC instant the next safe retry becomes due.</summary>
    public DateTime? NextRetryAtUtc { get; init; }

    /// <summary>UTC instant the current step started.</summary>
    public DateTime? StepStartedAtUtc { get; init; }

    /// <summary>Stable machine-readable failure code of the last failure.</summary>
    public string? LastErrorCode { get; init; }

    /// <summary>Ordered, redacted reasons behind the last failure.</summary>
    public IReadOnlyList<CalibrationGenerationProblemDto> Problems { get; init; } = [];

    /// <summary>Stored model identity the run slices.</summary>
    public Guid? Model3DId { get; init; }

    /// <summary>Submitted canonical slice job identity.</summary>
    public Guid? SliceJobId { get; init; }

    /// <summary>Worker that claimed and executed the slice job.</summary>
    public Guid? WorkerId { get; init; }

    /// <summary>Slicer artifact the worker produced.</summary>
    public Guid? SourceArtifactId { get; init; }

    /// <summary>Server-composed artifact that was safety validated and promoted.</summary>
    public Guid? FinalArtifactId { get; init; }

    /// <summary>Promoted G-code library file identity.</summary>
    public Guid? GcodeFileId { get; init; }

    /// <summary>SHA-256 of the recompiled canonical specification.</summary>
    public string? SpecificationSha256 { get; init; }

    /// <summary>SHA-256 of the compiled upstream-Orca plan manifest.</summary>
    public string? PlanManifestSha256 { get; init; }

    /// <summary>SHA-256 of the final annotated calibration G-code.</summary>
    public string? GcodeSha256 { get; init; }

    /// <summary>SHA-256 of the canonical calibration manifest.</summary>
    public string? ManifestSha256 { get; init; }

    /// <summary>Trusted generator version that produced the run.</summary>
    public string? GeneratorVersion { get; init; }

    /// <summary>Pinned slicer container digest attested by the accepted worker.</summary>
    public string? SlicerContainerDigest { get; init; }

    /// <summary>Pinned slicer binary digest attested by the accepted worker.</summary>
    public string? SlicerBinarySha256 { get; init; }

    /// <summary>Authenticated route that reports this orchestration's durable status.</summary>
    public required string StatusRoute { get; init; }

    /// <summary>UTC creation timestamp.</summary>
    public required DateTime CreatedAtUtc { get; init; }

    /// <summary>UTC timestamp of the last durable state change.</summary>
    public required DateTime UpdatedAtUtc { get; init; }

    /// <summary>UTC timestamp of the terminal transition.</summary>
    public DateTime? CompletedAtUtc { get; init; }
}
