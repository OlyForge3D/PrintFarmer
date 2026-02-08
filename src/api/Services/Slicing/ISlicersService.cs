using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Contracts.Slicing;
using Farm.Infrastructure.Domain;

namespace Farm.Web.Api.Services.Slicing
{
    public interface ISlicersService
    {
        Task<IReadOnlyList<SlicerService>> ListAsync(CancellationToken ct);

        Task<(Guid Id, string ApiKey)> RegisterAsync(RegisterSlicerDto dto, CancellationToken ct);

        Task<SlicerService?> GetAsync(Guid id, CancellationToken ct);

        Task<bool> HeartbeatAsync(Guid id, HeartbeatDto dto, CancellationToken ct);

        Task<bool> DeregisterAsync(Guid id, CancellationToken ct);

        Task<string?> RotateApiKeyAsync(Guid id, CancellationToken ct, bool isAdminForced = false);

        /// <summary>
        /// Import slicer profiles for a specific printer model on-demand.
        /// Called when a printer is added with a model that doesn't have profiles yet.
        /// Uses pull-based approach: only imports profiles when needed for active printers.
        /// </summary>
        /// <param name="printerModelId">The catalog PrinterModel ID to import profiles for.</param>
        /// <param name="printerModelName">The model name (for logging and alias resolution).</param>
        /// <param name="manufacturerName">The manufacturer name (for profile filtering).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Number of profiles imported, or 0 if no worker available or profiles already exist.</returns>
        Task<int> ImportProfilesForModelAsync(Guid printerModelId, string printerModelName, string manufacturerName, CancellationToken ct);
    }
}
