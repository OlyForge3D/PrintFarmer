using System;
using System.Collections.Generic;
using Farm.Infrastructure;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.Interfaces;

namespace Farm.Web.Api.Services.Printers
{
    /// <summary>
    /// Default implementation of IBackendClientFactory.
    /// Manages all backend-specific client implementations and provides
    /// unified access to them based on printer backend type.
    /// </summary>
    public class BackendClientFactory : IBackendClientFactory
    {
        private readonly Dictionary<PrinterBackend, IBackendClient> _clients;
        private readonly IUnifiedLoggingService _logger;

        public BackendClientFactory(
            IMoonrakerClient moonrakerClient,
            IPrusaLinkClient prusaLinkClient,
            ISdcpClient sdcpClient,
            IOctoPrintClient octoPrintClient,
            IUnifiedLoggingService logger)
        {
            if (moonrakerClient == null) throw new ArgumentNullException(nameof(moonrakerClient));
            if (prusaLinkClient == null) throw new ArgumentNullException(nameof(prusaLinkClient));
            if (sdcpClient == null) throw new ArgumentNullException(nameof(sdcpClient));
            if (octoPrintClient == null) throw new ArgumentNullException(nameof(octoPrintClient));

            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // Store all backend clients in a registry
            // Each backend client implements IBackendClient, enabling type-safe access
            _clients = new Dictionary<PrinterBackend, IBackendClient>
            {
                { PrinterBackend.Moonraker, moonrakerClient },
                { PrinterBackend.PrusaLink, prusaLinkClient },
                { PrinterBackend.SDCP, sdcpClient },
                { PrinterBackend.OctoPrint, octoPrintClient }
            };

            _logger.LogInformation("BackendClientFactory initialized with 4 backend clients (Moonraker, PrusaLink, SDCP, OctoPrint)");
        }

        public IBackendClient GetClient(PrinterBackend backend)
        {
            if (!_clients.TryGetValue(backend, out var client))
            {
                throw new ArgumentException($"Unsupported printer backend: {backend}", nameof(backend));
            }

            return client ?? throw new InvalidOperationException($"Client for {backend} is null");
        }

        public IBackendClient GetClient(int backendValue)
        {
            PrinterBackend backend = (PrinterBackend)backendValue;
            return GetClient(backend);
        }

        public bool IsBackendSupported(PrinterBackend backend)
        {
            return _clients.ContainsKey(backend);
        }
    }
}
