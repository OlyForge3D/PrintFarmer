using System.Net;
using System.Text;
using System.Text.Json;
using Farm.Slicer.Module.Contracts;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services.Configuration;
using Farm.Slicer.Worker.Core;

namespace Farm.OrcaSlicer.Worker.Services;

public enum SlicerHeartbeatResult
{
    Succeeded,
    Retry,
    ReRegister,
}

/// <summary>
/// Client for registering this worker with the central slicer registry API
/// </summary>
public interface ISlicerRegistrationClient
{
    /// <summary>
    /// Register this worker with the API and receive service ID and API key
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for async operation.</param>
    /// <returns>Tuple containing the assigned service ID and API key.</returns>
    Task<(Guid ServiceId, string ApiKey)> RegisterAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Send heartbeat to update capacity and status
    /// </summary>
    /// <param name="serviceId">The registered service identifier.</param>
    /// <param name="apiKey">API key for authentication.</param>
    /// <param name="freeSlots">Number of available job slots.</param>
    /// <param name="status">Current service status.</param>
    /// <param name="cancellationToken">Cancellation token for async operation.</param>
    /// <returns>The action the registration loop should take after the heartbeat.</returns>
    Task<SlicerHeartbeatResult> HeartbeatAsync(Guid serviceId, string apiKey, int freeSlots, string status = "Online", CancellationToken cancellationToken = default);

    /// <summary>
    /// Deregister from the API (called on shutdown)
    /// </summary>
    /// <param name="serviceId">The registered service identifier.</param>
    /// <param name="apiKey">API key for authentication.</param>
    /// <param name="cancellationToken">Cancellation token for async operation.</param>
    /// <returns>True if deregistration succeeded; otherwise, false.</returns>
    Task<bool> DeregisterAsync(Guid serviceId, string apiKey, CancellationToken cancellationToken = default);
}

public class SlicerRegistrationClient : ISlicerRegistrationClient
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly IOrcaBinaryDetector _binaryDetector;
    private readonly ILogger<SlicerRegistrationClient> _logger;
    private readonly WorkerCapabilityProvider _capabilityProvider;
    private readonly string _apiBaseUrl;
    private readonly string _serviceName;
    private readonly string _serviceVersion;
    private readonly string _serviceHost;
    private readonly string _workerInstanceId;
    private readonly string _registrationApiKey;

    /// <summary>Path of the image attestation describing the installed OrcaSlicer binary.</summary>
    private readonly string? _binaryAttestationPath;

    /// <summary>SHA-256 the image build declared for the pinned OrcaSlicer AppImage, if any.</summary>
    private readonly string? _declaredBinarySha256;

    /// <summary>Digest of the container image this worker runs from, when supplied.</summary>
    private readonly string? _slicerContainerDigest;

    public SlicerRegistrationClient(
        HttpClient httpClient,
        IConfiguration configuration,
        IOrcaBinaryDetector binaryDetector,
        ILogger<SlicerRegistrationClient> logger,
        WorkerCapabilityProvider capabilityProvider)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _binaryDetector = binaryDetector ?? throw new ArgumentNullException(nameof(binaryDetector));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _capabilityProvider = capabilityProvider ?? throw new ArgumentNullException(nameof(capabilityProvider));

        // Load configuration — prefer unified SlicerApi:BaseUrl, then legacy keys
        _apiBaseUrl = configuration["SlicerApi:BaseUrl"]
                   ?? configuration["SlicerRegistry:ApiBaseUrl"]
                   ?? configuration["Worker:StorageEndpoint"]
                   ?? "http://api:5245";
        _serviceName = configuration["SlicerRegistry:ServiceName"] ?? Environment.GetEnvironmentVariable("HOSTNAME") ?? "orcaslicer-worker";
        _serviceVersion = configuration["SlicerRegistry:Version"] ?? WorkerConstants.SlicerVersion;
        _serviceHost = configuration["SlicerRegistry:Host"] ?? "http://orcaslicer-worker:8080";
        _workerInstanceId = Normalize(configuration["Worker:InstanceId"]) ?? WorkerIdentity.Create();
        _registrationApiKey = ResolveRegistrationApiKey(configuration)
            ?? throw new InvalidOperationException(
                "The OrcaSlicer worker requires a registration key. Configure " +
                $"{WorkerAuthConfiguration.SharedKeyPath} through configuration or a secret provider.");

        // Identity comes from what the image actually installed, attested by the build. The declared
        // build argument alone never establishes it, because the stub fallback does not honour it.
        _binaryAttestationPath = Normalize(configuration["Worker:OrcaSlicerAttestationPath"]);
        _declaredBinarySha256 = Normalize(configuration["Worker:OrcaSlicerSha256"]);
        _slicerContainerDigest = Normalize(configuration["Worker:ContainerDigest"]);

        // Ensure base URL doesn't have trailing slash
        _apiBaseUrl = _apiBaseUrl.TrimEnd('/');
    }

    public async Task<(Guid ServiceId, string ApiKey)> RegisterAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            SlicerBinaryIdentity identity = await SlicerBinaryAttestation.ResolveFromFileAsync(
                _binaryAttestationPath,
                _declaredBinarySha256,
                _binaryDetector.IsRealBinaryPresent(),
                cancellationToken);
            if (!identity.RealBinary && _declaredBinarySha256 is not null)
            {
                _logger.LogWarning(
                    "This image declares a pinned OrcaSlicer digest but carries no verified binary; registering as unverified.");
            }

            RegisterSlicerDto registrationDto = new RegisterSlicerDto
            {
                Name = _serviceName,
                SlicerType = (int)SlicerType.OrcaSlicer,
                Version = _serviceVersion,
                Host = _serviceHost,
                UiManifestUrl = null, // Optional: can be added later for embedded UI
                CapabilitiesJson = JsonSerializer.Serialize(new
                {
                    supportedFormats = new[] { "stl", "obj", "3mf", "step", "stp" },
                    supportedFeatures = new[] { "multi-material", "variable-layer-height", "auto-arrange" },
                    capabilities = _capabilityProvider.GetCapabilities(),
                    engineVersion = _capabilityProvider.EngineVersion,

                    // Pinned build identity, so the API can decide whether this worker is the
                    // reproducible upstream image it advertises rather than trusting a version string.
                    slicerDistribution = "upstream",
                    slicerVersion = _serviceVersion,
                    slicerBinarySha256 = identity.BinarySha256,
                    slicerContainerDigest = _slicerContainerDigest,
                    realBinary = identity.RealBinary,
                }),
                MaxConcurrentJobs = _configuration.GetValue("Worker:MaxConcurrentJobs", 1),
                Tags = "orcaslicer,production",
                InstanceId = _workerInstanceId
            };

            string json = JsonSerializer.Serialize(registrationDto);
            StringContent content = new StringContent(json, Encoding.UTF8, "application/json");

            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, $"{_apiBaseUrl}/api/slicers/register")
            {
                Content = content
            };

            request.Headers.Add("X-Slicer-Api-Key", _registrationApiKey);

            HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                string error = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Failed to register with API: {StatusCode} - {Error}", response.StatusCode, error);
                _logger.LogError("Registration DTO: {RegistrationData}", json);
                _logger.LogError(
                    "API Base URL: {ApiBaseUrl}, Service Name: {ServiceName}, Host: {ServiceHost}",
                    _apiBaseUrl,
                    _serviceName,
                    _serviceHost);

                throw new InvalidOperationException($"Registration failed: {response.StatusCode} - {error}");
            }

            string responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            RegistrationResponse? result = JsonSerializer.Deserialize<RegistrationResponse>(responseJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (result == null)
            {
                throw new InvalidOperationException("Failed to deserialize registration response");
            }

            _logger.LogInformation("Successfully registered with API. ServiceId: {ServiceId}", result.Id);
            return (result.Id, result.ApiKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register with slicer registry");
            throw;
        }
    }

    public async Task<SlicerHeartbeatResult> HeartbeatAsync(Guid serviceId, string apiKey, int freeSlots, string status = "Online", CancellationToken cancellationToken = default)
    {
        try
        {
            HeartbeatDto heartbeatDto = new HeartbeatDto
            {
                Status = status,
                FreeSlots = freeSlots
            };

            string json = JsonSerializer.Serialize(heartbeatDto);
            StringContent content = new StringContent(json, Encoding.UTF8, "application/json");

            // Add API key header
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, $"{_apiBaseUrl}/api/slicers/{serviceId}/heartbeat")
            {
                Content = content
            };
            request.Headers.Add("X-Slicer-Service-Api-Key", apiKey);

            HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode is HttpStatusCode.Unauthorized
                or HttpStatusCode.Forbidden
                or HttpStatusCode.NotFound)
            {
                _logger.LogWarning(
                    "Heartbeat registration was rejected with {StatusCode}; the worker will register a new identity",
                    response.StatusCode);
                return SlicerHeartbeatResult.ReRegister;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Heartbeat failed: {StatusCode}", response.StatusCode);
                return SlicerHeartbeatResult.Retry;
            }

            _logger.LogDebug("Heartbeat sent successfully. FreeSlots: {FreeSlots}, Status: {Status}", freeSlots, status);
            return SlicerHeartbeatResult.Succeeded;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send heartbeat");
            return SlicerHeartbeatResult.Retry;
        }
    }

    public async Task<bool> DeregisterAsync(Guid serviceId, string apiKey, CancellationToken cancellationToken = default)
    {
        try
        {
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, $"{_apiBaseUrl}/api/slicers/{serviceId}/deregister");
            request.Headers.Add("X-Slicer-Service-Api-Key", apiKey);

            HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Deregister failed: {StatusCode}", response.StatusCode);
                return false;
            }

            _logger.LogInformation("Successfully deregistered from API. ServiceId: {ServiceId}", serviceId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to deregister from slicer registry");
            return false;
        }
    }

    /// <summary>Trims a configured identity value and treats blank input as absent.</summary>
    /// <param name="value">The configured value.</param>
    /// <returns>The trimmed value, or <see langword="null"/> when nothing was configured.</returns>
    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private class RegistrationResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public Guid Id { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("apiKey")]
        public string ApiKey { get; init; } = string.Empty;
    }

    internal static string? ResolveRegistrationApiKey(IConfiguration configuration)
    {
        return WorkerAuthConfiguration.ResolveSharedKey(configuration)?.Value;
    }
}
