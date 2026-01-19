using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Contracts.Slicing;
using Farm.Infrastructure.Domain;

namespace Farm.OrcaSlicer.Worker.Services;

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
    /// <returns>True if heartbeat was acknowledged; otherwise, false.</returns>
    Task<bool> HeartbeatAsync(Guid serviceId, string apiKey, int freeSlots, string status = "Online", CancellationToken cancellationToken = default);

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
    private readonly ILogger<SlicerRegistrationClient> _logger;
    private readonly string _apiBaseUrl;
    private readonly string _serviceName;
    private readonly string _serviceVersion;
    private readonly string _serviceHost;

    public SlicerRegistrationClient(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<SlicerRegistrationClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Load configuration
        _apiBaseUrl = configuration["SlicerRegistry:ApiBaseUrl"] ?? configuration["Worker:StorageEndpoint"] ?? "http://api:5245";
        _serviceName = configuration["SlicerRegistry:ServiceName"] ?? Environment.GetEnvironmentVariable("HOSTNAME") ?? "orcaslicer-worker";
        _serviceVersion = configuration["SlicerRegistry:Version"] ?? "1.0.0";
        _serviceHost = configuration["SlicerRegistry:Host"] ?? "http://orcaslicer-worker:8080";

        // Ensure base URL doesn't have trailing slash
        _apiBaseUrl = _apiBaseUrl.TrimEnd('/');
    }

    public async Task<(Guid ServiceId, string ApiKey)> RegisterAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            RegisterSlicerDto registrationDto = new RegisterSlicerDto
            {
                Name = _serviceName,
                SlicerType = (int)SlicerType.OrcaSlicer,
                Version = _serviceVersion,
                Host = _serviceHost,
                UiManifestUrl = null, // Optional: can be added later for embedded UI
                CapabilitiesJson = JsonSerializer.Serialize(new
                {
                    supportedFormats = new[] { "stl", "obj", "3mf" },
                    supportedFeatures = new[] { "multi-material", "variable-layer-height", "auto-arrange" },
                    capabilities = WorkerConstants.Capabilities
                }),
                MaxConcurrentJobs = _configuration.GetValue("Worker:MaxConcurrentJobs", 1),
                Tags = "orcaslicer,production"
            };

            string json = JsonSerializer.Serialize(registrationDto);
            StringContent content = new StringContent(json, Encoding.UTF8, "application/json");

            // Add API key header if configured
            string? apiKey = _configuration["SlicerRegistry:ApiKey"];
            if (!string.IsNullOrEmpty(apiKey))
            {
                _httpClient.DefaultRequestHeaders.Add("X-Slicer-ApiKey", apiKey);
            }

            HttpResponseMessage response = await _httpClient.PostAsync($"{_apiBaseUrl}/api/slicers/register", content, cancellationToken);

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

    public async Task<bool> HeartbeatAsync(Guid serviceId, string apiKey, int freeSlots, string status = "Online", CancellationToken cancellationToken = default)
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
            request.Headers.Add("X-Slicer-ApiKey", apiKey);

            HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Heartbeat failed: {StatusCode}", response.StatusCode);
                return false;
            }

            _logger.LogDebug("Heartbeat sent successfully. FreeSlots: {FreeSlots}, Status: {Status}", freeSlots, status);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send heartbeat");
            return false;
        }
    }

    public async Task<bool> DeregisterAsync(Guid serviceId, string apiKey, CancellationToken cancellationToken = default)
    {
        try
        {
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, $"{_apiBaseUrl}/api/slicers/{serviceId}/deregister");
            request.Headers.Add("X-Slicer-ApiKey", apiKey);

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

    private class RegistrationResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public Guid Id { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("apiKey")]
        public string ApiKey { get; init; } = string.Empty;
    }
}
