using System.Security.Cryptography;
using System.Text;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Infrastructure.Caching;

/// <summary>
/// Abstraction for retrieving and invalidating cached catalog (manufacturer/model) data with ETag support.
/// </summary>
public interface ICatalogCache
{
    /// <summary>Gets cached manufacturers with ETag for conditional requests.</summary>
    Task<(IReadOnlyList<ManufacturerDto> List, string Etag)> GetManufacturersAsync(CancellationToken ct);

    /// <summary>Gets cached printer models with ETag, optionally filtered by manufacturer.</summary>
    Task<(IReadOnlyList<PrinterModelDto> List, string Etag)> GetModelsAsync(Guid? manufacturerId, CancellationToken ct);

    /// <summary>Invalidates the manufacturer cache.</summary>
    void InvalidateManufacturers();

    /// <summary>Invalidates the models cache, optionally for a specific manufacturer.</summary>
    void InvalidateModels(Guid? manufacturerId = null);
}
