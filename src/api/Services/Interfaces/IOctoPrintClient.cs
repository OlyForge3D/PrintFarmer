using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;

namespace Farm.Web.Api.Services.Interfaces
{
    public interface IOctoPrintClient
    {
        Task<bool> TestConnectionAsync(string baseUrl, string apiKey);
        Task<string> GetPrinterStateAsync(string baseUrl, string apiKey);
        Task<string> GetJobStatusAsync(string baseUrl, string apiKey);
        Task<bool> StartJobAsync(string baseUrl, string apiKey, string fileName);
        Task<bool> CancelJobAsync(string baseUrl, string apiKey);
        Task<string> GetCameraStreamUrlAsync(string baseUrl, string apiKey);

        /// <summary>
        /// Creates a PrinterDto from OctoPrint printer entity and status information.
        /// Encapsulates OctoPrint-specific DTO creation logic.
        /// </summary>
        /// <param name="printer">The printer database entity</param>
        /// <param name="printerStateJson">JSON response from OctoPrint /api/printer endpoint</param>
        /// <param name="jobStatusJson">JSON response from OctoPrint /api/job endpoint</param>
        /// <param name="apiKey">API key for camera URL generation and plugin checks</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>A fully constructed PrinterDto with OctoPrint-specific data</returns>
        Task<PrinterDto> CreatePrinterDtoAsync(Printer printer, string printerStateJson, string jobStatusJson, string apiKey, CancellationToken ct = default);

        /// <summary>
        /// Sends an arbitrary HttpRequestMessage using the underlying HttpClient.
        /// This exposes plugin and non-standard endpoints without requiring callers to
        /// reference a concrete implementation.
        /// </summary>
        Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default);
        // Add more OctoPrint API methods as needed
    }
}
