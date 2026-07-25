namespace Farm.Infrastructure.Dtos;

/// <summary>
/// Exposes platform capabilities so the frontend can hide/show features
/// that depend on native libraries (e.g. lib3mf, AssimpNetter) unavailable on ARM64.
/// </summary>
public record PlatformCapabilitiesDto
{
    /// <summary>Gets the API contract version returned by this server.</summary>
    public string ApiContractVersion { get; init; } = "1.0";

    /// <summary>Gets the oldest API contract version accepted from clients.</summary>
    public string MinimumSupportedApiContractVersion { get; init; } = "1.0";

    /// <summary>Gets the running PrintFarmer server version.</summary>
    public string ServerVersion { get; init; } = string.Empty;

    /// <summary>Gets the calibration API contract version, or null when it is unavailable.</summary>
    public string? CalibrationApiVersion { get; init; }

    /// <summary>Gets the calibration persistence schema version, or null when it is unavailable.</summary>
    public string? CalibrationSchemaVersion { get; init; }

    /// <summary>Gets the deployment shape used by this host.</summary>
    public string DeploymentMode { get; init; } = "monolith";

    /// <summary>Gets the runtime processor architecture (e.g. X64, Arm64).</summary>
    public string Architecture { get; init; } = string.Empty;

    /// <summary>Gets whether slicer integration is available.</summary>
    public bool SlicingEnabled { get; init; }

    /// <summary>Gets whether slicing has been configured or registered.</summary>
    public bool SlicingConfigured { get; init; }

    /// <summary>Gets whether a complete, compatible slicing path is currently usable.</summary>
    public bool SlicingOperational { get; init; }

    /// <summary>Gets whether calibration printer context is implemented and enabled.</summary>
    public bool CalibrationContextEnabled { get; init; }

    /// <summary>Gets whether calibration project persistence is implemented and enabled.</summary>
    public bool CalibrationPersistenceEnabled { get; init; }

    /// <summary>Gets whether calibration synchronization is implemented and enabled.</summary>
    public bool CalibrationSyncEnabled { get; init; }

    /// <summary>Gets whether calibration photos are implemented and enabled.</summary>
    public bool CalibrationPhotosEnabled { get; init; }

    /// <summary>Gets whether calibration profile history is implemented and enabled.</summary>
    public bool CalibrationProfileHistoryEnabled { get; init; }

    /// <summary>Gets whether calibration command generation is implemented and enabled.</summary>
    public bool CalibrationGenerationEnabled { get; init; }

    /// <summary>Gets whether calibration-specific slicing is operational.</summary>
    public bool CalibrationSlicingEnabled { get; init; }

    /// <summary>Gets whether calibration artifact promotion is implemented and enabled.</summary>
    public bool CalibrationArtifactPromotionEnabled { get; init; }

    /// <summary>Gets whether calibration queue dispatch is implemented and enabled.</summary>
    public bool CalibrationQueueEnabled { get; init; }

    /// <summary>Gets whether exact-job bed-clear acknowledgement is implemented and enabled.</summary>
    public bool CalibrationJobBoundBedClearEnabled { get; init; }

    /// <summary>Gets whether calibration event streaming is implemented and enabled.</summary>
    public bool CalibrationEventsEnabled { get; init; }

    /// <summary>Gets firmware families supported by the calibration contract.</summary>
    public IReadOnlyList<string> SupportedFirmwareFamilies { get; init; } = [];

    /// <summary>Gets G-code dialects supported by the calibration contract.</summary>
    public IReadOnlyList<string> SupportedGcodeDialects { get; init; } = [];

    /// <summary>Gets whether 3D model file support (STL, OBJ, STEP, 3MF) is available.</summary>
    public bool ModelFilesEnabled { get; init; }

    /// <summary>Gets whether server-side thumbnail generation is available.</summary>
    public bool ThumbnailGenerationEnabled { get; init; }

    /// <summary>Gets whether G-code upload is available. Always true (no native deps).</summary>
    public bool GcodeUploadEnabled { get; init; } = true;

    /// <summary>Gets an optional note explaining platform limitations.</summary>
    public string? PlatformNote { get; init; }

    /// <summary>Gets slicer engines supported by this deployment.</summary>
    public IReadOnlyList<SlicerEngineCapabilityDto> SupportedSlicerEngines { get; init; } = [];

    /// <summary>Gets truthful global calibration feature availability.</summary>
    public CalibrationFeatureCapabilitiesDto Calibration { get; init; } = new();

    /// <summary>Gets safe, canonical API and hub routes without deployment addresses.</summary>
    public IReadOnlyDictionary<string, string> Routes { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Gets stable upload and media limits.</summary>
    public CapabilityLimitsDto Limits { get; init; } = new();

    /// <summary>Gets accepted MIME types by input category.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> AcceptedMimeTypes { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    /// <summary>Gets supported calibration export format identifiers.</summary>
    public IReadOnlyList<string> SupportedExportFormats { get; init; } = [];

    /// <summary>Gets non-secret health metadata for compatible slicer workers.</summary>
    public CompatibleWorkerCapabilityDto HealthyCompatibleWorker { get; init; } = new();

    /// <summary>Gets structured reasons for configured features that are not operational.</summary>
    public IReadOnlyList<CapabilityUnavailableReasonDto> UnavailableReasons { get; init; } = [];

    /// <summary>
    /// Gets the caller's effective resource-action permissions.
    /// This is populated only by the authenticated calibration capability endpoint.
    /// </summary>
    public IReadOnlyList<string>? EffectivePermissions { get; init; }

    /// <summary>
    /// Gets caller-reachable calibration operations after permissions and dependencies are applied.
    /// This is populated only by the authenticated calibration capability endpoint.
    /// </summary>
    public EffectiveCalibrationCapabilitiesDto? EffectiveCapabilities { get; init; }
}

/// <summary>Describes a supported slicer engine and its compatibility contract.</summary>
public sealed record SlicerEngineCapabilityDto
{
    public string Type { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public string Distribution { get; init; } = string.Empty;

    public bool Supported { get; init; }
}

/// <summary>Describes globally implemented and operational calibration features.</summary>
public sealed record CalibrationFeatureCapabilitiesDto
{
    public bool ContextImplemented { get; init; }

    public bool CommandsImplemented { get; init; }

    public bool GenerationImplemented { get; init; }

    public bool QueueIntegrationImplemented { get; init; }

    public bool EventStreamImplemented { get; init; }

    public bool Operational { get; init; }
}

/// <summary>Describes request limits used by desktop clients.</summary>
public sealed record CapabilityLimitsDto
{
    public long ModelUploadMaxBytes { get; init; }

    public long PhotoUploadMaxBytes { get; init; }

    public long PhotoMaxPixels { get; init; }
}

/// <summary>Reports compatible worker health without identifiers or network configuration.</summary>
public sealed record CompatibleWorkerCapabilityDto
{
    public bool Available { get; init; }

    public int HealthyCount { get; init; }

    public int AvailableSlots { get; init; }

    public string Engine { get; init; } = "OrcaSlicer";

    public string RequiredVersion { get; init; } = string.Empty;

    public string Distribution { get; init; } = string.Empty;
}

/// <summary>Provides a stable machine-readable reason that a capability is unavailable.</summary>
public sealed record CapabilityUnavailableReasonDto
{
    public string Feature { get; init; } = string.Empty;

    public string Code { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;
}

/// <summary>Describes calibration operations the authenticated caller can currently reach.</summary>
public sealed record EffectiveCalibrationCapabilitiesDto
{
    public bool CanCreate { get; init; }

    public bool CanRead { get; init; }

    public bool CanUpdate { get; init; }

    public bool CanDelete { get; init; }

    public bool CanGenerate { get; init; }

    public bool CanPublish { get; init; }

    public bool CanSubmitSlicing { get; init; }

    public bool CanReadArtifacts { get; init; }

    public bool CanManageDispatchSettings { get; init; }
}
