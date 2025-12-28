#pragma warning disable S1006, CA1033, S1939 // Default parameters, explicit interface implementations, and interface inheritance are intentional

using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Microsoft.Extensions.Logging;

namespace Farm.Backend.Plugin.OctoPrint;

#pragma warning disable CS1066 // Default value for optional parameter not enforced for interface members

/// <summary>
/// OctoPrint printer state DTO - encapsulates parsed /api/printer response.
/// </summary>
public sealed class OctoPrintPrinterState
{
    public bool Operational { get; set; }
    public bool Printing { get; set; }
    public string State { get; set; } = string.Empty;
    public Dictionary<string, object>? Temperatures { get; set; }
}

/// <summary>
/// OctoPrint job status DTO - encapsulates parsed /api/job response.
/// </summary>
public sealed class OctoPrintJobStatus
{
    public string? Filename { get; set; }
    public double? Progress { get; set; }
    public double? PrintTime { get; set; }
    public double? PrintTimeLeft { get; set; }
    public Dictionary<string, object>? Filament { get; set; }
}

public class OctoPrintClient(HttpClient httpClient, ILogger<OctoPrintClient>? logger = null) : IOctoPrintClient,
    ISupportsFileDownload,
    ISupportsFileList,
    ISupportsFileUpload,
    ISupportsCamera,
    ISupportsPrinterInformation,
    ISupportsHistory,
    ISupportsTemperatureControl
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly ILogger<OctoPrintClient>? _logger = logger;

    // Configuration
    private const int DefaultTimeoutSeconds = 30;
    private const int MaxRetryAttempts = 3;
    private const int RetryDelayMs = 1000;

    // Keep HttpClient internal; callers should use IOctoPrintClient.SendAsync
    internal HttpClient HttpClient => _httpClient;

    /// <summary>
    /// Normalizes a base URL by ensuring it doesn't have a trailing slash.
    /// </summary>
    /// <param name="baseUrl">The base URL to normalize</param>
    /// <returns>Normalized URL without trailing slash</returns>
    private static string NormalizeBaseUrl(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new ArgumentException("Base URL cannot be null or empty", nameof(baseUrl));
        }

        return baseUrl.TrimEnd('/');
    }

    /// <summary>
    /// Logs an HTTP request for debugging purposes.
    /// </summary>
    private void LogRequest(HttpRequestMessage request)
    {
        if (_logger?.IsEnabled(LogLevel.Debug) == true)
        {
            _logger.LogDebug(
                "OctoPrint HTTP Request: {Method} {Uri}",
                request.Method,
                request.RequestUri
            );
        }
    }

    /// <summary>
    /// Logs an HTTP response for debugging purposes.
    /// </summary>
    private void LogResponse(HttpResponseMessage response)
    {
        if (_logger?.IsEnabled(LogLevel.Debug) == true)
        {
            _logger.LogDebug(
                "OctoPrint HTTP Response: {StatusCode} {ReasonPhrase}",
                response.StatusCode,
                response.ReasonPhrase
            );
        }
    }

    /// <summary>
    /// Logs an error or warning.
    /// </summary>
    private void LogError(string message, Exception? ex = null)
    {
        if (ex != null)
        {
            _logger?.LogError(ex, "OctoPrint Client Error: {Message}", message);
        }
        else
        {
            _logger?.LogWarning("OctoPrint Client Warning: {Message}", message);
        }
    }

    /// <summary>
    /// Executes an HTTP request with retry logic and timeout handling.
    /// </summary>
    /// <param name="request">The HTTP request to send</param>
    /// <param name="timeoutSeconds">Timeout in seconds (default: 30)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The HTTP response</returns>
    private async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpRequestMessage request,
        int timeoutSeconds = DefaultTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        LogRequest(request);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        int attemptNumber = 0;
        while (true)
        {
            attemptNumber++;
            try
            {
                var response = await _httpClient.SendAsync(request, cts.Token);
                LogResponse(response);
                return response;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // Timeout occurred
                LogError($"Request timeout after {timeoutSeconds} seconds");
                throw new HttpRequestException($"OctoPrint request timeout after {timeoutSeconds} seconds");
            }
            catch (HttpRequestException ex) when (attemptNumber < MaxRetryAttempts && IsTransientError(ex))
            {
                // Transient error - retry
                LogError($"Transient error on attempt {attemptNumber}, retrying in {RetryDelayMs}ms", ex);
                await Task.Delay(RetryDelayMs * attemptNumber, cancellationToken); // Exponential backoff

                // Recreate request for retry (important: HttpRequestMessage can only be sent once)
                request = CloneRequest(request);
            }
        }
    }

    /// <summary>
    /// Determines if an exception is transient (worth retrying).
    /// </summary>
    private static bool IsTransientError(HttpRequestException ex)
    {
        // Retry on connection errors, timeouts, and 5xx errors
        return ex.InnerException is IOException or TimeoutException
               || ex.Message.Contains("Connection", StringComparison.OrdinalIgnoreCase)
               || ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Creates a copy of an HTTP request (since requests can only be sent once).
    /// </summary>
    private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
    {
        var newRequest = new HttpRequestMessage(request.Method, request.RequestUri);

        // Copy headers
        foreach (var header in request.Headers)
        {
            newRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        // Copy content if present
        if (request.Content != null)
        {
#pragma warning disable VSTHRD002
            var contentAsString = request.Content.ReadAsStringAsync().Result;
#pragma warning restore VSTHRD002
            newRequest.Content = new StringContent(contentAsString, Encoding.UTF8, "application/json");
        }

        return newRequest;
    }

    public async Task<bool> TestConnectionAsync(string baseUrl, string apiKey)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        HttpRequestMessage request = new(HttpMethod.Get, $"{baseUrl}/api/version");
        request.Headers.Add("X-Api-Key", apiKey);

        try
        {
            HttpResponseMessage response = await SendWithRetryAsync(request, timeoutSeconds: 10);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            LogError("Test connection failed", ex);
            return false;
        }
    }

    public async Task<OctoPrintPrinterState?> GetPrinterStateAsync(string baseUrl, string apiKey)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        HttpRequestMessage request = new(HttpMethod.Get, $"{baseUrl}/api/printer");
        request.Headers.Add("X-Api-Key", apiKey);

        try
        {
            HttpResponseMessage response = await SendWithRetryAsync(request);
            string content = await response.Content.ReadAsStringAsync();
            return ParsePrinterState(content);
        }
        catch (Exception ex)
        {
            LogError("Get printer state failed", ex);
            return null;
        }
    }

    public async Task<OctoPrintJobStatus?> GetJobStatusAsync(string baseUrl, string apiKey)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        HttpRequestMessage request = new(HttpMethod.Get, $"{baseUrl}/api/job");
        request.Headers.Add("X-Api-Key", apiKey);

        try
        {
            HttpResponseMessage response = await SendWithRetryAsync(request);
            string content = await response.Content.ReadAsStringAsync();
            return ParseJobStatus(content);
        }
        catch (Exception ex)
        {
            LogError("Get job status failed", ex);
            return null;
        }
    }

    public async Task<bool> StartJobAsync(string baseUrl, string apiKey, string fileName)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        // OctoPrint API: Select and start print via /api/files/local/{filename}
        // This endpoint requires URL encoding of special characters in filename
        string encodedFileName = Uri.EscapeDataString(fileName);
        HttpRequestMessage request = new(HttpMethod.Post, $"{baseUrl}/api/files/local/{encodedFileName}");
        request.Headers.Add("X-Api-Key", apiKey);
        var payload = new { command = "select", print = true };
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        try
        {
            HttpResponseMessage response = await SendWithRetryAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            LogError("Start job failed", ex);
            throw;
        }
    }

    public async Task<bool> CancelJobAsync(string baseUrl, string apiKey)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        HttpRequestMessage request = new(HttpMethod.Post, $"{baseUrl}/api/job");
        request.Headers.Add("X-Api-Key", apiKey);
        var payload = new { command = "cancel" };
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        try
        {
            HttpResponseMessage response = await SendWithRetryAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            LogError("Cancel job failed", ex);
            throw;
        }
    }

    public Task<string?> GetCameraStreamUrlAsync(string baseUrl, string apiKey)
    {
        // OctoPrint camera stream is typically a static URL, not an API call
        // This can be constructed from the baseUrl or stored in the printer config
        baseUrl = NormalizeBaseUrl(baseUrl);
        return Task.FromResult<string?>($"{baseUrl}/webcam/?action=stream");
    }

    public async Task<string[]> GetFileListAsync(string baseUrl, string apiKey)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        try
        {
            HttpRequestMessage request = new(HttpMethod.Get, $"{baseUrl}/api/files/local?recursive=true");
            request.Headers.Add("X-Api-Key", apiKey);
            HttpResponseMessage response = await SendWithRetryAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                return Array.Empty<string>();
            }

            string jsonContent = await response.Content.ReadAsStringAsync();
            using JsonDocument doc = JsonDocument.Parse(jsonContent);
            JsonElement root = doc.RootElement;

            List<string> files = new();
            if (root.TryGetProperty("files", out JsonElement filesArray))
            {
                ExtractFileNamesFromJson(filesArray, files, "");
            }

            return files.ToArray();
        }
        catch (Exception ex)
        {
            LogError("Get file list failed", ex);
            return Array.Empty<string>();
        }
    }

    private static void ExtractFileNamesFromJson(JsonElement element, List<string> files, string path)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                if (item.TryGetProperty("name", out JsonElement nameEl) && item.TryGetProperty("type", out JsonElement typeEl))
                {
                    string name = nameEl.GetString() ?? "";
                    string type = typeEl.GetString() ?? "";
                    string fullPath = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";

                    // Filter for machine code files (gcode)
                    if (type.Equals("machinecode", StringComparison.OrdinalIgnoreCase))
                    {
                        files.Add(fullPath);
                    }
                }
            }
        }
    }

    public async Task<PrinterDto> CreatePrinterDtoAsync(Printer printer, string printerStateJson, string jobStatusJson, string apiKey, CancellationToken ct = default)
    {
        // Check for position and spool manager plugins
        bool hasPositionPlugin = false;
        string normalizedUrl = NormalizeBaseUrl(printer.ServerUrl);
        try
        {
            HttpRequestMessage pluginsRequest = new(HttpMethod.Get, $"{normalizedUrl}/api/plugins");
            pluginsRequest.Headers.Add("X-Api-Key", apiKey);
            HttpResponseMessage pluginsResponse = await SendWithRetryAsync(pluginsRequest, cancellationToken: ct);
            string pluginsJson = await pluginsResponse.Content.ReadAsStringAsync(ct);
            if (!string.IsNullOrWhiteSpace(pluginsJson))
            {
                using JsonDocument doc = JsonDocument.Parse(pluginsJson);
                JsonElement root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("plugins", out JsonElement pluginsProp))
                {
                    foreach (JsonElement plugin in pluginsProp.EnumerateArray())
                    {
                        if (plugin.TryGetProperty("key", out JsonElement keyProp))
                        {
                            string? key = keyProp.GetString();
                            if (!string.IsNullOrEmpty(key))
                            {
                                if (key.Equals("display_current_position", StringComparison.OrdinalIgnoreCase) || key.Equals("positioninfo", StringComparison.OrdinalIgnoreCase))
                                {
                                    hasPositionPlugin = true;
                                }
                            }
                        }
                    }
                }
            }
        }
        catch { }

        // Parse printer state JSON
        bool isOnline = false;
        string? state = null;
        double? hotendTemp = null;
        double? bedTemp = null;
        double? hotendTarget = null;
        double? bedTarget = null;
        double? x = null, y = null, z = null;

        if (!string.IsNullOrWhiteSpace(printerStateJson))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(printerStateJson);
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("state", out JsonElement stateProp))
                {
                    state = stateProp.GetString();
                    isOnline = state != null && state != "Offline";
                }
                if (root.TryGetProperty("temperature", out JsonElement tempProp))
                {
                    if (tempProp.TryGetProperty("tool0", out JsonElement tool0))
                    {
                        if (tool0.TryGetProperty("actual", out JsonElement actual))
                        {
                            hotendTemp = actual.GetDouble();
                        }
                        if (tool0.TryGetProperty("target", out JsonElement target))
                        {
                            hotendTarget = target.GetDouble();
                        }
                    }
                    if (tempProp.TryGetProperty("bed", out JsonElement bed))
                    {
                        if (bed.TryGetProperty("actual", out JsonElement actual))
                        {
                            bedTemp = actual.GetDouble();
                        }
                        if (bed.TryGetProperty("target", out JsonElement target))
                        {
                            bedTarget = target.GetDouble();
                        }
                    }
                }

                if (hasPositionPlugin && root.TryGetProperty("position", out JsonElement posProp))
                {
                    if (posProp.TryGetProperty("x", out JsonElement xProp))
                    {
                        x = xProp.GetDouble();
                    }
                    if (posProp.TryGetProperty("y", out JsonElement yProp))
                    {
                        y = yProp.GetDouble();
                    }
                    if (posProp.TryGetProperty("z", out JsonElement zProp))
                    {
                        z = zProp.GetDouble();
                    }
                }
            }
            catch { }
        }

        // Parse job JSON
        double? progress = null;
        string? jobName = null;
        if (!string.IsNullOrWhiteSpace(jobStatusJson))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(jobStatusJson);
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("progress", out JsonElement progressProp))
                {
                    if (progressProp.TryGetProperty("completion", out JsonElement completion))
                    {
                        progress = completion.GetDouble();
                    }
                }
                if (root.TryGetProperty("job", out JsonElement jobProp))
                {
                    if (jobProp.TryGetProperty("file", out JsonElement fileProp))
                    {
                        if (fileProp.TryGetProperty("name", out JsonElement nameProp))
                        {
                            jobName = nameProp.GetString();
                        }
                    }
                }
            }
            catch { }
        }

        return new PrinterDto(
            Id: printer.Id,
            Name: printer.Name,
            Notes: printer.Notes,
            IsOnline: isOnline,
            State: state,
            ManufacturerName: printer.Manufacturer?.Name,
            ModelName: printer.Model?.Name,
            Progress: progress,
            JobName: jobName,
            ThumbnailUrl: null,
            CameraStreamUrl: await GetCameraStreamUrlAsync(printer.ServerUrl, apiKey),
            CameraSnapshotUrl: null,
            HotendTemp: hotendTemp,
            BedTemp: bedTemp,
            HotendTarget: hotendTarget,
            BedTarget: bedTarget,
            X: x,
            Y: y,
            Z: z,
            Backend: PrinterBackend.OctoPrint,
            ApiKey: printer.ApiKey,
            OriginalServerUrl: printer.OriginalServerUrl,
            IpAddress: printer.IpAddress,
            BackendPort: printer.BackendPort,
            FrontendPort: printer.FrontendPort,
            BackendUrl: printer.BackendUrl,
            FrontendUrl: printer.FrontendUrl
        );
    }

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        return SendWithRetryAsync(request, cancellationToken: cancellationToken);
    }

    public async Task<HistoryListResponse?> GetHistoryListAsync(string baseUrl, string apiKey, int? limit = null, int? start = null)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        HttpRequestMessage request = new(HttpMethod.Get, $"{baseUrl}/api/history");
        request.Headers.Add("X-Api-Key", apiKey);

        // OctoPrint supports limit and start query parameters for pagination
        var queryParams = new List<string>();
        if (limit.HasValue)
        {
            queryParams.Add($"limit={limit.Value}");
        }

        if (start.HasValue)
        {
            queryParams.Add($"start={start.Value}");
        }

        if (queryParams.Count > 0)
        {
            request.RequestUri = new Uri($"{baseUrl}/api/history?{string.Join("&", queryParams)}");
        }

        try
        {
            HttpResponseMessage response = await SendWithRetryAsync(request);
            string content = await response.Content.ReadAsStringAsync();
            return ParseOctoPrintHistoryList(content);
        }
        catch (Exception ex)
        {
            LogError("Get history list failed", ex);
            return null;
        }
    }

    public async Task<HistoryJob?> GetHistoryJobAsync(string baseUrl, string apiKey, string jobId)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        HttpRequestMessage request = new(HttpMethod.Get, $"{baseUrl}/api/history/{Uri.EscapeDataString(jobId)}");
        request.Headers.Add("X-Api-Key", apiKey);

        try
        {
            HttpResponseMessage response = await SendWithRetryAsync(request);
            string content = await response.Content.ReadAsStringAsync();
            return ParseOctoPrintHistoryJob(content);
        }
        catch (Exception ex)
        {
            LogError("Get history job failed", ex);
            return null;
        }
    }

    /// <summary>
    /// Gets history totals (wrapper for interface compatibility)
    /// </summary>
    public async Task<HistoryTotals?> GetHistoryTotalsAsync(string baseUrl, string apiKey)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        HttpRequestMessage request = new(HttpMethod.Get, $"{baseUrl}/api/history?limit=0");
        request.Headers.Add("X-Api-Key", apiKey);

        try
        {
            HttpResponseMessage response = await SendWithRetryAsync(request);
            string content = await response.Content.ReadAsStringAsync();
            var historyList = ParseOctoPrintHistoryList(content);
            if (historyList == null)
            {
                return null;
            }

            // Calculate totals from the job list
            double totalPrintTime = 0;
            double totalFilamentUsed = 0;
            int jobCount = 0;

            foreach (var job in historyList.Jobs)
            {
                if (job.Status == "Completed")
                {
                    totalPrintTime += job.PrintDuration;
                    totalFilamentUsed += job.FilamentUsed;
                    jobCount++;
                }
            }

            return new HistoryTotals
            {
                JobTotals = new JobTotals
                {
                    TotalJobs = jobCount,
                    TotalPrintTime = totalPrintTime,
                    TotalFilamentUsed = totalFilamentUsed
                }
            };
        }
        catch (Exception ex)
        {
            LogError("Get history totals failed", ex);
            return null;
        }
    }

    /// <summary>
    /// Deletes a job from history (wrapper for interface compatibility)
    /// </summary>
    public async Task<bool> DeleteHistoryJobAsync(string baseUrl, string apiKey, string jobId)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        HttpRequestMessage request = new(HttpMethod.Delete, $"{baseUrl}/api/history/{Uri.EscapeDataString(jobId)}");
        request.Headers.Add("X-Api-Key", apiKey);

        try
        {
            HttpResponseMessage response = await SendWithRetryAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            LogError("Delete history job failed", ex);
            return false;
        }
    }

    /// <summary>
    /// Sets temperatures (unified method for both bed and hotend temperatures)
    /// </summary>
    public async Task<bool> SetTemperaturesAsync(string baseUrl, string apiKey, double? hotendTemp = null, double? bedTemp = null)
    {
        bool success = true;

        if (bedTemp.HasValue)
        {
            success = await SetBedTempAsync(baseUrl, apiKey, bedTemp.Value) && success;
        }

        if (hotendTemp.HasValue)
        {
            success = await SetHotendTempAsync(baseUrl, apiKey, hotendTemp.Value) && success;
        }

        return success;
    }

    public async Task<bool> SendGcodeAsync(string baseUrl, string apiKey, string gcode)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        HttpRequestMessage request = new(HttpMethod.Post, $"{baseUrl}/api/printer/command");
        request.Headers.Add("X-Api-Key", apiKey);

        // Use JsonSerializer to properly escape special characters and newlines
        var payload = new { command = gcode };
        string json = JsonSerializer.Serialize(payload);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            HttpResponseMessage response = await SendWithRetryAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            LogError("Send gcode failed", ex);
            throw;
        }
    }

    /// <summary>
    /// Homes all axes using native OctoPrint API (preferred over gcode).
    /// </summary>
    public async Task<bool> SendHomeAsync(string baseUrl, string apiKey)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        HttpRequestMessage request = new(HttpMethod.Post, $"{baseUrl}/api/printer/printhead");
        request.Headers.Add("X-Api-Key", apiKey);

        var homeCommand = new { command = "home", axes = new[] { "x", "y", "z" } };
        string json = JsonSerializer.Serialize(homeCommand);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            HttpResponseMessage response = await SendWithRetryAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            LogError("Send home failed", ex);
            throw;
        }
    }

    /// <summary>
    /// Homes XY axes only using native OctoPrint API.
    /// </summary>
    public async Task<bool> HomeXYAsync(string baseUrl, string apiKey)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        HttpRequestMessage request = new(HttpMethod.Post, $"{baseUrl}/api/printer/printhead");
        request.Headers.Add("X-Api-Key", apiKey);

        var homeCommand = new { command = "home", axes = new[] { "x", "y" } };
        string json = JsonSerializer.Serialize(homeCommand);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            HttpResponseMessage response = await SendWithRetryAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            LogError("Home XY failed", ex);
            throw;
        }
    }

    /// <summary>
    /// Homes Z axis only using native OctoPrint API.
    /// </summary>
    public async Task<bool> HomeZAsync(string baseUrl, string apiKey)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        HttpRequestMessage request = new(HttpMethod.Post, $"{baseUrl}/api/printer/printhead");
        request.Headers.Add("X-Api-Key", apiKey);

        var homeCommand = new { command = "home", axes = new[] { "z" } };
        string json = JsonSerializer.Serialize(homeCommand);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            HttpResponseMessage response = await SendWithRetryAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            LogError("Home Z failed", ex);
            throw;
        }
    }

    /// <summary>
    /// Sets target temperature for bed using native OctoPrint API (preferred method).
    /// Uses /api/printer/bed endpoint which is the OctoPrint-native way to control bed temperature.
    /// </summary>
    public async Task<bool> SetBedTempAsync(string baseUrl, string apiKey, double bedTemp)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        HttpRequestMessage request = new(HttpMethod.Post, $"{baseUrl}/api/printer/bed");
        request.Headers.Add("X-Api-Key", apiKey);

        var bedCommand = new { command = "target", target = (int)bedTemp };
        string json = JsonSerializer.Serialize(bedCommand);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            HttpResponseMessage response = await SendWithRetryAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            LogError("Set bed temperature failed", ex);
            throw;
        }
    }

    /// <summary>
    /// Sets target temperature for hotend (tool) using native OctoPrint API.
    /// Uses the /api/printer/tool endpoint with the "target" command.
    /// </summary>
    /// <param name="baseUrl">Base URL of OctoPrint server</param>
    /// <param name="apiKey">OctoPrint API key</param>
    /// <param name="hotendTemp">Target temperature in Celsius (0 to turn off)</param>
    /// <param name="tool">Tool index to set temperature for (default "tool0" for first hotend)</param>
    public async Task<bool> SetHotendTempAsync(string baseUrl, string apiKey, double hotendTemp, string tool = "tool0")
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        HttpRequestMessage request = new(HttpMethod.Post, $"{baseUrl}/api/printer/tool");
        request.Headers.Add("X-Api-Key", apiKey);

        var toolCommand = new { command = "target", targets = new Dictionary<string, int> { { tool, (int)hotendTemp } } };
        string json = JsonSerializer.Serialize(toolCommand);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            HttpResponseMessage response = await SendWithRetryAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            LogError("Set hotend temperature failed", ex);
            throw;
        }
    }

    /// <summary>
    /// Pauses the current print job.
    /// </summary>
    public async Task<bool> PauseAsync(string baseUrl, string apiKey)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        HttpRequestMessage request = new(HttpMethod.Post, $"{baseUrl}/api/job");
        request.Headers.Add("X-Api-Key", apiKey);
        request.Content = new StringContent("{\"command\":\"pause\",\"action\":\"pause\"}", Encoding.UTF8, "application/json");

        try
        {
            HttpResponseMessage response = await SendWithRetryAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            LogError("Pause print failed", ex);
            throw;
        }
    }

    public async Task<bool> ResumeAsync(string baseUrl, string apiKey)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        HttpRequestMessage request = new(HttpMethod.Post, $"{baseUrl}/api/job");
        request.Headers.Add("X-Api-Key", apiKey);
        request.Content = new StringContent("{\"command\":\"pause\",\"action\":\"resume\"}", Encoding.UTF8, "application/json");

        try
        {
            HttpResponseMessage response = await SendWithRetryAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            LogError("Resume print failed", ex);
            throw;
        }
    }

    public async Task<bool> CancelPrintAsync(string baseUrl, string apiKey)
    {
        return await CancelJobAsync(baseUrl, apiKey);
    }

    /// <summary>
    /// Jogs the printhead (moves axes incrementally without homing).
    /// Allows relative movement for bed leveling, nozzle positioning, etc.
    /// </summary>
    /// <param name="baseUrl">Base URL of OctoPrint server</param>
    /// <param name="apiKey">OctoPrint API key</param>
    /// <param name="x">X axis movement (mm), optional</param>
    /// <param name="y">Y axis movement (mm), optional</param>
    /// <param name="z">Z axis movement (mm), optional</param>
    /// <param name="speed">Movement speed (mm/min), optional</param>
    /// <returns>Success status</returns>
    public async Task<bool> JogAsync(string baseUrl, string apiKey, double? x = null, double? y = null, double? z = null, double? speed = null)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        HttpRequestMessage request = new(HttpMethod.Post, $"{baseUrl}/api/printer/printhead");
        request.Headers.Add("X-Api-Key", apiKey);

        // Build jog command with only provided axes
        var jogCommand = new { command = "jog", x = x ?? 0, y = y ?? 0, z = z ?? 0 };
        string json = JsonSerializer.Serialize(jogCommand);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            HttpResponseMessage response = await SendWithRetryAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            LogError("Jog printhead failed", ex);
            throw;
        }
    }

    /// <summary>
    /// Connects the printer (initiates connection to physical device).
    /// </summary>
    /// <param name="baseUrl">Base URL of OctoPrint server</param>
    /// <param name="apiKey">OctoPrint API key</param>
    /// <returns>Success status</returns>
    public async Task<bool> ConnectAsync(string baseUrl, string apiKey)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        HttpRequestMessage request = new(HttpMethod.Post, $"{baseUrl}/api/connection");
        request.Headers.Add("X-Api-Key", apiKey);
        request.Content = new StringContent("{\"command\":\"connect\"}", Encoding.UTF8, "application/json");

        try
        {
            HttpResponseMessage response = await SendWithRetryAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            LogError("Connect to printer failed", ex);
            throw;
        }
    }

    /// <summary>
    /// Disconnects the printer (closes connection to physical device).
    /// </summary>
    /// <param name="baseUrl">Base URL of OctoPrint server</param>
    /// <param name="apiKey">OctoPrint API key</param>
    /// <returns>Success status</returns>
    public async Task<bool> DisconnectAsync(string baseUrl, string apiKey)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        HttpRequestMessage request = new(HttpMethod.Post, $"{baseUrl}/api/connection");
        request.Headers.Add("X-Api-Key", apiKey);
        request.Content = new StringContent("{\"command\":\"disconnect\"}", Encoding.UTF8, "application/json");

        try
        {
            HttpResponseMessage response = await SendWithRetryAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            LogError("Disconnect from printer failed", ex);
            throw;
        }
    }

    /// <summary>
    /// Gets the current connection state of the printer.
    /// </summary>
    /// <param name="baseUrl">Base URL of OctoPrint server</param>
    /// <param name="apiKey">OctoPrint API key</param>
    /// <returns>JSON string with connection information</returns>
    public async Task<string> GetConnectionStateAsync(string baseUrl, string apiKey)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        HttpRequestMessage request = new(HttpMethod.Get, $"{baseUrl}/api/connection");
        request.Headers.Add("X-Api-Key", apiKey);

        try
        {
            HttpResponseMessage response = await SendWithRetryAsync(request);
            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            LogError("Get connection state failed", ex);
            throw;
        }
    }

    /// <summary>
    /// Gets file details/metadata for a specific file on the printer.
    /// </summary>
    /// <param name="baseUrl">Base URL of OctoPrint server</param>
    /// <param name="apiKey">OctoPrint API key</param>
    /// <param name="path">File path (e.g., "folder/file.gcode" or just "file.gcode")</param>
    /// <returns>JSON string with file metadata (name, size, date, hash, etc.)</returns>
    public async Task<string> GetFileDetailsAsync(string baseUrl, string apiKey, string path)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        // Ensure path doesn't start with slash (OctoPrint expects clean paths)
        string cleanPath = path.TrimStart('/');
        HttpRequestMessage request = new(HttpMethod.Get, $"{baseUrl}/api/files/local/{cleanPath}");
        request.Headers.Add("X-Api-Key", apiKey);

        try
        {
            HttpResponseMessage response = await SendWithRetryAsync(request);
            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            LogError("Get file details failed", ex);
            throw;
        }
    }

    /// <summary>
    /// Moves or renames a file or folder on the printer.
    /// Uses OctoPrint /api/files/local/{path} POST endpoint with "move" command.
    /// </summary>
    /// <param name="baseUrl">Base URL of OctoPrint server</param>
    /// <param name="apiKey">OctoPrint API key</param>
    /// <param name="source">Source file/folder path (e.g., "old_name.gcode")</param>
    /// <param name="destination">Destination path (e.g., "new_folder/new_name.gcode")</param>
    /// <returns>Success status</returns>
    public async Task<bool> MoveFileAsync(string baseUrl, string apiKey, string source, string destination)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        // Clean paths
        string cleanSource = source.TrimStart('/');
        string cleanDestination = destination.TrimStart('/');

        HttpRequestMessage request = new(HttpMethod.Post, $"{baseUrl}/api/files/local/{cleanSource}");
        request.Headers.Add("X-Api-Key", apiKey);

        // Build move command
        var moveCommand = new { command = "move", destination = cleanDestination };
        string json = JsonSerializer.Serialize(moveCommand);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            HttpResponseMessage response = await SendWithRetryAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            LogError("Move file failed", ex);
            throw;
        }
    }

    /// <summary>
    /// Deletes a file or folder from the printer.
    /// Uses OctoPrint /api/files/local/{path} DELETE endpoint.
    /// </summary>
    /// <param name="baseUrl">Base URL of OctoPrint server</param>
    /// <param name="apiKey">OctoPrint API key</param>
    /// <param name="path">File/folder path to delete (e.g., "folder/file.gcode")</param>
    /// <returns>Success status</returns>
    public async Task<bool> DeleteFileAsync(string baseUrl, string apiKey, string path)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        // Clean path
        string cleanPath = path.TrimStart('/');

        HttpRequestMessage request = new(HttpMethod.Delete, $"{baseUrl}/api/files/local/{cleanPath}");
        request.Headers.Add("X-Api-Key", apiKey);

        try
        {
            HttpResponseMessage response = await SendWithRetryAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            LogError("Delete file failed", ex);
            throw;
        }
    }

    /// <summary>
    /// Creates a new folder on the printer's storage.
    /// Uses OctoPrint /api/files/local/{path} POST endpoint with "makedir" command.
    /// </summary>
    /// <param name="baseUrl">Base URL of OctoPrint server</param>
    /// <param name="apiKey">OctoPrint API key</param>
    /// <param name="path">Path where folder should be created (e.g., "folder")</param>
    /// <param name="folderName">Name of the new folder</param>
    /// <returns>Success status</returns>
    public async Task<bool> CreateFolderAsync(string baseUrl, string apiKey, string path, string folderName)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        // Clean path - use empty string for root, otherwise clean the path
        string cleanPath = string.IsNullOrWhiteSpace(path) ? "" : path.TrimStart('/');

        // Build the full endpoint path
        string endpoint = string.IsNullOrEmpty(cleanPath)
            ? $"{baseUrl}/api/files/local"
            : $"{baseUrl}/api/files/local/{cleanPath}";

        HttpRequestMessage request = new(HttpMethod.Post, endpoint);
        request.Headers.Add("X-Api-Key", apiKey);

        // Build makedir command
        var mkdirCommand = new { command = "makedir", foldername = folderName };
        string json = JsonSerializer.Serialize(mkdirCommand);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            HttpResponseMessage response = await SendWithRetryAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            LogError("Create folder failed", ex);
            throw;
        }
    }

    /// <summary>
    /// Uploads a gcode file to the printer.
    /// Uses OctoPrint /api/files/local POST endpoint with multipart form data.
    /// </summary>
    /// <param name="baseUrl">Base URL of OctoPrint server</param>
    /// <param name="apiKey">OctoPrint API key</param>
    /// <param name="fileContent">File content as byte array</param>
    /// <param name="fileName">Name of the file to upload</param>
    /// <param name="path">Optional destination folder (e.g., "folder" or null for root)</param>
    /// <param name="startPrint">Whether to start printing immediately after upload</param>
    /// <returns>Success status</returns>
    public async Task<bool> UploadFileAsync(string baseUrl, string apiKey, byte[] fileContent, string fileName, string? path = null, bool startPrint = false)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        // Build endpoint URL
        string endpoint = path == null || string.IsNullOrWhiteSpace(path)
            ? $"{baseUrl}/api/files/local"
            : $"{baseUrl}/api/files/local/{path.TrimStart('/')}";

        HttpRequestMessage request = new(HttpMethod.Post, endpoint);
        request.Headers.Add("X-Api-Key", apiKey);

        // Build multipart form data
        var content = new MultipartFormDataContent();

        // Add file content
        var fileStream = new MemoryStream(fileContent);
        var streamContent = new StreamContent(fileStream);
        streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        content.Add(streamContent, "file", fileName);

        // Add print parameter if requested
        if (startPrint)
        {
            content.Add(new StringContent("true"), "print");
        }

        request.Content = content;

        try
        {
            HttpResponseMessage response = await SendWithRetryAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            LogError("Upload file failed", ex);
            throw;
        }
    }

    // Priority 3: Settings Management

    /// <summary>
    /// Gets OctoPrint server configuration/settings.
    /// Includes API version, data folder, temperature profiles, and other settings.
    /// </summary>
    /// <param name="baseUrl">Base URL of OctoPrint server</param>
    /// <param name="apiKey">OctoPrint API key</param>
    /// <returns>JSON string with server settings</returns>
    public async Task<string> GetSettingsAsync(string baseUrl, string apiKey)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        HttpRequestMessage request = new(HttpMethod.Get, $"{baseUrl}/api/settings");
        request.Headers.Add("X-Api-Key", apiKey);

        try
        {
            HttpResponseMessage response = await SendWithRetryAsync(request);
            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            LogError("Get settings failed", ex);
            throw;
        }
    }

    /// <summary>
    /// Updates OctoPrint server settings.
    /// Allows configuration changes via API.
    /// </summary>
    /// <param name="baseUrl">Base URL of OctoPrint server</param>
    /// <param name="apiKey">OctoPrint API key</param>
    /// <param name="settingsJson">JSON settings object to update</param>
    /// <returns>Success status</returns>
    public async Task<bool> UpdateSettingsAsync(string baseUrl, string apiKey, string settingsJson)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        HttpRequestMessage request = new(HttpMethod.Post, $"{baseUrl}/api/settings");
        request.Headers.Add("X-Api-Key", apiKey);
        request.Content = new StringContent(settingsJson, Encoding.UTF8, "application/json");

        try
        {
            HttpResponseMessage response = await SendWithRetryAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            LogError("Update settings failed", ex);
            throw;
        }
    }

    // Priority 3: System Operations

    /// <summary>
    /// Restarts the OctoPrint server.
    /// </summary>
    /// <param name="baseUrl">Base URL of OctoPrint server</param>
    /// <param name="apiKey">OctoPrint API key</param>
    /// <returns>Success status</returns>
    public async Task<bool> RestartServerAsync(string baseUrl, string apiKey)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        HttpRequestMessage request = new(HttpMethod.Post, $"{baseUrl}/api/system/commands/core/restart");
        request.Headers.Add("X-Api-Key", apiKey);
        request.Content = new StringContent("", Encoding.UTF8, "application/json");

        try
        {
            HttpResponseMessage response = await SendWithRetryAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            LogError("Restart server failed", ex);
            throw;
        }
    }

    /// <summary>
    /// Gets detailed system information about the OctoPrint server.
    /// Includes operating system, Python version, OctoPrint version, and environment details.
    /// </summary>
    /// <param name="baseUrl">Base URL of OctoPrint server</param>
    /// <param name="apiKey">OctoPrint API key</param>
    /// <returns>JSON string with system information</returns>
    public async Task<string> GetSystemInfoAsync(string baseUrl, string apiKey)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        HttpRequestMessage request = new(HttpMethod.Get, $"{baseUrl}/api/system");
        request.Headers.Add("X-Api-Key", apiKey);

        try
        {
            HttpResponseMessage response = await SendWithRetryAsync(request);
            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            LogError("Get system info failed", ex);
            throw;
        }
    }

    /// <summary>
    /// Executes a system command on the OctoPrint host via the system endpoint.
    /// Requires system command plugin or appropriate permissions.
    /// </summary>
    /// <param name="baseUrl">Base URL of OctoPrint server</param>
    /// <param name="apiKey">OctoPrint API key</param>
    /// <param name="commandId">System command ID to execute (e.g., "reboot", "shutdown")</param>
    /// <returns>Success status</returns>
    public async Task<bool> ExecuteSystemCommandAsync(string baseUrl, string apiKey, string commandId)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        HttpRequestMessage request = new(HttpMethod.Post, $"{baseUrl}/api/system/commands/core/{commandId}");
        request.Headers.Add("X-Api-Key", apiKey);
        request.Content = new StringContent("", Encoding.UTF8, "application/json");

        try
        {
            HttpResponseMessage response = await SendWithRetryAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            LogError($"Execute system command '{commandId}' failed", ex);
            throw;
        }
    }

    // Priority 3: Server Info

    /// <summary>
    /// Gets detailed version information for OctoPrint server components.
    /// Includes OctoPrint version, OS, Python version, and plugin versions.
    /// </summary>
    /// <param name="baseUrl">Base URL of OctoPrint server</param>
    /// <param name="apiKey">OctoPrint API key</param>
    /// <returns>JSON string with detailed version information</returns>
    public async Task<string> GetVersionInfoAsync(string baseUrl, string apiKey)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        HttpRequestMessage request = new(HttpMethod.Get, $"{baseUrl}/api/version");
        request.Headers.Add("X-Api-Key", apiKey);

        try
        {
            HttpResponseMessage response = await SendWithRetryAsync(request);
            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            LogError("Get version info failed", ex);
            throw;
        }
    }

    /// <summary>
    /// Downloads the contents of a gcode file from OctoPrint storage.
    /// </summary>
    /// <param name="baseUrl">Base URL of OctoPrint server</param>
    /// <param name="apiKey">OctoPrint API key</param>
    /// <param name="filePath">File path relative to local storage (e.g., "my_print.gcode")</param>
    /// <returns>File contents as byte array</returns>
    public async Task<byte[]> DownloadFileAsync(string baseUrl, string apiKey, string filePath)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        // Clean up file path - remove leading/trailing slashes
        filePath = filePath?.TrimStart('/').TrimEnd('/') ?? "";

        HttpRequestMessage request = new(HttpMethod.Get, $"{baseUrl}/downloads/files/local/{filePath}");
        request.Headers.Add("X-Api-Key", apiKey);

        try
        {
            LogRequest(request);
            HttpResponseMessage response = await SendWithRetryAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                LogError($"Download file failed with status {response.StatusCode}", null);
                throw new HttpRequestException($"Download file failed: {response.StatusCode}");
            }

            byte[] content = await response.Content.ReadAsByteArrayAsync();
            LogResponse(response);
            return content;
        }
        catch (Exception ex)
        {
            LogError("Download file failed", ex);
            throw;
        }
    }

    /// <summary>
    /// Selects a file for printing without automatically starting the print job.
    /// Use this to prepare a file; call StartJobAsync to begin printing.
    /// </summary>
    /// <param name="baseUrl">Base URL of OctoPrint server</param>
    /// <param name="apiKey">OctoPrint API key</param>
    /// <param name="filePath">File path relative to local storage (e.g., "my_print.gcode")</param>
    /// <returns>Success status</returns>
    public async Task<bool> LoadFileAsync(string baseUrl, string apiKey, string filePath)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        HttpRequestMessage request = new(HttpMethod.Post, $"{baseUrl}/api/job");
        request.Headers.Add("X-Api-Key", apiKey);

        // Create the select command
        var selectCommand = new { command = "select", file = filePath };
        string jsonContent = JsonSerializer.Serialize(selectCommand);
        request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        try
        {
            LogRequest(request);
            HttpResponseMessage response = await SendWithRetryAsync(request);
            bool success = response.IsSuccessStatusCode;

            if (!success)
            {
                LogError($"Load file failed with status {response.StatusCode}", null);
            }
            else
            {
                LogResponse(response);
            }

            return success;
        }
        catch (Exception ex)
        {
            LogError("Load file failed", ex);
            throw;
        }
    }

    /// <summary>
    /// Parses OctoPrint history JSON response into a HistoryListResponse object.
    /// </summary>
    private static HistoryListResponse? ParseOctoPrintHistoryList(string historyJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(historyJson);
            JsonElement root = doc.RootElement;

            // Check if the response indicates success
            if (!root.TryGetProperty("success", out JsonElement successProp) || !successProp.GetBoolean())
            {
                return null;
            }

            List<HistoryJob> jobs = new();

            // Parse the results array
            if (root.TryGetProperty("results", out JsonElement resultsProp) && resultsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement jobElement in resultsProp.EnumerateArray())
                {
                    var job = ParseOctoPrintJobElement(jobElement);
                    if (job != null)
                    {
                        jobs.Add(job);
                    }
                }
            }

            int count = jobs.Count;
            if (root.TryGetProperty("count", out JsonElement countProp))
            {
                count = countProp.GetInt32();
            }

            return new HistoryListResponse { Count = count, Jobs = jobs.ToArray() };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses a single OctoPrint job element into a HistoryJob object.
    /// </summary>
    private static HistoryJob? ParseOctoPrintJobElement(JsonElement jobElement)
    {
        try
        {
            var job = new HistoryJob();

            // Extract basic job information
            if (jobElement.TryGetProperty("name", out JsonElement nameProp))
            {
                job.Filename = nameProp.GetString() ?? string.Empty;
                job.JobId = job.Filename;
            }

            if (jobElement.TryGetProperty("success", out JsonElement successProp))
            {
                job.Status = successProp.GetBoolean() ? "Completed" : "Failed";
            }

            if (jobElement.TryGetProperty("printTime", out JsonElement printTimeProp) && printTimeProp.ValueKind != JsonValueKind.Null)
            {
                job.PrintDuration = printTimeProp.GetDouble();
            }

            if (jobElement.TryGetProperty("filament", out JsonElement filamentProp) && filamentProp.ValueKind == JsonValueKind.Object)
            {
                if (filamentProp.TryGetProperty("length", out JsonElement lengthProp))
                {
                    job.FilamentUsed = lengthProp.GetDouble() / 1000.0; // Convert from mm to m
                }
            }

            // Extract timestamp information
            if (jobElement.TryGetProperty("timestamp", out JsonElement timestampProp))
            {
                job.StartTime = timestampProp.GetDouble();
            }

            if (jobElement.TryGetProperty("completionTime", out JsonElement completionProp) && completionProp.ValueKind != JsonValueKind.Null)
            {
                job.EndTime = completionProp.GetDouble();
            }

            return job;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses an OctoPrint history job detail JSON response into a HistoryJob object.
    /// </summary>
    private static HistoryJob? ParseOctoPrintHistoryJob(string jobJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(jobJson);
            JsonElement root = doc.RootElement;

            // Check if the response indicates success
            if (!root.TryGetProperty("success", out JsonElement successProp) || !successProp.GetBoolean())
            {
                return null;
            }

            // The actual job data is in the root (not nested under "result")
            return ParseOctoPrintJobElement(root);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses OctoPrint /api/printer response into OctoPrintPrinterState.
    /// </summary>
    private static OctoPrintPrinterState? ParsePrinterState(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            var state = new OctoPrintPrinterState();

            if (root.TryGetProperty("state", out JsonElement stateObj))
            {
                if (stateObj.TryGetProperty("text", out JsonElement textProp))
                {
                    state.State = textProp.GetString() ?? string.Empty;
                }
                if (stateObj.TryGetProperty("flags", out JsonElement flagsProp))
                {
                    if (flagsProp.TryGetProperty("operational", out JsonElement opProp))
                    {
                        state.Operational = opProp.GetBoolean();
                    }
                    if (flagsProp.TryGetProperty("printing", out JsonElement printProp))
                    {
                        state.Printing = printProp.GetBoolean();
                    }
                }
            }

            if (root.TryGetProperty("temperature", out JsonElement tempObj))
            {
                state.Temperatures = new Dictionary<string, object>();
                foreach (var prop in tempObj.EnumerateObject())
                {
                    state.Temperatures[prop.Name] = prop.Value.GetRawText();
                }
            }

            return state;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses OctoPrint /api/job response into OctoPrintJobStatus.
    /// </summary>
    private static OctoPrintJobStatus? ParseJobStatus(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            var status = new OctoPrintJobStatus();

            if (root.TryGetProperty("job", out JsonElement jobObj))
            {
                if (jobObj.TryGetProperty("file", out JsonElement fileObj) && fileObj.TryGetProperty("name", out JsonElement nameProp))
                {
                    status.Filename = nameProp.GetString();
                }
                if (jobObj.TryGetProperty("filament", out JsonElement filamentObj))
                {
                    status.Filament = new Dictionary<string, object>();
                    foreach (var prop in filamentObj.EnumerateObject())
                    {
                        status.Filament[prop.Name] = prop.Value.GetRawText();
                    }
                }
            }

            if (root.TryGetProperty("progress", out JsonElement progressObj))
            {
                if (progressObj.TryGetProperty("completion", out JsonElement completionProp) && completionProp.ValueKind != JsonValueKind.Null)
                {
                    status.Progress = completionProp.GetDouble(); // OctoPrint sends 0-100, keep as-is
                }
                if (progressObj.TryGetProperty("printTime", out JsonElement printTimeProp) && printTimeProp.ValueKind != JsonValueKind.Null)
                {
                    status.PrintTime = printTimeProp.GetDouble();
                }
                if (progressObj.TryGetProperty("printTimeLeft", out JsonElement leftProp) && leftProp.ValueKind != JsonValueKind.Null)
                {
                    status.PrintTimeLeft = leftProp.GetDouble();
                }
            }

            return status;
        }
        catch
        {
            return null;
        }
    }

    // ========== CAPABILITY INTERFACE IMPLEMENTATIONS ==========

    async Task<byte[]?> ISupportsFileDownload.DownloadFileAsync(string baseUrl, string filePath, CancellationToken ct = default)
    {
        try
        {
            var url = $"{baseUrl.TrimEnd('/')}/api/files/local/{Uri.EscapeDataString(filePath)}";
            var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch
        {
            return null;
        }
    }

    async Task<List<PrinterFileInfo>> ISupportsFileList.GetFileListAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        var files = await GetFileListAsync(baseUrl, apiKey ?? "");
        return files?.Select(f => new PrinterFileInfo { Name = Path.GetFileName(f), Path = f }).ToList() ?? new();
    }

    async Task<bool> ISupportsFileUpload.UploadGcodeAsync(string baseUrl, string fileName, Stream fileContent, string? apiKey = null, CancellationToken ct = default)
    {
        // Convert Stream to byte array for the existing UploadFileAsync method
        if (fileContent is MemoryStream ms)
        {
            return await UploadFileAsync(baseUrl, apiKey ?? "", ms.ToArray(), fileName, null, false);
        }

        using var memoryStream = new MemoryStream();
        await fileContent.CopyToAsync(memoryStream, ct);
        return await UploadFileAsync(baseUrl, apiKey ?? "", memoryStream.ToArray(), fileName, null, false);
    }

    async Task<string?> ISupportsCamera.GetCameraStreamUrlAsync(string baseUrl, int? frontendPort = null, string? apiKey = null, CancellationToken ct = default)
    {
        return await GetCameraStreamUrlAsync(baseUrl, apiKey ?? "");
    }

    async Task<string?> ISupportsCamera.GetCameraSnapshotUrlAsync(string baseUrl, int? frontendPort = null, string? apiKey = null, CancellationToken ct = default)
    {
        try
        {
            // OctoPrint snapshot is typically /webcam/?action=snapshot
            return await Task.FromResult($"{baseUrl.TrimEnd('/')}/webcam/?action=snapshot");
        }
        catch
        {
            return null;
        }
    }

    async Task<StandardPrinterInfo> ISupportsPrinterInformation.GetPrinterInformationAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        try
        {
            var state = await GetPrinterStateAsync(baseUrl, apiKey ?? "");
            return new StandardPrinterInfo
            {
                Name = "OctoPrint Printer",
                Firmware = "OctoPrint",
                Model = "Unknown"
            };
        }
        catch
        {
            return new StandardPrinterInfo { Name = "Unknown", Firmware = "Unknown", Model = "Unknown" };
        }
    }

    /// <summary>
    /// ISupportsHistory implementations - get and manage print history.
    /// </summary>
    async Task<HistoryListResponse?> ISupportsHistory.GetHistoryListAsync(string baseUrl, int? limit = null, int? start = null, string? apiKey = null, CancellationToken ct = default)
        => await GetHistoryListAsync(baseUrl, apiKey ?? "", limit, start);

    async Task<HistoryJob?> ISupportsHistory.GetHistoryJobAsync(string baseUrl, string jobId, string? apiKey = null, CancellationToken ct = default)
        => await GetHistoryJobAsync(baseUrl, apiKey ?? "", jobId);

    async Task<HistoryTotals?> ISupportsHistory.GetHistoryTotalsAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default)
        => await GetHistoryTotalsAsync(baseUrl, apiKey ?? "");

    async Task<bool> ISupportsHistory.DeleteHistoryJobAsync(string baseUrl, string jobId, string? apiKey = null, CancellationToken ct = default)
        => await DeleteHistoryJobAsync(baseUrl, apiKey ?? "", jobId);

    /// <summary>
    /// ISupportsTemperatureControl implementation - set temperatures.
    /// </summary>
    async Task<bool> ISupportsTemperatureControl.SetTemperaturesAsync(string baseUrl, double? hotendTemp = null, double? bedTemp = null, string? apiKey = null, CancellationToken ct = default)
        => await SetTemperaturesAsync(baseUrl, apiKey ?? "", hotendTemp, bedTemp);
}

#pragma warning restore CS1066


