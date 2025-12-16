using System;
using System.Collections.Generic;
using Farm.Infrastructure;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Telemetry;

namespace Farm.Web.Api.Services.Printers
{
    /// <summary>
    /// Receives printer status updates and stores them in the cache.
    /// This service is called by backend polling services to update the shared status cache.
    /// </summary>
    public interface IPrinterStatusUpdateReceiver
    {
        /// <summary>
        /// Called when a printer status update is received from a backend service.
        /// </summary>
        void ReceiveStatusUpdate(PrinterStatusDto status);

        /// <summary>
        /// Called when multiple printer statuses are updated at once.
        /// </summary>
        void ReceiveStatusUpdates(IEnumerable<PrinterStatusDto> statuses);
    }

    /// <summary>
    /// Receives printer status updates from backend services and stores them in the shared cache.
    /// Allows backend plugins to update the cache without direct dependencies on API services.
    /// </summary>
    public class PrinterStatusUpdateReceiver : IPrinterStatusUpdateReceiver
    {
        private readonly IPrinterStatusCacheWriter _cache;
        private readonly IUnifiedLoggingService _logger;

        public PrinterStatusUpdateReceiver(IPrinterStatusCacheWriter cache, IUnifiedLoggingService logger)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void ReceiveStatusUpdate(PrinterStatusDto status)
        {
            if (status == null)
                return;

            _cache.UpdateStatus(status);
            _logger.LogDebug($"[StatusCache] Updated printer {status.Id}: IsOnline={status.IsOnline}, State={status.State}");
        }

        public void ReceiveStatusUpdates(IEnumerable<PrinterStatusDto> statuses)
        {
            if (statuses == null)
                return;

            foreach (var status in statuses)
            {
                _cache.UpdateStatus(status);
            }
            _logger.LogDebug($"[StatusCache] Updated multiple printer statuses");
        }
    }
}
