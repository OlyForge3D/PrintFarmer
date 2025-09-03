using System.Text.Json;
using System.Text.Json.Serialization;

namespace Farm.Web.Api.Services;

/// <summary>
/// Comprehensive PrusaLink API client based on the official OpenAPI specification
/// https://github.com/prusa3d/Prusa-Link-Web/blob/master/spec/openapi.yaml
/// </summary>
public partial class PrusaLinkApiClient
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "PrusaLink API call failed for {Url}")]
    private static partial void LogApiError(ILogger logger, Exception exception, string url);

    [LoggerMessage(Level = LogLevel.Debug, Message = "PrusaLink API deserialization failed for {Url}")]
    private static partial void LogDeserializationError(ILogger logger, Exception exception, string url);

    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILogger<PrusaLinkApiClient> _logger;

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

    private static Uri EnsureBaseUri(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new ArgumentException("Base URL is required", nameof(baseUrl));
        }

        // Ensure scheme but do not force a port; preserve caller-provided formatting
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var abs))
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
    public async Task<VersionInfo> GetVersionAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        var url = new Uri(EnsureBaseUri(baseUrl), "api/version").ToString();
        try
        {
            using var request = CreateRequest(HttpMethod.Get, url, apiKey);
            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<VersionInfo>(json, _jsonOptions)!;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected when cancellation is requested
            throw;
        }
        catch (HttpRequestException ex)
        {
            LogApiError(_logger, ex, url);
            throw;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            LogApiError(_logger, ex, url);
            throw;
        }
        catch (JsonException ex)
        {
            LogDeserializationError(_logger, ex, url);
            throw;
        }
    }

    // API Version Information (Uri overload co-located for S4136)
    public async Task<VersionInfo> GetVersionAsync(Uri baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);

        var url = new Uri(baseUrl, "api/version");
        try
        {
            using var request = CreateRequest(HttpMethod.Get, url.ToString(), apiKey);
            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<VersionInfo>(json, _jsonOptions)!;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected when cancellation is requested
            throw;
        }
        catch (HttpRequestException ex)
        {
            LogApiError(_logger, ex, url.ToString());
            throw;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            LogApiError(_logger, ex, url.ToString());
            throw;
        }
        catch (JsonException ex)
        {
            LogDeserializationError(_logger, ex, url.ToString());
            throw;
        }
    }

    // Printer Information
    public async Task<PrinterInfo> GetInfoAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        var url = new Uri(EnsureBaseUri(baseUrl), "api/v1/info").ToString();
        try
        {
            using var request = CreateRequest(HttpMethod.Get, url, apiKey);
            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<PrinterInfo>(json, _jsonOptions)!;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected when cancellation is requested
            throw;
        }
        catch (HttpRequestException ex)
        {
            LogApiError(_logger, ex, url);
            throw;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            LogApiError(_logger, ex, url);
            throw;
        }
        catch (JsonException ex)
        {
            LogDeserializationError(_logger, ex, url);
            throw;
        }
    }

    // Status Information
    public async Task<StatusInfo> GetStatusAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        var url = new Uri(EnsureBaseUri(baseUrl), "api/v1/status").ToString();
        try
        {
            using var request = CreateRequest(HttpMethod.Get, url, apiKey);
            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<StatusInfo>(json, _jsonOptions)!;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected when cancellation is requested
            throw;
        }
        catch (HttpRequestException ex)
        {
            LogApiError(_logger, ex, url);
            throw;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            LogApiError(_logger, ex, url);
            throw;
        }
        catch (JsonException ex)
        {
            LogDeserializationError(_logger, ex, url);
            throw;
        }
    }

    // Job Management
    public async Task<Job?> GetJobAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Get, new Uri(EnsureBaseUri(baseUrl), "api/v1/job").ToString(), apiKey);
        using var response = await _httpClient.SendAsync(request, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<Job>(json, _jsonOptions);
    }

    public async Task<bool> StopJobAsync(string baseUrl, int jobId, string? apiKey = null, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Delete, new Uri(EnsureBaseUri(baseUrl), $"api/v1/job/{jobId}").ToString(), apiKey);
        using var response = await _httpClient.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> PauseJobAsync(string baseUrl, int jobId, string? apiKey = null, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Put, new Uri(EnsureBaseUri(baseUrl), $"api/v1/job/{jobId}/pause").ToString(), apiKey);
        using var response = await _httpClient.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> ResumeJobAsync(string baseUrl, int jobId, string? apiKey = null, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Put, new Uri(EnsureBaseUri(baseUrl), $"api/v1/job/{jobId}/resume").ToString(), apiKey);
        using var response = await _httpClient.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> ContinueJobAsync(string baseUrl, int jobId, string? apiKey = null, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Put, new Uri(EnsureBaseUri(baseUrl), $"api/v1/job/{jobId}/continue").ToString(), apiKey);
        using var response = await _httpClient.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    // Storage Management
    public async Task<StorageListResponse> GetStorageAsync(string baseUrl, string? apiKey = null, string? acceptLanguage = null, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Get, new Uri(EnsureBaseUri(baseUrl), "api/v1/storage").ToString(), apiKey);
        if (!string.IsNullOrWhiteSpace(acceptLanguage))
        {
            request.Headers.Add("Accept-Language", acceptLanguage);
        }

        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<StorageListResponse>(json, _jsonOptions)!;
    }

    // Transfer Management
    public async Task<Transfer?> GetTransferAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Get, new Uri(EnsureBaseUri(baseUrl), "api/v1/transfer").ToString(), apiKey);
        using var response = await _httpClient.SendAsync(request, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<Transfer>(json, _jsonOptions);
    }

    public async Task<bool> StopTransferAsync(string baseUrl, int transferId, string? apiKey = null, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Delete, new Uri(EnsureBaseUri(baseUrl), $"api/v1/transfer/{transferId}").ToString(), apiKey);
        using var response = await _httpClient.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    // File Management
    public async Task<FileInfoBase> GetFileInfoAsync(string baseUrl, string storagePath, string filePath, string? apiKey = null,
        string? acceptLanguage = null, string? accept = null, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Get, new Uri(EnsureBaseUri(baseUrl), $"api/v1/files{storagePath}{filePath}").ToString(), apiKey);
        if (!string.IsNullOrWhiteSpace(acceptLanguage))
        {
            request.Headers.Add("Accept-Language", acceptLanguage);
        }

        if (!string.IsNullOrWhiteSpace(accept))
        {
            request.Headers.Add("Accept", accept);
        }

        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);

        // Deserialize to appropriate type based on response content
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("type", out var typeElement))
        {
            var fileType = typeElement.GetString();
            return fileType switch
            {
                "PRINT_FILE" => JsonSerializer.Deserialize<PrintFileInfo>(json, _jsonOptions)!,
                "FIRMWARE" => JsonSerializer.Deserialize<FirmwareFileInfo>(json, _jsonOptions)!,
                "FOLDER" => JsonSerializer.Deserialize<FolderInfo>(json, _jsonOptions)!,
                _ => JsonSerializer.Deserialize<FileInfo>(json, _jsonOptions)!
            };
        }
        return JsonSerializer.Deserialize<FileInfo>(json, _jsonOptions)!;
    }

    public async Task<bool> UploadFileAsync(string baseUrl, string storagePath, string filePath, Stream fileStream,
        string? apiKey = null, bool printAfterUpload = false, bool overwrite = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileStream);

        using var request = CreateRequest(HttpMethod.Put, new Uri(EnsureBaseUri(baseUrl), $"api/v1/files{storagePath}{filePath}").ToString(), apiKey);

        request.Content = new StreamContent(fileStream);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentLength = fileStream.Length;

        request.Headers.Add("Print-After-Upload", printAfterUpload ? "?1" : "?0");
        request.Headers.Add("Overwrite", overwrite ? "?1" : "?0");

        using var response = await _httpClient.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> StartPrintAsync(string baseUrl, string storagePath, string filePath, string? apiKey = null, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Post, new Uri(EnsureBaseUri(baseUrl), $"api/v1/files{storagePath}{filePath}").ToString(), apiKey);
        request.Content = new StringContent("");

        using var response = await _httpClient.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<FileStatus> CheckFileStatusAsync(string baseUrl, string storagePath, string filePath, string? apiKey = null, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Head, new Uri(EnsureBaseUri(baseUrl), $"api/v1/files{storagePath}{filePath}").ToString(), apiKey);
        using var response = await _httpClient.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            return new FileStatus(false, false, false);
        }

        var readOnly = response.Headers.Contains("Read-Only") &&
            response.Headers.GetValues("Read-Only").FirstOrDefault() == "true";
        var currentlyPrinted = response.Headers.Contains("Currently-Printed") &&
            response.Headers.GetValues("Currently-Printed").FirstOrDefault() == "true";

        return new FileStatus(true, readOnly, currentlyPrinted);
    }

    public async Task<bool> DeleteFileAsync(string baseUrl, string storagePath, string filePath, string? apiKey = null,
        bool force = false, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Delete, new Uri(EnsureBaseUri(baseUrl), $"api/v1/files{storagePath}{filePath}").ToString(), apiKey);
        request.Headers.Add("Force", force ? "?1" : "?0");

        using var response = await _httpClient.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    // Camera Management
    public async Task<Camera[]> GetCamerasAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Get, new Uri(EnsureBaseUri(baseUrl), "api/v1/cameras").ToString(), apiKey);
        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<Camera[]>(json, _jsonOptions)!;
    }

    public async Task<bool> SetCameraOrderAsync(string baseUrl, string[] cameraIds, string? apiKey = null, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Put, new Uri(EnsureBaseUri(baseUrl), "api/v1/cameras").ToString(), apiKey);
        var jsonContent = JsonSerializer.Serialize(cameraIds, _jsonOptions);
        request.Content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<CameraConfig> GetCameraConfigAsync(string baseUrl, string cameraId, string? apiKey = null, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Get, new Uri(EnsureBaseUri(baseUrl), $"api/v1/cameras/{cameraId}").ToString(), apiKey);
        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<CameraConfig>(json, _jsonOptions)!;
    }

    public async Task<bool> SetupCameraAsync(string baseUrl, string cameraId, CameraConfigSet config, string? apiKey = null, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Post, new Uri(EnsureBaseUri(baseUrl), $"api/v1/cameras/{cameraId}").ToString(), apiKey);
        var jsonContent = JsonSerializer.Serialize(config, _jsonOptions);
        request.Content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> DeleteCameraAsync(string baseUrl, string cameraId, string? apiKey = null, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Delete, new Uri(EnsureBaseUri(baseUrl), $"api/v1/cameras/{cameraId}").ToString(), apiKey);
        using var response = await _httpClient.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<byte[]?> TakeSnapshotAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Get, new Uri(EnsureBaseUri(baseUrl), "api/v1/cameras/snap").ToString(), apiKey);
        using var response = await _httpClient.SendAsync(request, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<byte[]?> TakeSnapshotAsync(string baseUrl, string cameraId, string? apiKey = null, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Get, new Uri(EnsureBaseUri(baseUrl), $"api/v1/cameras/{cameraId}/snap").ToString(), apiKey);
        using var response = await _httpClient.SendAsync(request, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<byte[]?> TriggerSnapshotAsync(string baseUrl, string cameraId, string? apiKey = null, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Post, new Uri(EnsureBaseUri(baseUrl), $"api/v1/cameras/{cameraId}/snap").ToString(), apiKey);
        request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<bool> UpdateCameraConfigAsync(string baseUrl, string cameraId, CameraConfigSet config, string? apiKey = null, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Patch, new Uri(EnsureBaseUri(baseUrl), $"api/v1/cameras/{cameraId}/config").ToString(), apiKey);
        var jsonContent = JsonSerializer.Serialize(config, _jsonOptions);
        request.Content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> ResetCameraConfigAsync(string baseUrl, string cameraId, string? apiKey = null, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Delete, new Uri(EnsureBaseUri(baseUrl), $"api/v1/cameras/{cameraId}/config").ToString(), apiKey);
        using var response = await _httpClient.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> RegisterCameraToConnectAsync(string baseUrl, string cameraId, string? apiKey = null, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Post, new Uri(EnsureBaseUri(baseUrl), $"api/v1/cameras/{cameraId}/connection").ToString(), apiKey);
        using var response = await _httpClient.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> UnregisterCameraFromConnectAsync(string baseUrl, string cameraId, string? apiKey = null, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Delete, new Uri(EnsureBaseUri(baseUrl), $"api/v1/cameras/{cameraId}/connection").ToString(), apiKey);
        using var response = await _httpClient.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    // Update Management
    public async Task<UpdateInfo?> GetUpdateInfoAsync(string baseUrl, string environment = "prusalink", string? apiKey = null, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Get, new Uri(EnsureBaseUri(baseUrl), $"api/v1/update/{environment}").ToString(), apiKey);
        using var response = await _httpClient.SendAsync(request, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
        {
            var updateAvailable = response.Headers.Contains("Update-Available") &&
                response.Headers.GetValues("Update-Available").FirstOrDefault() == "true";
            return new UpdateInfo { UpdateAvailable = updateAvailable };
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        var updateInfo = JsonSerializer.Deserialize<UpdateInfo>(json, _jsonOptions)!;

        if (response.Headers.Contains("Update-Available"))
        {
            updateInfo.UpdateAvailable = response.Headers.GetValues("Update-Available").FirstOrDefault() == "true";
        }

        return updateInfo;
    }

    public async Task<bool> StartUpdateAsync(string baseUrl, string environment = "prusalink", string? apiKey = null, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Post, new Uri(EnsureBaseUri(baseUrl), $"api/v1/update/{environment}").ToString(), apiKey);
        using var response = await _httpClient.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string url, string? apiKey)
    {
        var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Add("X-Api-Key", apiKey);
        }

        return request;
    }


}
