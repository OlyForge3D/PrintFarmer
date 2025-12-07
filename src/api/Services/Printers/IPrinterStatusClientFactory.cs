using System;
using System.Collections.Generic;
using Farm.Infrastructure;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.Interfaces;

namespace Farm.Web.Api.Services.Printers
{
    /// <summary>
    /// Factory for creating printer status clients based on backend type.
    /// This service manages the creation and registry of all backend-specific status clients.
    /// </summary>
    public interface IPrinterStatusClientFactory
    {
        /// <summary>
        /// Gets the status client for a specific printer backend type.
        /// </summary>
        /// <param name="backend">The printer backend type</param>
        /// <returns>The corresponding status client implementation</returns>
        /// <exception cref="ArgumentException">Thrown if backend type is not supported</exception>
        IPrinterStatusClient GetStatusClient(PrinterBackend backend);

        /// <summary>
        /// Gets the status client for a specific backend integer value.
        /// </summary>
        /// <param name="backendValue">The integer value of the printer backend</param>
        /// <returns>The corresponding status client implementation</returns>
        IPrinterStatusClient GetStatusClient(int backendValue);

        /// <summary>
        /// Checks if a backend is supported by a registered status client.
        /// </summary>
        /// <param name="backend">The printer backend type</param>
        /// <returns>True if supported, false otherwise</returns>
        bool IsBackendSupported(PrinterBackend backend);
    }

    /// <summary>
    /// Default implementation of IPrinterStatusClientFactory.
    /// Manages backend-specific printer status clients.
    /// </summary>
    public class PrinterStatusClientFactory : IPrinterStatusClientFactory
    {
        private readonly Dictionary<PrinterBackend, IPrinterStatusClient> _clients;
        private readonly IUnifiedLoggingService _logger;

        public PrinterStatusClientFactory(
            IMoonrakerClient moonrakerClient,
            IPrusaLinkClient prusaLinkClient,
            ISdcpClient sdcpClient,
            IOctoPrintClient octoPrintClient,
            ICircuitBreakerService circuitBreaker,
            IUnifiedLoggingService logger)
        {
            if (moonrakerClient == null) throw new ArgumentNullException(nameof(moonrakerClient));
            if (prusaLinkClient == null) throw new ArgumentNullException(nameof(prusaLinkClient));
            if (sdcpClient == null) throw new ArgumentNullException(nameof(sdcpClient));
            if (octoPrintClient == null) throw new ArgumentNullException(nameof(octoPrintClient));
            if (circuitBreaker == null) throw new ArgumentNullException(nameof(circuitBreaker));

            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // Initialize status clients for each backend
            _clients = new Dictionary<PrinterBackend, IPrinterStatusClient>
            {
                { PrinterBackend.Moonraker, new MoonrakerStatusClient(moonrakerClient, circuitBreaker, logger) },
                { PrinterBackend.PrusaLink, new PrusaLinkStatusClient(prusaLinkClient, circuitBreaker, logger) },
                { PrinterBackend.SDCP, new SdcpStatusClient(sdcpClient, circuitBreaker, logger) },
                { PrinterBackend.OctoPrint, new OctoPrintStatusClient(octoPrintClient, circuitBreaker, logger) }
            };
        }

        public IPrinterStatusClient GetStatusClient(PrinterBackend backend)
        {
            if (_clients.TryGetValue(backend, out var client))
            {
                return client;
            }

            throw new ArgumentException($"Unsupported printer backend: {backend}", nameof(backend));
        }

        public IPrinterStatusClient GetStatusClient(int backendValue)
        {
            PrinterBackend backend = (PrinterBackend)backendValue;
            return GetStatusClient(backend);
        }

        public bool IsBackendSupported(PrinterBackend backend)
        {
            return _clients.ContainsKey(backend);
        }
    }
}
