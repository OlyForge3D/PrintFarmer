using System.Net.Http.Json;
using Farm.Web.Shared;

namespace Farm.Web.Client.Services;

public class PrinterService
{
    private readonly HttpClient _http;

    public PrinterService(HttpClient http)
    {
        _http = http;
    }

    public async Task<List<PrinterBasicDto>> GetPrintersAsync()
    {
        try
        {
            // Use the existing API endpoint that returns basic printer info
            var response = await _http.GetAsync("api/printers/basic");
            response.EnsureSuccessStatusCode();
            
            var printers = await response.Content.ReadFromJsonAsync<List<PrinterBasicDto>>();
            return printers ?? new List<PrinterBasicDto>();
        }
        catch (Exception ex)
        {
            // Log error and return empty list for now
            Console.WriteLine($"Error loading printers: {ex.Message}");
            return new List<PrinterBasicDto>();
        }
    }

    public async Task<PrinterDetailsDto?> GetPrinterDetailsAsync(Guid printerId)
    {
        try
        {
            var response = await _http.GetAsync($"api/printers/{printerId}");
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<PrinterDetailsDto>();
            }
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading printer details: {ex.Message}");
            return null;
        }
    }
}
