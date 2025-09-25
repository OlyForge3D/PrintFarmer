using System.Net.Http;
using System.Threading.Tasks;
using Farm.Web.Api.Services.Interfaces;

namespace Farm.Web.Api.Services;

public class OctoPrintClient(HttpClient httpClient) : IOctoPrintClient
{
    private readonly HttpClient _httpClient = httpClient;
    // Expose HttpClient for plugin integration (internal use only)
    internal HttpClient HttpClient => _httpClient;

    public async Task<bool> TestConnectionAsync(string baseUrl, string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/version");
        request.Headers.Add("X-Api-Key", apiKey);
        var response = await _httpClient.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    public async Task<string> GetPrinterStateAsync(string baseUrl, string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/printer");
        request.Headers.Add("X-Api-Key", apiKey);
        var response = await _httpClient.SendAsync(request);
        return await response.Content.ReadAsStringAsync();
    }

    public async Task<string> GetJobStatusAsync(string baseUrl, string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/job");
        request.Headers.Add("X-Api-Key", apiKey);
        var response = await _httpClient.SendAsync(request);
        return await response.Content.ReadAsStringAsync();
    }

    public async Task<bool> StartJobAsync(string baseUrl, string apiKey, string fileName)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/job");
        request.Headers.Add("X-Api-Key", apiKey);
        request.Content = new StringContent($"{{\"command\":\"select\",\"print\":true,\"file\":\"{fileName}\"}}", System.Text.Encoding.UTF8, "application/json");
        var response = await _httpClient.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> CancelJobAsync(string baseUrl, string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/job");
        request.Headers.Add("X-Api-Key", apiKey);
        request.Content = new StringContent("{\"command\":\"cancel\"}", System.Text.Encoding.UTF8, "application/json");
        var response = await _httpClient.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    public Task<string> GetCameraStreamUrlAsync(string baseUrl, string apiKey)
    {
        // OctoPrint camera stream is typically a static URL, not an API call
        // This can be constructed from the baseUrl or stored in the printer config
        return Task.FromResult($"{baseUrl}/webcam/?action=stream");
    }
}

