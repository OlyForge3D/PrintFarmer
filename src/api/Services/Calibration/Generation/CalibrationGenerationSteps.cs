namespace Farm.Web.Api.Services.Calibration.Generation;

/// <summary>
/// The durable step names a calibration generation orchestration checkpoints.
/// </summary>
/// <remarks>
/// Each name is persisted on the orchestration row before the side effect it describes is attempted,
/// so a restart always knows exactly which effect may already have happened.
/// </remarks>
public static class CalibrationGenerationSteps
{
    /// <summary>The attempt aggregate created the orchestration; generation has not started.</summary>
    public const string Created = "created";

    /// <summary>The immutable context and specification are being re-verified.</summary>
    public const string ValidatingContext = "validating-context";

    /// <summary>The trusted body or the linked stored model is being resolved.</summary>
    public const string ResolvingModel = "resolving-model";

    /// <summary>The exact native upstream-Orca plan is being compiled.</summary>
    public const string CompilingPlan = "compiling-plan";

    /// <summary>The canonical slice job is being submitted.</summary>
    public const string SubmittingSliceJob = "submitting-slice-job";

    /// <summary>The pinned worker is claiming or executing the slice job.</summary>
    public const string AwaitingWorker = "awaiting-worker";

    /// <summary>The completed worker artifact is being verified.</summary>
    public const string VerifyingArtifact = "verifying-artifact";

    /// <summary>The final annotated program is being composed and safety validated.</summary>
    public const string ComposingGcode = "composing-gcode";

    /// <summary>The verified artifact is being promoted into the G-code library.</summary>
    public const string Promoting = "promoting";

    /// <summary>The run reached a durable terminal success.</summary>
    public const string Completed = "completed";

    /// <summary>The run reached a durable terminal failure.</summary>
    public const string Failed = "failed";

    /// <summary>The run was cancelled before it owned work in another context.</summary>
    public const string Cancelled = "cancelled";
}
