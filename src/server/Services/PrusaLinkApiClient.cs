using System.Text.Json;
using System.Text.Json.Serialization;

namespace Farm.Web.Server.Services;

/// <summary>
/// Comprehensive PrusaLink API client based on the official OpenAPI specification
/// https://github.com/prusa3d/Prusa-Link-Web/blob/master/spec/openapi.yaml
/// </summary>
public class PrusaLinkApiClient
{
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions;

    public PrusaLinkApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    // API Version Information
    public async Task<VersionInfo> GetVersionAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        var request = CreateRequest(HttpMethod.Get, $"{baseUrl}/api/version", apiKey);
        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<VersionInfo>(json, _jsonOptions)!;
    }

    // Printer Information
    public async Task<PrinterInfo> GetInfoAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        var request = CreateRequest(HttpMethod.Get, $"{baseUrl}/api/v1/info", apiKey);
        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<PrinterInfo>(json, _jsonOptions)!;
    }

    // Status Information
    public async Task<StatusInfo> GetStatusAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        var request = CreateRequest(HttpMethod.Get, $"{baseUrl}/api/v1/status", apiKey);
        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<StatusInfo>(json, _jsonOptions)!;
    }

    // Job Management
    public async Task<Job?> GetJobAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        var request = CreateRequest(HttpMethod.Get, $"{baseUrl}/api/v1/job", apiKey);
        var response = await _httpClient.SendAsync(request, ct);
        
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            return null;
        
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<Job>(json, _jsonOptions);
    }

    public async Task<bool> StopJobAsync(string baseUrl, int jobId, string? apiKey = null, CancellationToken ct = default)
    {
        var request = CreateRequest(HttpMethod.Delete, $"{baseUrl}/api/v1/job/{jobId}", apiKey);
        var response = await _httpClient.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> PauseJobAsync(string baseUrl, int jobId, string? apiKey = null, CancellationToken ct = default)
    {
        var request = CreateRequest(HttpMethod.Put, $"{baseUrl}/api/v1/job/{jobId}/pause", apiKey);
        var response = await _httpClient.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> ResumeJobAsync(string baseUrl, int jobId, string? apiKey = null, CancellationToken ct = default)
    {
        var request = CreateRequest(HttpMethod.Put, $"{baseUrl}/api/v1/job/{jobId}/resume", apiKey);
        var response = await _httpClient.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> ContinueJobAsync(string baseUrl, int jobId, string? apiKey = null, CancellationToken ct = default)
    {
        var request = CreateRequest(HttpMethod.Put, $"{baseUrl}/api/v1/job/{jobId}/continue", apiKey);
        var response = await _httpClient.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    // Storage Management
    public async Task<StorageListResponse> GetStorageAsync(string baseUrl, string? apiKey = null, string? acceptLanguage = null, CancellationToken ct = default)
    {
        var request = CreateRequest(HttpMethod.Get, $"{baseUrl}/api/v1/storage", apiKey);
        if (!string.IsNullOrWhiteSpace(acceptLanguage))
            request.Headers.Add("Accept-Language", acceptLanguage);
        
        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<StorageListResponse>(json, _jsonOptions)!;
    }

    // Transfer Management
    public async Task<Transfer?> GetTransferAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        var request = CreateRequest(HttpMethod.Get, $"{baseUrl}/api/v1/transfer", apiKey);
        var response = await _httpClient.SendAsync(request, ct);
        
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            return null;
        
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<Transfer>(json, _jsonOptions);
    }

    public async Task<bool> StopTransferAsync(string baseUrl, int transferId, string? apiKey = null, CancellationToken ct = default)
    {
        var request = CreateRequest(HttpMethod.Delete, $"{baseUrl}/api/v1/transfer/{transferId}", apiKey);
        var response = await _httpClient.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    // File Management
    public async Task<FileInfoBase> GetFileInfoAsync(string baseUrl, string storagePath, string filePath, string? apiKey = null, 
        string? acceptLanguage = null, string? accept = null, CancellationToken ct = default)
    {
        var request = CreateRequest(HttpMethod.Get, $"{baseUrl}/api/v1/files{storagePath}{filePath}", apiKey);
        if (!string.IsNullOrWhiteSpace(acceptLanguage))
            request.Headers.Add("Accept-Language", acceptLanguage);
        if (!string.IsNullOrWhiteSpace(accept))
            request.Headers.Add("Accept", accept);
        
        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        
        // Deserialize to appropriate type based on response content
        var document = JsonDocument.Parse(json);
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
        var request = CreateRequest(HttpMethod.Put, $"{baseUrl}/api/v1/files{storagePath}{filePath}", apiKey);
        
        request.Content = new StreamContent(fileStream);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentLength = fileStream.Length;
        
        request.Headers.Add("Print-After-Upload", printAfterUpload ? "?1" : "?0");
        request.Headers.Add("Overwrite", overwrite ? "?1" : "?0");
        
        var response = await _httpClient.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> StartPrintAsync(string baseUrl, string storagePath, string filePath, string? apiKey = null, CancellationToken ct = default)
    {
        var request = CreateRequest(HttpMethod.Post, $"{baseUrl}/api/v1/files{storagePath}{filePath}", apiKey);
        request.Content = new StringContent("");
        
        var response = await _httpClient.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<FileStatus> CheckFileStatusAsync(string baseUrl, string storagePath, string filePath, string? apiKey = null, CancellationToken ct = default)
    {
        var request = CreateRequest(HttpMethod.Head, $"{baseUrl}/api/v1/files{storagePath}{filePath}", apiKey);
        var response = await _httpClient.SendAsync(request, ct);
        
        if (!response.IsSuccessStatusCode)
            return new FileStatus(false, false, false);
        
        var readOnly = response.Headers.Contains("Read-Only") && 
            response.Headers.GetValues("Read-Only").FirstOrDefault() == "true";
        var currentlyPrinted = response.Headers.Contains("Currently-Printed") && 
            response.Headers.GetValues("Currently-Printed").FirstOrDefault() == "true";
        
        return new FileStatus(true, readOnly, currentlyPrinted);
    }

    public async Task<bool> DeleteFileAsync(string baseUrl, string storagePath, string filePath, string? apiKey = null, 
        bool force = false, CancellationToken ct = default)
    {
        var request = CreateRequest(HttpMethod.Delete, $"{baseUrl}/api/v1/files{storagePath}{filePath}", apiKey);
        request.Headers.Add("Force", force ? "?1" : "?0");
        
        var response = await _httpClient.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    // Camera Management
    public async Task<Camera[]> GetCamerasAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        var request = CreateRequest(HttpMethod.Get, $"{baseUrl}/api/v1/cameras", apiKey);
        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<Camera[]>(json, _jsonOptions)!;
    }

    public async Task<bool> SetCameraOrderAsync(string baseUrl, string[] cameraIds, string? apiKey = null, CancellationToken ct = default)
    {
        var request = CreateRequest(HttpMethod.Put, $"{baseUrl}/api/v1/cameras", apiKey);
        var jsonContent = JsonSerializer.Serialize(cameraIds, _jsonOptions);
        request.Content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");
        
        var response = await _httpClient.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<CameraConfig> GetCameraConfigAsync(string baseUrl, string cameraId, string? apiKey = null, CancellationToken ct = default)
    {
        var request = CreateRequest(HttpMethod.Get, $"{baseUrl}/api/v1/cameras/{cameraId}", apiKey);
        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<CameraConfig>(json, _jsonOptions)!;
    }

    public async Task<bool> SetupCameraAsync(string baseUrl, string cameraId, CameraConfigSet config, string? apiKey = null, CancellationToken ct = default)
    {
        var request = CreateRequest(HttpMethod.Post, $"{baseUrl}/api/v1/cameras/{cameraId}", apiKey);
        var jsonContent = JsonSerializer.Serialize(config, _jsonOptions);
        request.Content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");
        
        var response = await _httpClient.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> DeleteCameraAsync(string baseUrl, string cameraId, string? apiKey = null, CancellationToken ct = default)
    {
        var request = CreateRequest(HttpMethod.Delete, $"{baseUrl}/api/v1/cameras/{cameraId}", apiKey);
        var response = await _httpClient.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<byte[]?> TakeSnapshotAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        var request = CreateRequest(HttpMethod.Get, $"{baseUrl}/api/v1/cameras/snap", apiKey);
        var response = await _httpClient.SendAsync(request, ct);
        
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            return null;
        
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<byte[]?> TakeSnapshotAsync(string baseUrl, string cameraId, string? apiKey = null, CancellationToken ct = default)
    {
        var request = CreateRequest(HttpMethod.Get, $"{baseUrl}/api/v1/cameras/{cameraId}/snap", apiKey);
        var response = await _httpClient.SendAsync(request, ct);
        
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            return null;
        
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<byte[]?> TriggerSnapshotAsync(string baseUrl, string cameraId, string? apiKey = null, CancellationToken ct = default)
    {
        var request = CreateRequest(HttpMethod.Post, $"{baseUrl}/api/v1/cameras/{cameraId}/snap", apiKey);
        request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        
        var response = await _httpClient.SendAsync(request, ct);
        
        if (!response.IsSuccessStatusCode)
            return null;
        
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<bool> UpdateCameraConfigAsync(string baseUrl, string cameraId, CameraConfigSet config, string? apiKey = null, CancellationToken ct = default)
    {
        var request = CreateRequest(HttpMethod.Patch, $"{baseUrl}/api/v1/cameras/{cameraId}/config", apiKey);
        var jsonContent = JsonSerializer.Serialize(config, _jsonOptions);
        request.Content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");
        
        var response = await _httpClient.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> ResetCameraConfigAsync(string baseUrl, string cameraId, string? apiKey = null, CancellationToken ct = default)
    {
        var request = CreateRequest(HttpMethod.Delete, $"{baseUrl}/api/v1/cameras/{cameraId}/config", apiKey);
        var response = await _httpClient.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> RegisterCameraToConnectAsync(string baseUrl, string cameraId, string? apiKey = null, CancellationToken ct = default)
    {
        var request = CreateRequest(HttpMethod.Post, $"{baseUrl}/api/v1/cameras/{cameraId}/connection", apiKey);
        var response = await _httpClient.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> UnregisterCameraFromConnectAsync(string baseUrl, string cameraId, string? apiKey = null, CancellationToken ct = default)
    {
        var request = CreateRequest(HttpMethod.Delete, $"{baseUrl}/api/v1/cameras/{cameraId}/connection", apiKey);
        var response = await _httpClient.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    // Update Management
    public async Task<UpdateInfo?> GetUpdateInfoAsync(string baseUrl, string environment = "prusalink", string? apiKey = null, CancellationToken ct = default)
    {
        var request = CreateRequest(HttpMethod.Get, $"{baseUrl}/api/v1/update/{environment}", apiKey);
        var response = await _httpClient.SendAsync(request, ct);
        
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
            updateInfo.UpdateAvailable = response.Headers.GetValues("Update-Available").FirstOrDefault() == "true";
        
        return updateInfo;
    }

    public async Task<bool> StartUpdateAsync(string baseUrl, string environment = "prusalink", string? apiKey = null, CancellationToken ct = default)
    {
        var request = CreateRequest(HttpMethod.Post, $"{baseUrl}/api/v1/update/{environment}", apiKey);
        var response = await _httpClient.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string url, string? apiKey)
    {
        var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Add("X-Api-Key", apiKey);
        return request;
    }
}
