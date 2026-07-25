using System.Data.Common;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Text.Json;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Security;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services.Configuration;
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
    ILogger<CalibrationCapabilityService> logger) : ICalibrationCapabilityService
{
    private static readonly TimeSpan WorkerHeartbeatFreshness = TimeSpan.FromMinutes(2);

    public const string CurrentApiContractVersion = "1.0";
    public const string MinimumSupportedApiContractVersion = "1.0";
    public const string CalibrationApiVersion = "1.0";
    public const string CalibrationSchemaVersion = "1.0";
    public const string UpstreamOrcaSlicerVersion = "2.3.1";
    public const string UpstreamOrcaSlicerCapability = "orcaslicer-upstream";

    private static readonly IReadOnlyList<SlicerEngineCapabilityDto> SlicerEngines =
    [
        new()
        {
            Type = "OrcaSlicer",
            Version = UpstreamOrcaSlicerVersion,
            Distribution = "upstream",
            Supported = true,
        },
    ];

    private static readonly IReadOnlyDictionary<string, string> SafeRoutes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["systemCapabilities"] = "/api/system/capabilities",
            ["calibrationCapabilities"] = "/api/calibration/capabilities",
            ["printers"] = "/api/printers",
            ["sliceJobs"] = "/api/slice-jobs",
            ["sliceJob"] = "/api/slice-jobs/{id}",
            ["jobArtifact"] = "/api/artifacts/job/{jobId}",
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
            ["photo"] = [],
        };

    private readonly IConfiguration _configuration = configuration;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly SlicerArtifactStorageSettings _artifactStorage =
        configuration.GetSection(SlicerArtifactStorageSettings.SectionName)
            .Get<SlicerArtifactStorageSettings>() ??
        new SlicerArtifactStorageSettings();

    private readonly ILogger<CalibrationCapabilityService> _logger = logger;

    /// <inheritdoc/>
    public async Task<PlatformCapabilitiesDto> GetCapabilitiesAsync(
        ClaimsPrincipal? user,
        CancellationToken cancellationToken)
    {
        bool slicingEnabled = _configuration.GetValue("Slicer:Enabled", false);
        bool workerAuthenticationConfigured =
            !string.IsNullOrWhiteSpace(_configuration["WorkerAuth:SharedKey"]) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WORKER_SHARED_API_KEY"));
        bool slicingConfigured = slicingEnabled && workerAuthenticationConfigured;
        WorkerHealthSnapshot workerHealth =
            await GetWorkerHealthAsync(slicingConfigured, cancellationToken);
        bool slicingOperational =
            slicingConfigured &&
            workerHealth.RegistryAvailable &&
            workerHealth.HealthyCount > 0 &&
            workerHealth.AvailableSlots > 0 &&
            _artifactStorage.MaxFileSizeBytes > 0;

        List<CapabilityUnavailableReasonDto> unavailableReasons =
            BuildUnavailableReasons(
                slicingEnabled,
                workerAuthenticationConfigured,
                slicingOperational,
                workerHealth);

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
                calibrationOperational: false);
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
            CalibrationContextEnabled = false,
            CalibrationPersistenceEnabled = false,
            CalibrationSyncEnabled = false,
            CalibrationPhotosEnabled = false,
            CalibrationProfileHistoryEnabled = false,
            CalibrationGenerationEnabled = false,
            CalibrationSlicingEnabled = false,
            CalibrationArtifactPromotionEnabled = false,
            CalibrationQueueEnabled = false,
            CalibrationJobBoundBedClearEnabled = false,
            CalibrationEventsEnabled = false,
            SupportedFirmwareFamilies = ["Klipper"],
            SupportedGcodeDialects = ["Klipper"],
            ModelFilesEnabled = modelFilesEnabled,
            ThumbnailGenerationEnabled = thumbnailEnabled,
            GcodeUploadEnabled = true,
            PlatformNote = GetPlatformNote(modelFilesEnabled),
            SupportedSlicerEngines = SlicerEngines,
            Calibration = new CalibrationFeatureCapabilitiesDto(),
            Routes = SafeRoutes,
            Limits = new CapabilityLimitsDto
            {
                ModelUploadMaxBytes = _artifactStorage.MaxFileSizeBytes,
                PhotoUploadMaxBytes = 0,
                PhotoMaxPixels = 0,
            },
            AcceptedMimeTypes = MimeTypes,
            SupportedExportFormats = [],
            HealthyCompatibleWorker = new CompatibleWorkerCapabilityDto
            {
                Available = workerHealth.HealthyCount > 0,
                HealthyCount = workerHealth.HealthyCount,
                AvailableSlots = workerHealth.AvailableSlots,
                RequiredVersion = UpstreamOrcaSlicerVersion,
                Distribution = "upstream",
            },
            UnavailableReasons = unavailableReasons,
            EffectivePermissions = effectivePermissions,
            EffectiveCapabilities = effectiveCapabilities,
        };
    }

    private async Task<WorkerHealthSnapshot> GetWorkerHealthAsync(
        bool slicingConfigured,
        CancellationToken cancellationToken)
    {
        if (!slicingConfigured)
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
                    IsCompatibleVersion(service.Version) &&
                    AttestsUpstreamOrcaSlicer(service.CapabilitiesJson))
                .Select(service => service.Id.ToString())
                .ToArray();

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
                .Where(worker => AttestsUpstreamOrcaSlicer(worker.CapabilitiesJson))
                .ToList();

            return new WorkerHealthSnapshot(
                RegistryAvailable: true,
                HealthyCount: compatibleWorkers.Count,
                AvailableSlots: compatibleWorkers.Sum(worker => Math.Max(0, worker.AvailableSlots)));
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

    private static bool IsCompatibleVersion(string? version) =>
        string.Equals(version, UpstreamOrcaSlicerVersion, StringComparison.Ordinal) ||
        (version?.StartsWith($"{UpstreamOrcaSlicerVersion}+", StringComparison.Ordinal) ?? false);

    private static bool AttestsUpstreamOrcaSlicer(string? capabilitiesJson)
    {
        if (string.IsNullOrWhiteSpace(capabilitiesJson))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(capabilitiesJson);
            JsonElement capabilities = document.RootElement.ValueKind switch
            {
                JsonValueKind.Array => document.RootElement,
                JsonValueKind.Object when document.RootElement.TryGetProperty(
                    "capabilities",
                    out JsonElement value) => value,
                _ => default,
            };

            return capabilities.ValueKind == JsonValueKind.Array &&
                   capabilities.EnumerateArray().Any(value =>
                       value.ValueKind == JsonValueKind.String &&
                       string.Equals(
                           value.GetString(),
                           UpstreamOrcaSlicerCapability,
                           StringComparison.Ordinal));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static List<CapabilityUnavailableReasonDto> BuildUnavailableReasons(
        bool slicingEnabled,
        bool workerAuthenticationConfigured,
        bool slicingOperational,
        WorkerHealthSnapshot workerHealth)
    {
        List<CapabilityUnavailableReasonDto> reasons =
        [
            new()
            {
                Feature = "calibration",
                Code = "calibration_domain_not_implemented",
                Message = "Calibration context, command generation, and dispatch are not implemented by this foundation.",
            },
        ];

        if (!slicingEnabled)
        {
            reasons.Add(new()
            {
                Feature = "slicing",
                Code = "slicing_disabled",
                Message = "Slicing is disabled for this deployment.",
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
        else if (!workerHealth.RegistryAvailable)
        {
            reasons.Add(new()
            {
                Feature = "slicing",
                Code = "slicer_registry_unavailable",
                Message = "The slicer registry is not currently available.",
            });
        }
        else if (!slicingOperational)
        {
            reasons.Add(new()
            {
                Feature = "slicing",
                Code = workerHealth.HealthyCount == 0
                    ? "compatible_worker_unavailable"
                    : "slicing_path_unavailable",
                Message = workerHealth.HealthyCount == 0
                    ? $"No healthy upstream OrcaSlicer {UpstreamOrcaSlicerVersion} worker is available."
                    : "The complete slicer-to-artifact path is not currently usable.",
            });
        }

        return reasons;
    }

    private static EffectiveCalibrationCapabilitiesDto BuildEffectiveCapabilities(
        ClaimsPrincipal user,
        bool slicingOperational,
        bool calibrationOperational) =>
        new()
        {
            CanCreate =
                calibrationOperational &&
                PrintFarmerPermissions.HasPermission(user, PrintFarmerPermissions.Calibration.Create),
            CanRead =
                calibrationOperational &&
                PrintFarmerPermissions.HasPermission(user, PrintFarmerPermissions.Calibration.Read),
            CanUpdate =
                calibrationOperational &&
                PrintFarmerPermissions.HasPermission(user, PrintFarmerPermissions.Calibration.Update),
            CanDelete =
                calibrationOperational &&
                PrintFarmerPermissions.HasPermission(user, PrintFarmerPermissions.Calibration.Delete),
            CanGenerate =
                calibrationOperational &&
                PrintFarmerPermissions.HasPermission(user, PrintFarmerPermissions.Calibration.Generate),
            CanPublish =
                calibrationOperational &&
                PrintFarmerPermissions.HasPermission(user, PrintFarmerPermissions.Calibration.Publish),
            CanSubmitSlicing =
                slicingOperational &&
                PrintFarmerPermissions.HasPermission(user, PrintFarmerPermissions.Slicing.Submit),
            CanReadArtifacts =
                slicingOperational &&
                PrintFarmerPermissions.HasPermission(user, PrintFarmerPermissions.Slicing.ReadArtifact),
            CanManageDispatchSettings =
                calibrationOperational &&
                PrintFarmerPermissions.HasPermission(user, PrintFarmerPermissions.DispatchSettings.Manage),
        };

    private string GetDeploymentMode() =>
        _configuration.GetValue<string>("Deployment:Mode")?.ToLowerInvariant() switch
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
        int AvailableSlots)
    {
        public static WorkerHealthSnapshot Disabled { get; } = new(false, 0, 0);

        public static WorkerHealthSnapshot Unavailable { get; } = new(false, 0, 0);
    }
}
