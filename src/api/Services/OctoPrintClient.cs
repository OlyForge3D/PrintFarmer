using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Services.Interfaces;

namespace Farm.Web.Api.Services;

public class OctoPrintClient(HttpClient httpClient) : IOctoPrintClient
{
    private readonly HttpClient _httpClient = httpClient;
    // Keep HttpClient internal; callers should use IOctoPrintClient.SendAsync
    internal HttpClient HttpClient => _httpClient;

    public async Task<bool> TestConnectionAsync(string baseUrl, string apiKey)
    {
        HttpRequestMessage request = new(HttpMethod.Get, $"{baseUrl}/api/version");
        request.Headers.Add("X-Api-Key", apiKey);
        HttpResponseMessage response = await _httpClient.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    public async Task<string> GetPrinterStateAsync(string baseUrl, string apiKey)
    {
        HttpRequestMessage request = new(HttpMethod.Get, $"{baseUrl}/api/printer");
        request.Headers.Add("X-Api-Key", apiKey);
        HttpResponseMessage response = await _httpClient.SendAsync(request);
        return await response.Content.ReadAsStringAsync();
    }

    public async Task<string> GetJobStatusAsync(string baseUrl, string apiKey)
    {
        HttpRequestMessage request = new(HttpMethod.Get, $"{baseUrl}/api/job");
        request.Headers.Add("X-Api-Key", apiKey);
        HttpResponseMessage response = await _httpClient.SendAsync(request);
        return await response.Content.ReadAsStringAsync();
    }

    public async Task<bool> StartJobAsync(string baseUrl, string apiKey, string fileName)
    {
        HttpRequestMessage request = new(HttpMethod.Post, $"{baseUrl}/api/job");
        request.Headers.Add("X-Api-Key", apiKey);
        request.Content = new StringContent($"{{\"command\":\"select\",\"print\":true,\"file\":\"{fileName}\"}}", Encoding.UTF8, "application/json");
        HttpResponseMessage response = await _httpClient.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> CancelJobAsync(string baseUrl, string apiKey)
    {
        HttpRequestMessage request = new(HttpMethod.Post, $"{baseUrl}/api/job");
        request.Headers.Add("X-Api-Key", apiKey);
        request.Content = new StringContent("{\"command\":\"cancel\"}", Encoding.UTF8, "application/json");
        HttpResponseMessage response = await _httpClient.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    public Task<string> GetCameraStreamUrlAsync(string baseUrl, string apiKey)
    {
        // OctoPrint camera stream is typically a static URL, not an API call
        // This can be constructed from the baseUrl or stored in the printer config
        return Task.FromResult($"{baseUrl}/webcam/?action=stream");
    }

    public async Task<PrinterDto> CreatePrinterDtoAsync(Printer printer, string printerStateJson, string jobStatusJson, string apiKey, CancellationToken ct = default)
    {
        // Check for position and spool manager plugins
        bool hasPositionPlugin = false;
        try
        {
            HttpRequestMessage pluginsRequest = new(HttpMethod.Get, $"{printer.ServerUrl.TrimEnd('/')}/api/plugins");
            pluginsRequest.Headers.Add("X-Api-Key", apiKey);
            HttpResponseMessage pluginsResponse = await _httpClient.SendAsync(pluginsRequest, ct);
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
            ServerUrl: printer.ServerUrl,
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
            IpAddress: printer.IpAddress
        );
    }

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        return _httpClient.SendAsync(request, cancellationToken);
    }
}

