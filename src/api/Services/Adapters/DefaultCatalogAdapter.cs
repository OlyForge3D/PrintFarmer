using System;
using System.Threading.Tasks;
using Farm.Importing.Services.Adapters;
using Farm.Web.Api.Services;

namespace Farm.Web.Api.Services.Adapters;

public class DefaultCatalogAdapter : IDefaultCatalogAdapter
{
    private readonly IDefaultCatalogService _inner;
    public DefaultCatalogAdapter(IDefaultCatalogService inner) => _inner = inner;
    public async Task<(Guid ManufacturerId, Guid ModelId)> GetDefaultCatalogIdsAsync()
    {
        var tup = await _inner.GetDefaultCatalogIdsAsync();
        return tup;
    }
}
