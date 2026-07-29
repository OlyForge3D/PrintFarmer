using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Backend.Plugin.Core;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers.PrusaLink;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Microsoft.Extensions.Logging;

namespace Farm.Backend.Plugin.PrusaLink;

/// <summary>
/// Comprehensive PrusaLink API client based on the official OpenAPI specification
/// https://github.com/prusa3d/Prusa-Link-Web/blob/master/spec/openapi.yaml
///
/// Supports two authentication modes:
/// - API Key (X-Api-Key header): Read access to most endpoints
/// - HTTP Digest Authentication: Full access including privileged operations
/// </summary>
public class PrusaLinkApiClient : IPrusaLinkApiClient
{
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILogger<PrusaLinkApiClient> _logger;

    // Cache for digest auth clients - keyed by username:password hash
    private readonly Dictionary<string, HttpClient> _digestAuthClients = new();
    private readonly object _clientLock = new();

    public PrusaLinkApiClient(HttpClient httpClient, ILogger<PrusaLinkApiClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    /// <summary>
    /// Resolves PrusaLink digest auth credentials from available auth data.
    /// PrusaLink uses "maker" as username with the API key as password.
    /// Supports legacy username/password credentials for backward compatibility.
    /// </summary>
    private static (string Username, string Password)? ResolvePrusaLinkDigestAuth(PrinterCredential? credentials)
    {
        if (credentials == null)
        {
            return null;
        }

        // Legacy: explicit username/password
        if (credentials.HasDigestAuth)
        {
            return (credentials.Username!, credentials.Password!);
        }

        // New: API key with hardcoded "maker" username
        if (credentials.HasApiKey)
        {
            return ("maker", credentials.ApiKey!);
        }

        return null;
    }

    // Intentionally caches HttpClient instances per credential set for connection reuse
#pragma warning disable IDISP015 // Member should not return created and cached instance
    private HttpClient GetClientForCredentials(PrinterCredential? credentials)
#pragma warning restore IDISP015
    {
        var auth = ResolvePrusaLinkDigestAuth(credentials);
        if (auth == null)
        {
            return _httpClient;
        }

        // Create a cache key for these credentials
        string cacheKey = $"{auth.Value.Username}:{auth.Value.Password.GetHashCode()}";

        lock (_clientLock)
        {
            if (_digestAuthClients.TryGetValue(cacheKey, out HttpClient? cachedClient))
            {
                return cachedClient;
            }

            // Create a new HttpClient with DigestAuthHandler
            DigestAuthHandler handler = new(auth.Value.Username, auth.Value.Password);
            HttpClient newClient = new(handler, disposeHandler: true)
            {
                Timeout = _httpClient.Timeout
            };

            _digestAuthClients[cacheKey] = newClient;
            return newClient;
        }
    }

    private static Uri EnsureBaseUri(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new ArgumentException("Base URL is required", nameof(baseUrl));
        }

        // Ensure scheme but do not force a port; preserve caller-provided formatting
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? abs))
        {
            return abs;
        }

        // Prepend http:// if missing a scheme
        if (Uri.TryCreate("http://" + baseUrl.Trim(), UriKind.Absolute, out abs))
        {
            return abs;
        }

        // Fallback: treat as http
        return new UriBuilder("http", baseUrl.Trim()).Uri;
    }

    // API Version Information
    public async Task<VersionInfo> GetVersionAsync(string baseUrl, PrinterCredential? credentials = null, CancellationToken ct = default)
    {
        string url = new Uri(EnsureBaseUri(baseUrl), "api/version").ToString();
        HttpClient client = GetClientForCredentials(credentials);
        try
        {
            using HttpRequestMessage request = CreateRequest(HttpMethod.Get, url, credentials);
            using HttpResponseMessage response = await client.SendAsync(request, ct);
            _ = response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<VersionInfo>(json, _jsonOptions)!;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug("PrusaLink API call failed for {Url}: {Message}", url, ex.Message);
            throw;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogDebug("PrusaLink API call failed for {Url}: {Message}", url, ex.Message);
            throw;
        }
        catch (JsonException ex)
        {
            _logger.LogDebug("PrusaLink API deserialization failed for {Url}: {Message}", url, ex.Message);
            throw;
        }
    }

    // API Version Information (Uri overload co-located for S4136)
    public async Task<VersionInfo> GetVersionAsync(Uri baseUrl, PrinterCredential? credentials = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);

        Uri url = new(baseUrl, "api/version");
        HttpClient client = GetClientForCredentials(credentials);
        try
        {
            using HttpRequestMessage request = CreateRequest(HttpMethod.Get, url.ToString(), credentials);
            using HttpResponseMessage response = await client.SendAsync(request, ct);
            _ = response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<VersionInfo>(json, _jsonOptions)!;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug("PrusaLink API call failed for {Url}: {Message}", url, ex.Message);
            throw;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogDebug("PrusaLink API call failed for {Url}: {Message}", url, ex.Message);
            throw;
        }
        catch (JsonException ex)
        {
            _logger.LogDebug("PrusaLink API deserialization failed for {Url}: {Message}", url, ex.Message);
            throw;
        }
    }

    // Printer Information
    public async Task<PrinterInfo> GetInfoAsync(string baseUrl, PrinterCredential? credentials = null, CancellationToken ct = default)
    {
        string url = new Uri(EnsureBaseUri(baseUrl), "api/v1/info").ToString();
        HttpClient client = GetClientForCredentials(credentials);
        try
        {
            using HttpRequestMessage request = CreateRequest(HttpMethod.Get, url, credentials);
            using HttpResponseMessage response = await client.SendAsync(request, ct);
            _ = response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<PrinterInfo>(json, _jsonOptions)!;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug("PrusaLink API call failed for {Url}: {Message}", url, ex.Message);
            throw;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogDebug("PrusaLink API call failed for {Url}: {Message}", url, ex.Message);
            throw;
        }
        catch (JsonException ex)
        {
            _logger.LogDebug("PrusaLink API deserialization failed for {Url}: {Message}", url, ex.Message);
            throw;
        }
    }

    // Status Information
    public async Task<StatusInfo> GetStatusAsync(string baseUrl, PrinterCredential? credentials = null, CancellationToken ct = default)
    {
        string url = new Uri(EnsureBaseUri(baseUrl), "api/v1/status").ToString();
        HttpClient client = GetClientForCredentials(credentials);
        try
        {
            using HttpRequestMessage request = CreateRequest(HttpMethod.Get, url, credentials);
            using HttpResponseMessage response = await client.SendAsync(request, ct);
            _ = response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<StatusInfo>(json, _jsonOptions)!;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug("PrusaLink API call failed for {Url}: {Message}", url, ex.Message);
            throw;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogDebug("PrusaLink API call failed for {Url}: {Message}", url, ex.Message);
            throw;
        }
        catch (JsonException ex)
        {
            _logger.LogDebug("PrusaLink API deserialization failed for {Url}: {Message}", url, ex.Message);
            throw;
        }
    }

    // Job Management
    public async Task<Job?> GetJobAsync(string baseUrl, PrinterCredential? credentials = null, CancellationToken ct = default)
    {
        HttpClient client = GetClientForCredentials(credentials);
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, new Uri(EnsureBaseUri(baseUrl), "api/v1/job").ToString(), credentials);
        using HttpResponseMessage response = await client.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        _ = response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<Job>(json, _jsonOptions);
    }

    public async Task<bool> StopJobAsync(string baseUrl, int jobId, PrinterCredential? credentials = null, CancellationToken ct = default)
    {
        HttpClient client = GetClientForCredentials(credentials);
        using HttpRequestMessage request = CreateRequest(HttpMethod.Delete, new Uri(EnsureBaseUri(baseUrl), $"api/v1/job/{jobId}").ToString(), credentials);
        using HttpResponseMessage response = await client.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> PauseJobAsync(string baseUrl, int jobId, PrinterCredential? credentials = null, CancellationToken ct = default)
    {
        HttpClient client = GetClientForCredentials(credentials);
        using HttpRequestMessage request = CreateRequest(HttpMethod.Put, new Uri(EnsureBaseUri(baseUrl), $"api/v1/job/{jobId}/pause").ToString(), credentials);
        using HttpResponseMessage response = await client.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> ResumeJobAsync(string baseUrl, int jobId, PrinterCredential? credentials = null, CancellationToken ct = default)
    {
        HttpClient client = GetClientForCredentials(credentials);
        using HttpRequestMessage request = CreateRequest(HttpMethod.Put, new Uri(EnsureBaseUri(baseUrl), $"api/v1/job/{jobId}/resume").ToString(), credentials);
        using HttpResponseMessage response = await client.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> ContinueJobAsync(string baseUrl, int jobId, PrinterCredential? credentials = null, CancellationToken ct = default)
    {
        HttpClient client = GetClientForCredentials(credentials);
        using HttpRequestMessage request = CreateRequest(HttpMethod.Put, new Uri(EnsureBaseUri(baseUrl), $"api/v1/job/{jobId}/continue").ToString(), credentials);
        using HttpResponseMessage response = await client.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    // Storage Management
    public async Task<StorageListResponse> GetStorageAsync(string baseUrl, PrinterCredential? credentials = null, string? acceptLanguage = null, CancellationToken ct = default)
    {
        HttpClient client = GetClientForCredentials(credentials);
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, new Uri(EnsureBaseUri(baseUrl), "api/v1/storage").ToString(), credentials);
        if (!string.IsNullOrWhiteSpace(acceptLanguage))
        {
            request.Headers.Add("Accept-Language", acceptLanguage);
        }

        using HttpResponseMessage response = await client.SendAsync(request, ct);
        _ = response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<StorageListResponse>(json, _jsonOptions)!;
    }

    // Transfer Management
    public async Task<Transfer?> GetTransferAsync(string baseUrl, PrinterCredential? credentials = null, CancellationToken ct = default)
    {
        HttpClient client = GetClientForCredentials(credentials);
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, new Uri(EnsureBaseUri(baseUrl), "api/v1/transfer").ToString(), credentials);
        using HttpResponseMessage response = await client.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        _ = response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<Transfer>(json, _jsonOptions);
    }

    public async Task<bool> StopTransferAsync(string baseUrl, int transferId, PrinterCredential? credentials = null, CancellationToken ct = default)
    {
        HttpClient client = GetClientForCredentials(credentials);
        using HttpRequestMessage request = CreateRequest(HttpMethod.Delete, new Uri(EnsureBaseUri(baseUrl), $"api/v1/transfer/{transferId}").ToString(), credentials);
        using HttpResponseMessage response = await client.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    // File Management
    public async Task<FileInfoBase> GetFileInfoAsync(string baseUrl, string storagePath, string filePath, PrinterCredential? credentials = null,
        string? acceptLanguage = null, string? accept = null, CancellationToken ct = default)
    {
        HttpClient client = GetClientForCredentials(credentials);
        string url = new Uri(EnsureBaseUri(baseUrl), $"api/v1/files{storagePath}{filePath}").ToString();
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, url, credentials);
        if (!string.IsNullOrWhiteSpace(acceptLanguage))
        {
            request.Headers.Add("Accept-Language", acceptLanguage);
        }

        if (!string.IsNullOrWhiteSpace(accept))
        {
            request.Headers.Add("Accept", accept);
        }

        using HttpResponseMessage response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger?.LogError("PrusaLink API returned {StatusCode} for {Url}", response.StatusCode, url);
            throw new HttpRequestException($"PrusaLink API error: {response.StatusCode}", null, response.StatusCode);
        }

        string json = await response.Content.ReadAsStringAsync(ct);

        // Deserialize to appropriate type based on response content
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("type", out JsonElement typeElement))
        {
            string? fileType = typeElement.GetString();
            return fileType switch
            {
                "PRINT_FILE" => JsonSerializer.Deserialize<PrintFileInfo>(json, _jsonOptions)!,
                "FIRMWARE" => JsonSerializer.Deserialize<FirmwareFileInfo>(json, _jsonOptions)!,
                "FOLDER" => JsonSerializer.Deserialize<FolderInfo>(json, _jsonOptions)!,
                _ => JsonSerializer.Deserialize<PrusaLinkFileInfo>(json, _jsonOptions)!
            };
        }

        return JsonSerializer.Deserialize<PrusaLinkFileInfo>(json, _jsonOptions)!;
    }

    public async Task<bool> UploadFileAsync(string baseUrl, string storagePath, string filePath, Stream fileStream,
        PrinterCredential? credentials = null, bool printAfterUpload = false, bool overwrite = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileStream);

        HttpClient client = GetClientForCredentials(credentials);
        string uploadUrl = new Uri(EnsureBaseUri(baseUrl), $"api/v1/files{storagePath}{filePath}").ToString();
        using HttpRequestMessage request = CreateRequest(HttpMethod.Put, uploadUrl, credentials);

        request.Content = new StreamContent(fileStream);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentLength = fileStream.Length;

        request.Headers.Add("Print-After-Upload", printAfterUpload ? "?1" : "?0");
        request.Headers.Add("Overwrite", overwrite ? "?1" : "?0");

        _logger.LogInformation("Uploading {FileStreamLength} bytes to PrusaLink: PUT {UploadUrl} (timeout={TotalSeconds}s)", fileStream.Length, uploadUrl, client.Timeout.TotalSeconds);

        using HttpResponseMessage response = await client.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("PrusaLink upload failed: HTTP {StatusCode} {ReasonPhrase} - {Body}", (int)response.StatusCode, response.ReasonPhrase, body);
            if (printAfterUpload &&
                (int)response.StatusCode is >= 400 and < 500)
            {
                throw new PrusaLinkCommandRejectedException(response.StatusCode);
            }
        }

        return response.IsSuccessStatusCode;
    }

    public async Task<bool> StartPrintAsync(string baseUrl, string storagePath, string filePath, PrinterCredential? credentials = null, CancellationToken ct = default)
    {
        HttpClient client = GetClientForCredentials(credentials);
        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, new Uri(EnsureBaseUri(baseUrl), $"api/v1/files{storagePath}{filePath}").ToString(), credentials);
        request.Content = new StringContent(string.Empty);

        using HttpResponseMessage response = await client.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<FileStatus> CheckFileStatusAsync(string baseUrl, string storagePath, string filePath, PrinterCredential? credentials = null, CancellationToken ct = default)
    {
        HttpClient client = GetClientForCredentials(credentials);
        using HttpRequestMessage request = CreateRequest(HttpMethod.Head, new Uri(EnsureBaseUri(baseUrl), $"api/v1/files{storagePath}{filePath}").ToString(), credentials);
        using HttpResponseMessage response = await client.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            return new FileStatus(false, false, false);
        }

        bool readOnly = response.Headers.Contains("Read-Only") &&
            response.Headers.GetValues("Read-Only").FirstOrDefault() == "true";
        bool currentlyPrinted = response.Headers.Contains("Currently-Printed") &&
            response.Headers.GetValues("Currently-Printed").FirstOrDefault() == "true";

        return new FileStatus(true, readOnly, currentlyPrinted);
    }

    public async Task<bool> DeleteFileAsync(string baseUrl, string storagePath, string filePath, PrinterCredential? credentials = null,
        bool force = false, CancellationToken ct = default)
    {
        HttpClient client = GetClientForCredentials(credentials);
        using HttpRequestMessage request = CreateRequest(HttpMethod.Delete, new Uri(EnsureBaseUri(baseUrl), $"api/v1/files{storagePath}{filePath}").ToString(), credentials);
        request.Headers.Add("Force", force ? "?1" : "?0");

        using HttpResponseMessage response = await client.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    // Camera Management
    public async Task<Farm.Infrastructure.Contracts.Printers.PrusaLink.Camera[]> GetCamerasAsync(string baseUrl, PrinterCredential? credentials = null, CancellationToken ct = default)
    {
        HttpClient client = GetClientForCredentials(credentials);
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, new Uri(EnsureBaseUri(baseUrl), "api/v1/cameras").ToString(), credentials);
        using HttpResponseMessage response = await client.SendAsync(request, ct);
        _ = response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<Farm.Infrastructure.Contracts.Printers.PrusaLink.Camera[]>(json, _jsonOptions)!;
    }

    public async Task<bool> SetCameraOrderAsync(string baseUrl, string[] cameraIds, PrinterCredential? credentials = null, CancellationToken ct = default)
    {
        HttpClient client = GetClientForCredentials(credentials);
        using HttpRequestMessage request = CreateRequest(HttpMethod.Put, new Uri(EnsureBaseUri(baseUrl), "api/v1/cameras").ToString(), credentials);
        string jsonContent = JsonSerializer.Serialize(cameraIds, _jsonOptions);
        request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<CameraConfig> GetCameraConfigAsync(string baseUrl, string cameraId, PrinterCredential? credentials = null, CancellationToken ct = default)
    {
        HttpClient client = GetClientForCredentials(credentials);
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, new Uri(EnsureBaseUri(baseUrl), $"api/v1/cameras/{cameraId}").ToString(), credentials);
        using HttpResponseMessage response = await client.SendAsync(request, ct);
        _ = response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<CameraConfig>(json, _jsonOptions)!;
    }

    public async Task<bool> SetupCameraAsync(string baseUrl, string cameraId, CameraConfigSet config, PrinterCredential? credentials = null, CancellationToken ct = default)
    {
        HttpClient client = GetClientForCredentials(credentials);
        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, new Uri(EnsureBaseUri(baseUrl), $"api/v1/cameras/{cameraId}").ToString(), credentials);
        string jsonContent = JsonSerializer.Serialize(config, _jsonOptions);
        request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> DeleteCameraAsync(string baseUrl, string cameraId, PrinterCredential? credentials = null, CancellationToken ct = default)
    {
        HttpClient client = GetClientForCredentials(credentials);
        using HttpRequestMessage request = CreateRequest(HttpMethod.Delete, new Uri(EnsureBaseUri(baseUrl), $"api/v1/cameras/{cameraId}").ToString(), credentials);
        using HttpResponseMessage response = await client.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<byte[]?> TakeSnapshotAsync(string baseUrl, PrinterCredential? credentials = null, CancellationToken ct = default)
    {
        HttpClient client = GetClientForCredentials(credentials);
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, new Uri(EnsureBaseUri(baseUrl), "api/v1/cameras/snap").ToString(), credentials);
        using HttpResponseMessage response = await client.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        _ = response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<byte[]?> TakeSnapshotAsync(string baseUrl, string cameraId, PrinterCredential? credentials = null, CancellationToken ct = default)
    {
        HttpClient client = GetClientForCredentials(credentials);
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, new Uri(EnsureBaseUri(baseUrl), $"api/v1/cameras/{cameraId}/snap").ToString(), credentials);
        using HttpResponseMessage response = await client.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        _ = response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<byte[]?> TriggerSnapshotAsync(string baseUrl, string cameraId, PrinterCredential? credentials = null, CancellationToken ct = default)
    {
        HttpClient client = GetClientForCredentials(credentials);
        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, new Uri(EnsureBaseUri(baseUrl), $"api/v1/cameras/{cameraId}/snap").ToString(), credentials);
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.SendAsync(request, ct);

        return !response.IsSuccessStatusCode ? null : await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<bool> UpdateCameraConfigAsync(string baseUrl, string cameraId, CameraConfigSet config, PrinterCredential? credentials = null, CancellationToken ct = default)
    {
        HttpClient client = GetClientForCredentials(credentials);
        using HttpRequestMessage request = CreateRequest(HttpMethod.Patch, new Uri(EnsureBaseUri(baseUrl), $"api/v1/cameras/{cameraId}/config").ToString(), credentials);
        string jsonContent = JsonSerializer.Serialize(config, _jsonOptions);
        request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> ResetCameraConfigAsync(string baseUrl, string cameraId, PrinterCredential? credentials = null, CancellationToken ct = default)
    {
        HttpClient client = GetClientForCredentials(credentials);
        using HttpRequestMessage request = CreateRequest(HttpMethod.Delete, new Uri(EnsureBaseUri(baseUrl), $"api/v1/cameras/{cameraId}/config").ToString(), credentials);
        using HttpResponseMessage response = await client.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> RegisterCameraToConnectAsync(string baseUrl, string cameraId, PrinterCredential? credentials = null, CancellationToken ct = default)
    {
        HttpClient client = GetClientForCredentials(credentials);
        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, new Uri(EnsureBaseUri(baseUrl), $"api/v1/cameras/{cameraId}/connection").ToString(), credentials);
        using HttpResponseMessage response = await client.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> UnregisterCameraFromConnectAsync(string baseUrl, string cameraId, PrinterCredential? credentials = null, CancellationToken ct = default)
    {
        HttpClient client = GetClientForCredentials(credentials);
        using HttpRequestMessage request = CreateRequest(HttpMethod.Delete, new Uri(EnsureBaseUri(baseUrl), $"api/v1/cameras/{cameraId}/connection").ToString(), credentials);
        using HttpResponseMessage response = await client.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    // Update Management
    public async Task<UpdateInfo?> GetUpdateInfoAsync(string baseUrl, string environment = "prusalink", PrinterCredential? credentials = null, CancellationToken ct = default)
    {
        HttpClient client = GetClientForCredentials(credentials);
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, new Uri(EnsureBaseUri(baseUrl), $"api/v1/update/{environment}").ToString(), credentials);
        using HttpResponseMessage response = await client.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            bool updateAvailable = response.Headers.Contains("Update-Available") &&
                response.Headers.GetValues("Update-Available").FirstOrDefault() == "true";
            return new UpdateInfo { UpdateAvailable = updateAvailable };
        }

        _ = response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        UpdateInfo updateInfo = JsonSerializer.Deserialize<UpdateInfo>(json, _jsonOptions)!;

        if (response.Headers.Contains("Update-Available"))
        {
            updateInfo.UpdateAvailable = response.Headers.GetValues("Update-Available").FirstOrDefault() == "true";
        }

        return updateInfo;
    }

    public async Task<bool> StartUpdateAsync(string baseUrl, string environment = "prusalink", PrinterCredential? credentials = null, CancellationToken ct = default)
    {
        HttpClient client = GetClientForCredentials(credentials);
        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, new Uri(EnsureBaseUri(baseUrl), $"api/v1/update/{environment}").ToString(), credentials);
        using HttpResponseMessage response = await client.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    /// <summary>
    /// Gets file list using the legacy /api/files endpoint (OctoPrint compatibility endpoint).
    /// This endpoint also requires authentication and returns files grouped by storage location.
    /// Used as a fallback when /api/v1/files fails due to authentication issues.
    /// Reference: FDM-Monster implementation at https://github.com/fdm-monster/fdm-monster
    /// </summary>
    /// <param name="baseUrl">The base URL of the PrusaLink printer.</param>
    /// <param name="credentials">Optional credentials for digest authentication.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task<List<FileChild>> GetFilesLegacyAsync(string baseUrl, PrinterCredential? credentials = null, CancellationToken ct = default)
    {
        HttpClient client = GetClientForCredentials(credentials);
        string url = new Uri(EnsureBaseUri(baseUrl), "api/files").ToString();
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, url, credentials);
        using HttpResponseMessage response = await client.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger?.LogError("PrusaLink legacy files API returned {StatusCode} for {Url}", response.StatusCode, url);
            throw new HttpRequestException($"PrusaLink legacy API error: {response.StatusCode}", null, response.StatusCode);
        }

        string json = await response.Content.ReadAsStringAsync(ct);
        using JsonDocument document = JsonDocument.Parse(json);

        List<FileChild> allFiles = [];

        // Response structure: { "files": [ { "path": "/usb", "children": [...], ... }, { "path": "/local", "children": [...], ... } ] }
        if (document.RootElement.TryGetProperty("files", out JsonElement filesArray))
        {
            foreach (JsonElement storageElement in filesArray.EnumerateArray())
            {
                // Look for the USB storage (print files are typically here)
                if (storageElement.TryGetProperty("path", out JsonElement pathElement))
                {
                    string? storagePath = pathElement.GetString();

                    // Collect files from /usb or /local storage
                    if ((storagePath == "/usb" || storagePath == "/local") && storageElement.TryGetProperty("children", out JsonElement childrenArray))
                    {
                        foreach (JsonElement childElement in childrenArray.EnumerateArray())
                        {
                            FileChild? fileChild = JsonSerializer.Deserialize<FileChild>(childElement.GetRawText(), _jsonOptions);
                            if (fileChild != null)
                            {
                                allFiles.Add(fileChild);
                            }
                        }
                    }
                }
            }
        }

        return allFiles;
    }

    public async Task<HistoryListResponse?> GetHistoryListAsync(
        string baseUrl,
        int? limit = null,
        int? start = null,
        DateTime? since = null,
        DateTime? before = null,
        string? order = null,
        PrinterCredential? credentials = null,
        CancellationToken ct = default)
    {
        HttpClient client = GetClientForCredentials(credentials);
        string normalizedBaseUrl = baseUrl.TrimEnd('/');
        try
        {
            int requestedStart = Math.Max(0, start ?? 0);
            int? requestedLimit = limit is > 0 ? limit : null;
            bool requiresFullScan =
                since.HasValue ||
                before.HasValue ||
                !string.IsNullOrWhiteSpace(order) ||
                !requestedLimit.HasValue;
            long requestedEnd = requestedLimit.HasValue
                ? (long)requestedStart + requestedLimit.Value
                : long.MaxValue;

            const int PageSize = 100;
            int offset = 0;
            int sourceCount = 0;
            int examinedCount = 0;
            bool reachedSourceEnd = false;
            var allJobs = new List<HistoryJob>();
            var excludedEntries = new List<HistoryExcludedEntryEvidence>();
            while (true)
            {
                int pageSize = !requiresFullScan && requestedLimit.HasValue
                    ? (int)Math.Min(
                        PageSize,
                        Math.Max(1L, requestedEnd - allJobs.Count))
                    : PageSize;
                HistoryListResponse page = await FetchPrusaLinkHistoryPageAsync(
                    client,
                    normalizedBaseUrl,
                    pageSize,
                    offset,
                    credentials,
                    ct);
                int examined = page.ExaminedSourceEntries;
                if (examined > 0 && page.Count < offset + examined)
                {
                    throw new InvalidDataException(
                        "PrusaLink history count did not cover the returned source page.");
                }

                sourceCount = Math.Max(sourceCount, page.Count);
                allJobs.AddRange(page.Jobs);
                excludedEntries.AddRange(page.ExcludedEntries);
                examinedCount += examined;
                offset += examined;
                reachedSourceEnd = offset >= sourceCount;
                bool requestedRangeFilled =
                    !requiresFullScan &&
                    requestedLimit.HasValue &&
                    allJobs.Count >= requestedEnd;
                if (examined == 0 || reachedSourceEnd || requestedRangeFilled)
                {
                    break;
                }
            }

            IEnumerable<HistoryJob> filtered = allJobs;
            if (since.HasValue)
            {
                double sinceSeconds = new DateTimeOffset(NormalizeUtc(since.Value))
                    .ToUnixTimeMilliseconds() / 1000d;
                filtered = filtered.Where(job => job.StartTime > sinceSeconds);
            }

            if (before.HasValue)
            {
                double beforeSeconds = new DateTimeOffset(NormalizeUtc(before.Value))
                    .ToUnixTimeMilliseconds() / 1000d;
                filtered = filtered.Where(job => job.StartTime < beforeSeconds);
            }

            filtered = string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase)
                ? filtered.OrderBy(job => job.StartTime)
                : string.Equals(order, "desc", StringComparison.OrdinalIgnoreCase)
                    ? filtered.OrderByDescending(job => job.StartTime)
                    : filtered;
            HistoryJob[] filteredJobs = filtered.ToArray();
            HistoryJob[] requestedJobs = filteredJobs
                .Skip(requestedStart)
                .Take(requestedLimit ?? int.MaxValue)
                .ToArray();
            bool coversRequestedRange = requiresFullScan
                ? reachedSourceEnd
                : requestedLimit.HasValue &&
                  (requestedJobs.Length >= requestedLimit.Value || reachedSourceEnd);
            return new HistoryListResponse
            {
                Count = requiresFullScan ? filteredJobs.Length : sourceCount,
                Jobs = requestedJobs,
                ExaminedSourceEntries = examinedCount,
                AuthorityEvidence = new HistoryListAuthorityEvidence(
                    "prusalink",
                    Math.Max(sourceCount, examinedCount),
                    examinedCount,
                    StartsAtBeginning: true,
                    HasUnambiguousEnd: reachedSourceEnd,
                    CoversRequestedRange: coversRequestedRange,
                    ExcludedEntryCount: excludedEntries.Count),
                ExcludedEntries = [.. excludedEntries],
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "PrusaLink history request threw for {BaseUrl} (limit={Limit}, start={Start}, since={Since})",
                normalizedBaseUrl,
                limit,
                start,
                since);
            return null;
        }
    }

    private static DateTime NormalizeUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };

    private static async Task<HistoryListResponse> FetchPrusaLinkHistoryPageAsync(
        HttpClient client,
        string normalizedBaseUrl,
        int? limit,
        int start,
        PrinterCredential? credentials,
        CancellationToken ct)
    {
        var queryParams = new List<string>();
        if (limit.HasValue)
        {
            queryParams.Add($"limit={limit.Value}");
        }

        queryParams.Add($"start={start}");
        string requestUrl =
            $"{normalizedBaseUrl}/api/history?{string.Join("&", queryParams)}";
        using HttpRequestMessage request =
            CreateRequest(HttpMethod.Get, requestUrl, credentials);
        using HttpResponseMessage response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string content = await response.Content.ReadAsStringAsync(ct);
        return ParseOctoPrintHistoryList(content)
            ?? throw new InvalidDataException(
                "PrusaLink returned malformed history list data.");
    }

    public async Task<HistoryJob?> GetHistoryJobAsync(string baseUrl, string jobId, PrinterCredential? credentials = null, CancellationToken ct = default)
    {
        HttpClient client = GetClientForCredentials(credentials);
        string normalizedBaseUrl = baseUrl.TrimEnd('/');
        HttpRequestMessage request = CreateRequest(HttpMethod.Get, $"{normalizedBaseUrl}/api/history/{Uri.EscapeDataString(jobId)}", credentials);

        try
        {
            using HttpResponseMessage response = await client.SendAsync(request, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                throw new HistoryJobNotFoundException(
                    $"PrusaLink history job {jobId} was not found.");
            }

            response.EnsureSuccessStatusCode();
            string content = await response.Content.ReadAsStringAsync(ct);
            HistoryJob? parsed = ParseOctoPrintHistoryJob(content);
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.JobId))
            {
                throw new InvalidDataException(
                    $"PrusaLink returned malformed history detail for {jobId}.");
            }

            return parsed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "PrusaLink history probe failed for job {JobId}",
                jobId);
            throw;
        }
    }

    public async Task<HistoryTotals?> GetHistoryTotalsAsync(string baseUrl, PrinterCredential? credentials = null, CancellationToken ct = default)
    {
        HistoryListResponse? history = await GetHistoryListAsync(
            baseUrl,
            limit: 10000,
            start: 0,
            since: null,
            before: null,
            order: null,
            credentials: credentials,
            ct: ct);
        if (history == null)
        {
            return null;
        }

        double totalPrintTime = 0;
        double totalFilamentUsed = 0;
        int completedCount = 0;

        foreach (HistoryJob job in history.Jobs)
        {
            if (job.Status.Equals("completed", StringComparison.OrdinalIgnoreCase))
            {
                totalPrintTime += job.PrintDuration;
                totalFilamentUsed += job.FilamentUsed;
                completedCount++;
            }
        }

        return new HistoryTotals
        {
            JobTotals = new JobTotals
            {
                TotalJobs = completedCount,
                TotalPrintTime = totalPrintTime,
                TotalFilamentUsed = totalFilamentUsed
            }
        };
    }

    public async Task<bool> DeleteHistoryJobAsync(string baseUrl, string jobId, PrinterCredential? credentials = null, CancellationToken ct = default)
    {
        HttpClient client = GetClientForCredentials(credentials);
        string normalizedBaseUrl = baseUrl.TrimEnd('/');
        HttpRequestMessage request = CreateRequest(HttpMethod.Delete, $"{normalizedBaseUrl}/api/history/{Uri.EscapeDataString(jobId)}", credentials);

        try
        {
            using HttpResponseMessage response = await client.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static HistoryListResponse? ParseOctoPrintHistoryList(string historyJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(historyJson);
            JsonElement root = doc.RootElement;

            if (!root.TryGetProperty("success", out JsonElement successProp) || !successProp.GetBoolean())
            {
                return null;
            }

            List<HistoryJob> jobs = [];
            List<HistoryExcludedEntryEvidence> excludedEntries = [];
            if (!root.TryGetProperty("results", out JsonElement resultsProp) ||
                resultsProp.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            int examinedEntries = resultsProp.GetArrayLength();
            foreach (JsonElement jobElement in resultsProp.EnumerateArray())
            {
                HistoryJob? job = ParseOctoPrintJobElement(
                    jobElement,
                    requireCompleteListEntry: true);
                if (job is null)
                {
                    try
                    {
                        excludedEntries.Add(
                            CreateExcludedHistoryEvidence(jobElement));
                    }
                    catch (Exception)
                    {
                        excludedEntries.Add(new HistoryExcludedEntryEvidence(
                            BackendJobId: null,
                            Filename: null,
                            StartTime: null,
                            Reason: "malformed_history_entry"));
                    }

                    continue;
                }

                jobs.Add(job);
            }

            if (!root.TryGetProperty("count", out JsonElement countProp) ||
                !countProp.TryGetInt32(out int count) ||
                count < 0 ||
                count < examinedEntries)
            {
                return null;
            }

            return new HistoryListResponse
            {
                Count = count,
                Jobs = jobs.ToArray(),
                ExaminedSourceEntries = examinedEntries,
                ExcludedEntries = excludedEntries.ToArray(),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static HistoryJob? ParseOctoPrintHistoryJob(string jobJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(jobJson);
            return ParseOctoPrintJobElement(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static HistoryJob? ParseOctoPrintJobElement(
        JsonElement jobElement,
        bool requireCompleteListEntry = false)
    {
        if (jobElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            var job = new HistoryJob();

            if (jobElement.TryGetProperty("id", out JsonElement idProp))
            {
                job.JobId = idProp.GetString() ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(job.JobId))
            {
                return null;
            }

            bool hasState =
                jobElement.TryGetProperty("state", out JsonElement stateProp) &&
                stateProp.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(stateProp.GetString());
            string? filename = null;
            if (jobElement.TryGetProperty("job", out JsonElement jobProp) &&
                jobProp.ValueKind == JsonValueKind.Object &&
                jobProp.TryGetProperty("file", out JsonElement fileProp) &&
                fileProp.ValueKind == JsonValueKind.Object &&
                fileProp.TryGetProperty("name", out JsonElement nameProp) &&
                nameProp.ValueKind == JsonValueKind.String)
            {
                filename = nameProp.GetString();
            }

            bool hasFilename = !string.IsNullOrWhiteSpace(filename);
            bool hasStartTime =
                jobElement.TryGetProperty("startTime", out JsonElement startProp) &&
                startProp.ValueKind == JsonValueKind.Number;
            if (requireCompleteListEntry &&
                (!hasState || !hasFilename || !hasStartTime))
            {
                return null;
            }

            if (hasState)
            {
                job.Status = stateProp.GetString() ?? string.Empty;
            }

            if (hasFilename)
            {
                job.Filename = filename!;
            }

            if (jobElement.TryGetProperty("printTime", out JsonElement printTimeProp) && printTimeProp.ValueKind == JsonValueKind.Number)
            {
                job.PrintDuration = printTimeProp.GetDouble();
            }

            if (hasStartTime)
            {
                job.StartTime = startProp.GetDouble();
            }

            if (jobElement.TryGetProperty("endTime", out JsonElement endProp) && endProp.ValueKind == JsonValueKind.Number)
            {
                job.EndTime = endProp.GetDouble();
            }

            if (jobElement.TryGetProperty("filament", out JsonElement filamentProp)
                && filamentProp.TryGetProperty("tool0", out JsonElement tool0Prop)
                && tool0Prop.TryGetProperty("length", out JsonElement lengthProp)
                && lengthProp.ValueKind == JsonValueKind.Number)
            {
                job.FilamentUsed = lengthProp.GetDouble();
            }

            return job;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static HistoryExcludedEntryEvidence CreateExcludedHistoryEvidence(
        JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            return new HistoryExcludedEntryEvidence(
                BackendJobId: null,
                Filename: null,
                StartTime: null,
                Reason: "malformed_history_entry");
        }

        string? backendJobId = TryReadHistoryString(entry, "id");
        string? filename = null;
        if (entry.TryGetProperty("job", out JsonElement job) &&
            job.ValueKind == JsonValueKind.Object &&
            job.TryGetProperty("file", out JsonElement file) &&
            file.ValueKind == JsonValueKind.Object)
        {
            filename = TryReadHistoryString(file, "name");
        }

        double? startTime =
            entry.TryGetProperty("startTime", out JsonElement start) &&
            start.ValueKind == JsonValueKind.Number &&
            start.TryGetDouble(out double value)
                ? value
                : null;
        return new HistoryExcludedEntryEvidence(
            backendJobId,
            filename,
            startTime,
            "malformed_history_entry");
    }

    private static string? TryReadHistoryString(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.ToString();
    }

    /// <summary>
    /// Creates an HTTP request message with appropriate authentication headers.
    /// Digest authentication is handled by DigestAuthHandler at the HttpClient level.
    /// </summary>
    private static HttpRequestMessage CreateRequest(HttpMethod method, string url, PrinterCredential? credentials)
    {
        HttpRequestMessage request = new(method, url);

        // Add X-Api-Key header if available (for compatibility with some PrusaLink endpoints)
        if (credentials?.HasApiKey == true)
        {
            request.Headers.Add("X-Api-Key", credentials.ApiKey);
        }

        return request;
    }

    // ========== LEGACY ENDPOINT IMPLEMENTATIONS (OctoPrint-compatible, require HTTP Digest Auth) ==========
    // These endpoints provide pause/resume, temperature control, and movement capabilities
    public async Task<bool> PausePrintLegacyAsync(string baseUrl, PrinterCredential? credentials, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (ResolvePrusaLinkDigestAuth(credentials) == null)
        {
            _logger?.LogWarning("[PrusaLink] PausePrintLegacy requires digest auth credentials (username/password or API key)");
            return false;
        }

        HttpClient client = GetClientForCredentials(credentials);
        string url = new Uri(EnsureBaseUri(baseUrl), "api/job").ToString();

        try
        {
            LegacyJobCommand command = new() { Command = "pause", Action = "pause" };
            string jsonContent = JsonSerializer.Serialize(command, _jsonOptions);

            using HttpRequestMessage request = CreateRequest(HttpMethod.Post, url, credentials);
            request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await client.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger?.LogError("[PrusaLink] PausePrintLegacy failed for {BaseUrl}: {Message}", baseUrl, ex.Message);
            return false;
        }
    }

    public async Task<bool> ResumePrintLegacyAsync(string baseUrl, PrinterCredential? credentials, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (ResolvePrusaLinkDigestAuth(credentials) == null)
        {
            _logger?.LogWarning("[PrusaLink] ResumePrintLegacy requires digest auth credentials (username/password or API key)");
            return false;
        }

        HttpClient client = GetClientForCredentials(credentials);
        string url = new Uri(EnsureBaseUri(baseUrl), "api/job").ToString();

        try
        {
            LegacyJobCommand command = new() { Command = "pause", Action = "resume" };
            string jsonContent = JsonSerializer.Serialize(command, _jsonOptions);

            using HttpRequestMessage request = CreateRequest(HttpMethod.Post, url, credentials);
            request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await client.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger?.LogError("[PrusaLink] ResumePrintLegacy failed for {BaseUrl}: {Message}", baseUrl, ex.Message);
            return false;
        }
    }

    public async Task<bool> SetToolTemperatureLegacyAsync(string baseUrl, double temperature, PrinterCredential? credentials, int toolIndex = 0, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (ResolvePrusaLinkDigestAuth(credentials) == null)
        {
            _logger?.LogWarning("[PrusaLink] SetToolTemperatureLegacy requires digest auth credentials (username/password or API key)");
            return false;
        }

        HttpClient client = GetClientForCredentials(credentials);
        string url = new Uri(EnsureBaseUri(baseUrl), "api/printer/tool").ToString();

        try
        {
            LegacyToolCommand command = new()
            {
                Command = "target",
                Targets = new Dictionary<string, double> { [$"tool{toolIndex}"] = temperature }
            };
            string jsonContent = JsonSerializer.Serialize(command, _jsonOptions);

            using HttpRequestMessage request = CreateRequest(HttpMethod.Post, url, credentials);
            request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await client.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                throw new Farm.Infrastructure.Services.Printers.PrinterBackendBusyException(
                    $"PrusaLink refused tool temperature (409 Conflict) at {baseUrl}.");
            }

            return response.IsSuccessStatusCode;
        }
        catch (Farm.Infrastructure.Services.Printers.PrinterBackendBusyException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError("[PrusaLink] SetToolTemperatureLegacy failed for {BaseUrl}: {Message}", baseUrl, ex.Message);
            return false;
        }
    }

    public async Task<bool> SetBedTemperatureLegacyAsync(string baseUrl, double temperature, PrinterCredential? credentials, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (ResolvePrusaLinkDigestAuth(credentials) == null)
        {
            _logger?.LogWarning("[PrusaLink] SetBedTemperatureLegacy requires digest auth credentials (username/password or API key)");
            return false;
        }

        HttpClient client = GetClientForCredentials(credentials);
        string url = new Uri(EnsureBaseUri(baseUrl), "api/printer/bed").ToString();

        try
        {
            LegacyBedCommand command = new() { Command = "target", Target = temperature };
            string jsonContent = JsonSerializer.Serialize(command, _jsonOptions);

            using HttpRequestMessage request = CreateRequest(HttpMethod.Post, url, credentials);
            request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await client.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                throw new Farm.Infrastructure.Services.Printers.PrinterBackendBusyException(
                    $"PrusaLink refused bed temperature (409 Conflict) at {baseUrl}.");
            }

            return response.IsSuccessStatusCode;
        }
        catch (Farm.Infrastructure.Services.Printers.PrinterBackendBusyException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError("[PrusaLink] SetBedTemperatureLegacy failed for {BaseUrl}: {Message}", baseUrl, ex.Message);
            return false;
        }
    }

    public async Task<bool> JogPrintHeadLegacyAsync(string baseUrl, double? x, double? y, double? z, double? feedRate, PrinterCredential? credentials, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (ResolvePrusaLinkDigestAuth(credentials) == null)
        {
            _logger?.LogWarning("[PrusaLink] JogPrintHeadLegacy requires digest auth credentials (username/password or API key)");
            return false;
        }

        HttpClient client = GetClientForCredentials(credentials);
        string url = new Uri(EnsureBaseUri(baseUrl), "api/printer/printhead").ToString();

        try
        {
            LegacyPrintheadCommand command = new()
            {
                Command = "jog",
                X = x,
                Y = y,
                Z = z,
                Speed = feedRate
            };
            string jsonContent = JsonSerializer.Serialize(command, _jsonOptions);

            using HttpRequestMessage request = CreateRequest(HttpMethod.Post, url, credentials);
            request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await client.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                throw new Farm.Infrastructure.Services.Printers.PrinterBackendBusyException(
                    $"PrusaLink refused jog (409 Conflict) at {baseUrl}.");
            }

            return response.IsSuccessStatusCode;
        }
        catch (Farm.Infrastructure.Services.Printers.PrinterBackendBusyException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError("[PrusaLink] JogPrintHeadLegacy failed for {BaseUrl}: {Message}", baseUrl, ex.Message);
            return false;
        }
    }

    public async Task<bool> HomePrintHeadLegacyAsync(string baseUrl, bool homeX, bool homeY, bool homeZ, PrinterCredential? credentials, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (ResolvePrusaLinkDigestAuth(credentials) == null)
        {
            _logger?.LogWarning("[PrusaLink] HomePrintHeadLegacy requires digest auth credentials (username/password or API key)");
            return false;
        }

        HttpClient client = GetClientForCredentials(credentials);
        string url = new Uri(EnsureBaseUri(baseUrl), "api/printer/printhead").ToString();

        try
        {
            // Build axes list based on what needs to be homed
            List<string> axes = new();
            if (homeX)
            {
                axes.Add("x");
            }

            if (homeY)
            {
                axes.Add("y");
            }

            if (homeZ)
            {
                axes.Add("z");
            }

            LegacyPrintheadCommand command = new()
            {
                Command = "home",
                Axes = axes.ToArray()
            };
            string jsonContent = JsonSerializer.Serialize(command, _jsonOptions);

            using HttpRequestMessage request = CreateRequest(HttpMethod.Post, url, credentials);
            request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await client.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger?.LogError("[PrusaLink] HomePrintHeadLegacy failed for {BaseUrl}: {Message}", baseUrl, ex.Message);
            return false;
        }
    }
}
