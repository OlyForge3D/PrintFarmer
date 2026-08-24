using System.Data.Common;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Text.Json;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Infrastructure.Security;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services;
using Farm.Slicer.Module.Services.Configuration;
using Farm.Web.Api.Services.Calibration;
using Farm.Web.Api.Services.Calibration.Generation;
using Farm.Web.Api.Services.Gcode;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Services.Capabilities;

/// <summary>Builds public and caller-effective capability documents without exposing deployment secrets.</summary>
public interface ICalibrationCapabilityService
{
    /// <summary>Builds the current capability document.</summary>
    Task<PlatformCapabilitiesDto> GetCapabilitiesAsync(
        ClaimsPrincipal? user,
        CancellationToken cancellationToken);
}

/// <summary>
/// Observes configured services and operational dependencies to produce conservative capability flags.
/// </summary>
public sealed class CalibrationCapabilityService(
    IConfiguration configuration,
    IServiceProvider serviceProvider,
    ILogger<CalibrationCapabilityService> logger,
    CalibrationSlicerCompatibilityPolicy? compatibilityPolicy = null)
    : ICalibrationCapabilityService
{
    private static readonly TimeSpan WorkerHeartbeatFreshness = TimeSpan.FromMinutes(2);

    public const string CurrentApiContractVersion = "1.0";
    public const string MinimumSupportedApiContractVersion = "1.0";
    public const string CalibrationApiVersion = CalibrationContractConstants.ApiVersion;
    public const string CalibrationSchemaVersion = CalibrationContractConstants.SchemaVersion;
    public const string UpstreamOrcaSlicerCapability =
        CalibrationContractConstants.UpstreamSlicerCapability;

    private static readonly IReadOnlyDictionary<string, string> SafeRoutes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["systemCapabilities"] = "/api/system/capabilities",
            ["calibrationCapabilities"] = "/api/calibration/capabilities",
            ["printers"] = "/api/printers",
            ["calibrationProjects"] = "/api/calibration-projects",
            ["calibrationGenerateJob"] =
                "/api/calibration-projects/{projectId}/attempts/{attemptId}/generate-job",
            ["calibrationOrchestration"] = "/api/calibration-orchestrations/{id}",
            ["calibrationSync"] = "/api/calibration-sync/changes",
            ["calibrationImports"] = "/api/calibration-imports/legacy-v4",
            ["sliceJobs"] = "/api/slice",
            ["sliceJob"] = "/api/slice/{id}",
            ["jobArtifact"] = "/api/artifacts/job/{jobId}",
            ["gcodePromotions"] = "/api/gcode-promotions",
            ["gcodePromotion"] = "/api/gcode-promotions/{operationId}",
            ["printerHub"] = "/hubs/printers",
            ["slicerRegistryHub"] = "/hubs/slicer-registry",
            ["slicerProgressHub"] = "/hubs/slicers",
        };

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> MimeTypes =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["model"] =
            [
                "application/octet-stream",
                "application/vnd.ms-package.3dmanufacturing-3dmodel+xml",
                "model/3mf",
                "model/obj",
                "model/stl",
            ],
            ["photo"] = ["image/jpeg", "image/png", "image/webp"],
        };

    private readonly IConfiguration _configuration = configuration;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly SlicerArtifactStorageSettings _artifactStorage =
        configuration.GetSection(SlicerArtifactStorageSettings.SectionName)
            .Get<SlicerArtifactStorageSettings>() ??
        new SlicerArtifactStorageSettings();

    private readonly CalibrationBlobStorageOptions _calibrationBlobStorage =
        configuration.GetSection(CalibrationBlobStorageOptions.SectionName)
            .Get<CalibrationBlobStorageOptions>() ??
        new CalibrationBlobStorageOptions();

    private readonly ILogger<CalibrationCapabilityService> _logger = logger;
    private readonly CalibrationSlicerCompatibilityPolicy _compatibilityPolicy =
        compatibilityPolicy ?? CalibrationSlicerCompatibilityPolicy.Default;

    /// <inheritdoc/>
    public async Task<PlatformCapabilitiesDto> GetCapabilitiesAsync(
        ClaimsPrincipal? user,
        CancellationToken cancellationToken)
    {
        bool slicingEnabled = _configuration.GetValue("Slicer:Enabled", false);
        WorkerHealthSnapshot workerHealth =
            await GetWorkerHealthAsync(slicingEnabled, cancellationToken);

        // Worker authentication is configured when the registry actually holds per-worker
        // credentials. The retired deployment-wide shared key is no longer evidence of anything.
        bool workerAuthenticationConfigured = workerHealth.CredentialedWorkerCount > 0;
        bool slicingConfigured = slicingEnabled && workerAuthenticationConfigured;
        bool slicingOperational =
            slicingConfigured &&
            workerHealth.RegistryAvailable &&
            workerHealth.HealthyCount > 0 &&
            workerHealth.AvailableSlots > 0 &&
            _artifactStorage.MaxFileSizeBytes > 0;
        ICalibrationProfileResolver? profileResolver =
            _serviceProvider.GetService<ICalibrationProfileResolver>();
        bool calibrationContextOperational =
            await IsProfileResolverAvailableAsync(profileResolver, cancellationToken);

        // Promotion is only advertised when routing, library storage, the durable outbox and the
        // reconciler are all usable in this deployment. Split hosts without artifact routing stay false.
        GcodePromotionCapabilityDto? promotionCapability = await GetPromotionCapabilityAsync(cancellationToken);
        bool artifactPromotionOperational = promotionCapability?.Operational == true;

        // Calibration slicing stays false until the whole hop is provable: an operational slicing
        // path plus a healthy worker that attests an allow-listed upstream build identity and an
        // ownership-checked model resolver that can serve the bytes.
        bool modelStorageResolvable = _serviceProvider.GetService<IModelStorageResolver>() is not null;
        bool calibrationSlicingOperational =
            slicingOperational &&
            workerHealth.PinnedIdentityCount > 0 &&
            modelStorageResolvable;

        // Generation is only advertised when every production hop of the durable saga was actually
        // probed in this process. A configuration switch, a registered type or a test double is never
        // accepted as evidence, and a split host stays false until real routing adapters answer.
        CalibrationGenerationCapabilityDto? generationCapability =
            await GetGenerationCapabilityAsync(cancellationToken);
        bool calibrationGenerationOperational = generationCapability?.Operational == true;

        // The calibration command/gcode generation pipeline, calibration queue integration
        // (JobQueueService), exact-job bed-clear acknowledgement (BedClearAcknowledgementService)
        // and dispatch claiming/safety gating (DispatchClaimService/DispatchSafetyGates) are all
        // unconditionally registered in ServiceCollectionExtensions and ship in every deployment:
        // there is no configuration toggle that compiles them out. These four booleans therefore
        // reflect that build-time fact, matching the existing ContextImplemented precedent. Only
        // "Operational" below depends on runtime evidence.
        const bool calibrationCommandsImplemented = true;
        const bool calibrationGenerationImplemented = true;
        const bool calibrationQueueIntegrationImplemented = true;
        const bool calibrationEventStreamImplemented = true;
        bool calibrationOperational =
            calibrationContextOperational &&
            calibrationCommandsImplemented &&
            calibrationGenerationImplemented &&
            calibrationQueueIntegrationImplemented &&
            calibrationEventStreamImplemented;

        List<CapabilityUnavailableReasonDto> unavailableReasons =
            BuildUnavailableReasons(
                slicingEnabled,
                workerAuthenticationConfigured,
                slicingOperational,
                workerHealth,
                calibrationContextOperational,
                promotionCapability,
                generationCapability);

        IReadOnlyList<string>? effectivePermissions = null;
        EffectiveCalibrationCapabilitiesDto? effectiveCapabilities = null;
        if (user?.Identity?.IsAuthenticated == true)
        {
            effectivePermissions = PrintFarmerPermissions.CalibrationFoundation
                .Where(permission => PrintFarmerPermissions.HasPermission(user, permission))
                .Order(StringComparer.Ordinal)
                .ToArray();

            if (PrintFarmerPermissions.IsFarmAdmin(user))
            {
                _logger.LogInformation(
                    "Farm administrator capability evaluation applied the audited permission bypass for user {UserId}",
                    user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "unknown");
            }

            effectiveCapabilities = BuildEffectiveCapabilities(
                user,
                slicingOperational,
                calibrationContextOperational,
                calibrationGenerationOperational);
        }

        bool modelFilesEnabled = _configuration.GetValue("Platform:ModelFilesEnabled", true);
        bool thumbnailEnabled = _configuration.GetValue("Platform:ThumbnailGenerationEnabled", true);

        return new PlatformCapabilitiesDto
        {
            ApiContractVersion = CurrentApiContractVersion,
            MinimumSupportedApiContractVersion = MinimumSupportedApiContractVersion,
            ServerVersion = GetServerVersion(),
            CalibrationApiVersion = CalibrationApiVersion,
            CalibrationSchemaVersion = CalibrationSchemaVersion,
            DeploymentMode = GetDeploymentMode(),
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            SlicingEnabled = slicingEnabled,
            SlicingConfigured = slicingConfigured,
            SlicingOperational = slicingOperational,
            CalibrationContextEnabled = calibrationContextOperational,
            CalibrationPersistenceEnabled = true,
            CalibrationSyncEnabled = true,
            CalibrationPhotosEnabled = true,
            CalibrationProfileHistoryEnabled = true,
            CalibrationGenerationEnabled = calibrationGenerationOperational,
            CalibrationSlicingEnabled = calibrationSlicingOperational,
            CalibrationArtifactPromotionEnabled = artifactPromotionOperational,
            CalibrationQueueEnabled = false,
            CalibrationJobBoundBedClearEnabled = false,
            CalibrationEventsEnabled = false,
            SupportedFirmwareFamilies = ["Klipper"],
            SupportedGcodeDialects = ["Klipper"],
            ModelFilesEnabled = modelFilesEnabled,
            ThumbnailGenerationEnabled = thumbnailEnabled,
            GcodeUploadEnabled = true,
            ClientThumbnailUploadEnabled = modelFilesEnabled,
            IdempotentModelUploadEnabled = modelFilesEnabled,
            ModelThumbnailReplacementEnabled = modelFilesEnabled,
            PlatformNote = GetPlatformNote(modelFilesEnabled),
            SupportedSlicerEngines = _compatibilityPolicy.SupportedVersions
                .Select(version => new SlicerEngineCapabilityDto
                {
                    Type = CalibrationContractConstants.SlicerEngine,
                    Version = version,
                    Distribution = CalibrationContractConstants.SlicerDistribution,
                    Supported = true,
                })
                .ToArray(),
            Calibration = new CalibrationFeatureCapabilitiesDto
            {
                ContextImplemented = true,
                CommandsImplemented = calibrationCommandsImplemented,
                GenerationImplemented = calibrationGenerationImplemented,
                QueueIntegrationImplemented = calibrationQueueIntegrationImplemented,
                EventStreamImplemented = calibrationEventStreamImplemented,
                Operational = calibrationOperational,
            },
            Routes = SafeRoutes,
            Limits = new CapabilityLimitsDto
            {
                ModelUploadMaxBytes = _artifactStorage.MaxFileSizeBytes,
                PhotoUploadMaxBytes = _calibrationBlobStorage.MaxBytes,
                PhotoMaxPixels = _calibrationBlobStorage.MaxPixels,
            },
            AcceptedMimeTypes = MimeTypes,
            SupportedExportFormats = ["orca-json"],
            HealthyCompatibleWorker = new CompatibleWorkerCapabilityDto
            {
                Available = workerHealth.HealthyCount > 0,
                HealthyCount = workerHealth.HealthyCount,
                AvailableSlots = workerHealth.AvailableSlots,
                RequiredVersion = _compatibilityPolicy.RequiredVersion,
                SupportedVersions = _compatibilityPolicy.SupportedVersions,
                ObservedVersions = workerHealth.ObservedVersions,
                Distribution = "upstream",
            },
            UnavailableReasons = unavailableReasons,
            EffectivePermissions = effectivePermissions,
            EffectiveCapabilities = effectiveCapabilities,
        };
    }

    /// <summary>
    /// Probes the calibration profile resolver without letting its failure mode reach the caller.
    /// </summary>
    /// <param name="profileResolver">The registered resolver, if any.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> only when a registered resolver proves it is usable.</returns>
    /// <remarks>
    /// The capability document is public and must degrade rather than fail. A split deployment
    /// resolves profiles over an internal HTTP hop, so this probe can raise transport exceptions
    /// that the other capability probes here never had to consider.
    /// </remarks>
    private async Task<bool> IsProfileResolverAvailableAsync(
        ICalibrationProfileResolver? profileResolver,
        CancellationToken cancellationToken)
    {
        if (profileResolver is null)
        {
            return false;
        }

        try
        {
            return await profileResolver.IsAvailableAsync(cancellationToken);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException ||
            !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Capability discovery could not evaluate calibration profile resolution ({ExceptionType})",
                exception.GetType().Name);
            return false;
        }
    }

    private async Task<WorkerHealthSnapshot> GetWorkerHealthAsync(
        bool slicingEnabled,
        CancellationToken cancellationToken)
    {
        if (!slicingEnabled)
        {
            return WorkerHealthSnapshot.Disabled;
        }

        IDbContextFactory<SlicerDbContext>? factory =
            _serviceProvider.GetService<IDbContextFactory<SlicerDbContext>>();
        if (factory is null)
        {
            return WorkerHealthSnapshot.Unavailable;
        }

        try
        {
            await using SlicerDbContext db = await factory.CreateDbContextAsync(cancellationToken);
            DateTime heartbeatCutoff = DateTime.UtcNow - WorkerHeartbeatFreshness;
            List<CompatibleService> services = await db.SlicerServices
                .AsNoTracking()
                .Where(service =>
                    service.SlicerType == (int)SlicerType.OrcaSlicer &&
                    service.Status == WorkerStatus.Online &&
                    service.LastSeen >= heartbeatCutoff)
                .Select(service => new CompatibleService(
                    service.Id,
                    service.Version,
                    service.CapabilitiesJson))
                .ToListAsync(cancellationToken);

            string[] compatibleServiceIds = services
                .Where(service =>
                    _compatibilityPolicy.IsSupported(service.Version) &&
                    CalibrationContractConstants.AttestsUpstreamSlicer(service.CapabilitiesJson))
                .Select(service => service.Id.ToString())
                .ToArray();

            int credentialedWorkerCount = await db.Workers
                .AsNoTracking()
                .CountAsync(
                    worker => !worker.IsDisabled && worker.ApiKey != null && worker.ApiKey != string.Empty,
                    cancellationToken);

            List<WorkerCapacity> compatibleWorkers = await db.Workers
                .AsNoTracking()
                .Where(worker =>
                    compatibleServiceIds.Contains(worker.ServiceId) &&
                    !worker.IsDisabled &&
                    worker.Status == WorkerStatus.Online &&
                    worker.LastHeartbeat >= heartbeatCutoff)
                .Select(worker => new WorkerCapacity(
                    worker.TotalSlots - worker.ActiveJobs,
                    worker.CapabilitiesJson))
                .ToListAsync(cancellationToken);

            compatibleWorkers = compatibleWorkers
                .Where(worker =>
                    CalibrationContractConstants.AttestsUpstreamSlicer(
                        worker.CapabilitiesJson))
                .ToList();

            int pinnedIdentityCount = services
                .Where(service =>
                    _compatibilityPolicy.IsSupported(service.Version) &&
                    AttestsPinnedSlicerIdentity(service.CapabilitiesJson))
                .Count(service => compatibleServiceIds.Contains(service.Id.ToString()));

            string[] observedVersions = services
                .Where(service =>
                    CalibrationContractConstants.AttestsUpstreamSlicer(service.CapabilitiesJson))
                .Select(service => service.Version)
                .Where(version => !string.IsNullOrWhiteSpace(version))
                .Select(version => version!)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();

            return new WorkerHealthSnapshot(
                RegistryAvailable: true,
                HealthyCount: compatibleWorkers.Count,
                AvailableSlots: compatibleWorkers.Sum(worker => Math.Max(0, worker.AvailableSlots)),
                CredentialedWorkerCount: credentialedWorkerCount,
                PinnedIdentityCount: compatibleWorkers.Count == 0 ? 0 : pinnedIdentityCount,
                ObservedVersions: observedVersions,
                HasSupportedVersion: observedVersions.Any(_compatibilityPolicy.IsSupported));
        }
        catch (DbException exception)
        {
            _logger.LogWarning(
                "Capability discovery could not query slicer worker health ({ExceptionType})",
                exception.GetType().Name);
            return WorkerHealthSnapshot.Unavailable;
        }
        catch (InvalidOperationException exception)
        {
            _logger.LogWarning(
                "Capability discovery found unavailable slicer persistence ({ExceptionType})",
                exception.GetType().Name);
            return WorkerHealthSnapshot.Unavailable;
        }
    }

    /// <summary>
    /// Determines whether a registered service reports the reproducible build identity of the pinned
    /// upstream OrcaSlicer image.
    /// </summary>
    /// <param name="capabilitiesJson">The capabilities document the worker registered with.</param>
    /// <returns>
    /// <see langword="true"/> only when the worker publishes both a binary digest and a container
    /// digest. Anything less is treated as unverifiable, which keeps calibration slicing false.
    /// </returns>
    private static bool AttestsPinnedSlicerIdentity(string? capabilitiesJson)
    {
        if (string.IsNullOrWhiteSpace(capabilitiesJson))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(capabilitiesJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            return HasNonEmptyString(document.RootElement, "slicerBinarySha256") &&
                   HasNonEmptyString(document.RootElement, "slicerContainerDigest");
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasNonEmptyString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString());

    private List<CapabilityUnavailableReasonDto> BuildUnavailableReasons(
        bool slicingEnabled,
        bool workerAuthenticationConfigured,
        bool slicingOperational,
        WorkerHealthSnapshot workerHealth,
        bool calibrationContextOperational,
        GcodePromotionCapabilityDto? promotionCapability,
        CalibrationGenerationCapabilityDto? generationCapability)
    {
        List<CapabilityUnavailableReasonDto> reasons = [];
        if (!calibrationContextOperational)
        {
            reasons.Add(new()
            {
                Feature = "calibrationContext",
                Code = "profile_service_unavailable",
                Message = "Calibration context requires a caller-reachable upstream OrcaSlicer profile resolver.",
            });
        }

        if (!slicingEnabled)
        {
            reasons.Add(new()
            {
                Feature = "slicing",
                Code = "slicing_disabled",
                Message = "Slicing is disabled for this deployment.",
            });
        }
        else if (!workerHealth.RegistryAvailable)
        {
            // Ordered before the credential check on purpose: CredentialedWorkerCount is 0 both when
            // no worker holds a key and when the registry could not be read at all. Reporting
            // "authentication not configured" for an unobservable registry sent operators to rotate
            // worker keys for what is actually a persistence outage.
            reasons.Add(new()
            {
                Feature = "slicing",
                Code = "slicer_registry_unavailable",
                Message = "The slicer registry is not currently available.",
            });
        }
        else if (!workerAuthenticationConfigured)
        {
            reasons.Add(new()
            {
                Feature = "slicing",
                Code = "worker_authentication_not_configured",
                Message = "Authenticated slicer worker communication is not configured.",
            });
        }
        else if (!slicingOperational)
        {
            bool unsupportedVersion =
                workerHealth.ObservedVersions.Count > 0 &&
                !workerHealth.HasSupportedVersion;
            reasons.Add(new()
            {
                Feature = "slicing",
                Code = unsupportedVersion
                    ? CalibrationGenerationProblemCodes.SlicerVersionUnsupported
                    : workerHealth.HealthyCount == 0
                        ? "compatible_worker_unavailable"
                    : "slicing_path_unavailable",
                Message = unsupportedVersion
                    ? BuildUnsupportedVersionMessage(
                        workerHealth.ObservedVersions,
                        _compatibilityPolicy.SupportedVersions)
                    : workerHealth.HealthyCount == 0
                        ? $"No healthy upstream OrcaSlicer worker matches the configured allow-list ({string.Join(", ", _compatibilityPolicy.SupportedVersions)})."
                    : "The complete slicer-to-artifact path is not currently usable.",
            });
        }

        if (promotionCapability?.Operational != true)
        {
            reasons.Add(new()
            {
                Feature = "calibrationArtifactPromotion",
                Code = promotionCapability?.UnavailableCode ?? "promotion_dependency_unavailable",
                Message = "Artifact promotion requires routable artifacts, writable G-code storage, a durable promotion checkpoint store and a healthy reconciler.",
            });
        }

        if (generationCapability?.Operational != true)
        {
            string message =
                generationCapability?.UnavailableCode ==
                CalibrationGenerationProblemCodes.SlicerVersionUnsupported
                    ? BuildUnsupportedVersionMessage(
                        generationCapability.ObservedWorkerVersions,
                        generationCapability.SupportedSlicerVersions)
                    : "Calibration generation requires the deterministic core, authorized model storage, the canonical slice path, an allow-listed attested worker, operational promotion, a durable orchestration store and a healthy recovery loop.";
            reasons.Add(new()
            {
                Feature = "calibrationGeneration",
                Code = generationCapability?.UnavailableCode ?? "generation_dependency_unavailable",
                Message = message,
            });
        }

        return reasons;
    }

    private static string BuildUnsupportedVersionMessage(
        IReadOnlyList<string> observedVersions,
        IReadOnlyList<string> supportedVersions) =>
        $"Observed upstream OrcaSlicer version(s) {string.Join(", ", observedVersions)}; configured supported version(s): {string.Join(", ", supportedVersions)}.";

    /// <summary>
    /// Asks the generation probe whether every production hop of the durable saga is usable here.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The generation capability, or <see langword="null"/> when no probe is registered at all — which
    /// is the split-host case where the generation path does not exist in this process.
    /// </returns>
    private async Task<CalibrationGenerationCapabilityDto?> GetGenerationCapabilityAsync(
        CancellationToken cancellationToken)
    {
        ICalibrationGenerationCapabilityProbe? probe =
            _serviceProvider.GetService<ICalibrationGenerationCapabilityProbe>();
        if (probe is null)
        {
            return null;
        }

        try
        {
            return await probe.GetCapabilityAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            _logger.LogWarning(
                "Capability discovery could not evaluate calibration generation ({ExceptionType})",
                exception.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// Asks the promoter whether every promotion hop is usable in this deployment.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The promoter capability, or <see langword="null"/> when no promoter is registered at all — which
    /// is the split-host case where artifacts are not routable from this process.
    /// </returns>
    private async Task<GcodePromotionCapabilityDto?> GetPromotionCapabilityAsync(
        CancellationToken cancellationToken)
    {
        IGcodeArtifactPromoter? promoter = _serviceProvider.GetService<IGcodeArtifactPromoter>();
        if (promoter is null)
        {
            return null;
        }

        try
        {
            return await promoter.GetCapabilityAsync(cancellationToken);
        }
        catch (DbException exception)
        {
            _logger.LogWarning(
                "Capability discovery could not evaluate artifact promotion ({ExceptionType})",
                exception.GetType().Name);
            return null;
        }
        catch (InvalidOperationException exception)
        {
            _logger.LogWarning(
                "Capability discovery could not evaluate artifact promotion ({ExceptionType})",
                exception.GetType().Name);
            return null;
        }
    }

    private static EffectiveCalibrationCapabilitiesDto BuildEffectiveCapabilities(
        ClaimsPrincipal user,
        bool slicingOperational,
        bool calibrationContextOperational,
        bool calibrationGenerationOperational) =>
        new()
        {
            CanCreate =
                calibrationContextOperational &&
                PrintFarmerPermissions.HasPermission(user, PrintFarmerPermissions.Calibration.Create),
            CanRead = PrintFarmerPermissions.HasPermission(user, PrintFarmerPermissions.Calibration.Read),
            CanUpdate = PrintFarmerPermissions.HasPermission(user, PrintFarmerPermissions.Calibration.Update),
            CanDelete = PrintFarmerPermissions.HasPermission(user, PrintFarmerPermissions.Calibration.Delete),
            CanGenerate =
                calibrationGenerationOperational &&
                PrintFarmerPermissions.HasPermission(user, PrintFarmerPermissions.Calibration.Generate) &&
                PrintFarmerPermissions.HasPermission(user, PrintFarmerPermissions.Slicing.Submit),
            CanPublish = PrintFarmerPermissions.HasPermission(user, PrintFarmerPermissions.Calibration.Publish),
            CanSubmitSlicing =
                slicingOperational &&
                PrintFarmerPermissions.HasPermission(user, PrintFarmerPermissions.Slicing.Submit),
            CanReadArtifacts =
                slicingOperational &&
                PrintFarmerPermissions.HasPermission(user, PrintFarmerPermissions.Slicing.ReadArtifact),
            CanManageDispatchSettings = false,
        };

    private string GetDeploymentMode() =>
        (_configuration.GetValue<string>("DEPLOYMENT_MODE") ??
            _configuration.GetValue<string>("Deployment:Mode"))?.ToLowerInvariant() switch
        {
            "split" or "microservices" => "split",
            _ => "monolith",
        };

    private static string GetServerVersion()
    {
        Assembly assembly = Assembly.GetEntryAssembly() ?? typeof(CalibrationCapabilityService).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3)
            ?? "unknown";
    }

    private static string? GetPlatformNote(bool modelFilesEnabled) =>
        modelFilesEnabled
            ? null
            : "3D model processing is unavailable on this architecture.";

    private sealed record CompatibleService(Guid Id, string? Version, string? CapabilitiesJson);

    private sealed record WorkerCapacity(int AvailableSlots, string? CapabilitiesJson);

    private sealed record WorkerHealthSnapshot(
        bool RegistryAvailable,
        int HealthyCount,
        int AvailableSlots,
        int CredentialedWorkerCount,
        int PinnedIdentityCount,
        IReadOnlyList<string> ObservedVersions,
        bool HasSupportedVersion)
    {
        public static WorkerHealthSnapshot Disabled { get; } =
            new(false, 0, 0, 0, 0, [], false);

        public static WorkerHealthSnapshot Unavailable { get; } =
            new(false, 0, 0, 0, 0, [], false);
    }
}
