using System;
using System.Threading.Tasks;

namespace Farm.Importing.Services.Adapters;

public interface IDefaultCatalogAdapter
{
    Task<(Guid ManufacturerId, Guid ModelId)> GetDefaultCatalogIdsAsync();
}
