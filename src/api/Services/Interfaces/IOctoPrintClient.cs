using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

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
        /// Sends an arbitrary HttpRequestMessage using the underlying HttpClient.
        /// This exposes plugin and non-standard endpoints without requiring callers to
        /// reference a concrete implementation.
        /// </summary>
        Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default);
        // Add more OctoPrint API methods as needed
    }
}
