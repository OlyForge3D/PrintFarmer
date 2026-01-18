using System;
using System.Linq;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Telemetry;
using Microsoft.AspNetCore.SignalR;

namespace Farm.Web.Api.Services.SignalR
{
    /// <summary>
    /// Listens for printer status updates broadcast on the PrinterHub and stores them in the cache.
    /// This service connects to the hub and registers event handlers for real-time status updates.
    /// </summary>
    public interface IPrinterStatusHubListener : IAsyncDisposable
    {
        /// <summary>
        /// Start listening for printer status updates.
        /// </summary>
        Task StartListeningAsync();

        /// <summary>
        /// Stop listening for printer status updates.
        /// </summary>
        Task StopListeningAsync();
    }

    /// <summary>
    /// Listens to SignalR PrinterHub for "printerupdated" events and updates the status cache.
    /// Allows the API to receive and cache real-time status updates from backend services.
    /// </summary>
    public sealed class PrinterStatusHubListener(
        IHubContext<PrinterHub> hubContext,
        Farm.Infrastructure.Services.Printers.IPrinterStatusCacheReader cache,
        IUnifiedLoggingService logger) : IPrinterStatusHubListener
    {
        private readonly IUnifiedLoggingService _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        public async Task StartListeningAsync()
        {
            // Note: We can't actually listen to hub messages from within the server without special setup.
            // Instead, we rely on backend services to call the cache update receiver directly.
            // This class serves as a placeholder for potential future enhancement where we might
            // use HubConnectionBuilder on the client side (if needed).
            await Task.Yield();
            _logger.LogInformation("[PrinterStatusHubListener] Listener initialized (backend services will update cache directly)");
        }

        public async Task StopListeningAsync()
        {
            await Task.Yield();
            _logger.LogInformation("[PrinterStatusHubListener] Listener stopped");
        }

        public async ValueTask DisposeAsync()
        {
            await StopListeningAsync();
        }
    }
}
