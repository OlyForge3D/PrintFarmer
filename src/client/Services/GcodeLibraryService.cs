using Farm.Web.Shared;
using System.Net.Http.Json;

namespace Farm.Web.Client.Services;

public class GcodeLibraryService
{
    private readonly HttpClient _httpClient;
    
    public GcodeLibraryService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }
    
    public async Task<List<GcodeFileDto>> GetFilesAsync()
    {
        try
        {
            var files = await _httpClient.GetFromJsonAsync<List<GcodeFileDto>>("api/library");
            return files ?? new List<GcodeFileDto>();
        }
        catch (Exception)
        {
            return new List<GcodeFileDto>();
        }
    }
    
    public async Task<GcodeFileDto?> GetFileAsync(Guid id)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<GcodeFileDto>($"api/library/{id}");
        }
        catch (Exception)
        {
            return null;
        }
    }
    
    public async Task<GcodeFileDto?> CreateFileAsync(CreateGcodeFileDto file)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("api/library", file);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<GcodeFileDto>();
            }
        }
        catch (Exception)
        {
            // Handle error
        }
        return null;
    }
    
    public async Task<bool> UpdateFileAsync(Guid id, UpdateGcodeFileDto file)
    {
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"api/library/{id}", file);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }
    
    public async Task<bool> DeleteFileAsync(Guid id)
    {
        try
        {
            var response = await _httpClient.DeleteAsync($"api/library/{id}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }
    
    public async Task<GcodeHarvestResultDto?> StartHarvestAsync(StartGcodeHarvestDto request)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("api/gcode-harvest/start", request);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<GcodeHarvestResultDto>();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error starting harvest: {ex.Message}");
        }
        return null;
    }

    public async Task<GcodeHarvestOperationDto?> GetHarvestOperationAsync(Guid operationId)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<GcodeHarvestOperationDto>($"api/gcode-harvest/operations/{operationId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting harvest operation: {ex.Message}");
            return null;
        }
    }

    public async Task<List<DiscoveredGcodeFileDto>> GetDiscoveredFilesAsync(Guid operationId)
    {
        try
        {
            var files = await _httpClient.GetFromJsonAsync<List<DiscoveredGcodeFileDto>>($"api/gcode-harvest/operations/{operationId}/files");
            return files ?? new List<DiscoveredGcodeFileDto>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting discovered files: {ex.Message}");
            return new List<DiscoveredGcodeFileDto>();
        }
    }

    public async Task<bool> ImportSelectedFilesAsync(ImportSelectedGcodeFilesDto request)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("api/gcode-harvest/import", request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error importing files: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> CancelHarvestAsync(Guid operationId)
    {
        try
        {
            var response = await _httpClient.PostAsync($"api/gcode-harvest/operations/{operationId}/cancel", null);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error cancelling harvest: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> HarvestFileAsync(Guid printerId, string filename)
    {
        try
        {
            var request = new StartGcodeHarvestDto 
            { 
                PrinterId = printerId,
                IncludeSubdirectories = true,
                MaxFileSizeBytes = 100 * 1024 * 1024 // 100MB
            };
            var response = await _httpClient.PostAsJsonAsync("api/gcode-harvest/start", request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }
    
    public async Task<List<string>> GetPrinterFilesAsync(Guid printerId)
    {
        try
        {
            var files = await _httpClient.GetFromJsonAsync<List<string>>($"api/gcode-harvest/printer/{printerId}");
            return files ?? new List<string>();
        }
        catch (Exception)
        {
            return new List<string>();
        }
    }

    public async Task<List<GcodeHarvestOperationDto>> GetActiveHarvestsAsync()
    {
        try
        {
            var operations = await _httpClient.GetFromJsonAsync<List<GcodeHarvestOperationDto>>("api/gcode-harvest/active");
            return operations ?? new List<GcodeHarvestOperationDto>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting active harvests: {ex.Message}");
            return new List<GcodeHarvestOperationDto>();
        }
    }
}
