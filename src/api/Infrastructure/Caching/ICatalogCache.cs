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
    Task<(IReadOnlyList<ManufacturerDto> List, string Etag)> GetManufacturersAsync(CancellationToken ct);

    Task<(IReadOnlyList<PrinterModelDto> List, string Etag)> GetModelsAsync(Guid? manufacturerId, CancellationToken ct);

    void InvalidateManufacturers();

    void InvalidateModels(Guid? manufacturerId = null);
}
