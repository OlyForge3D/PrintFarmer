using System;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Tests.TestInfrastructure;

public static class TestHelpers
{
    /// <summary>
    /// Return the seeded Unknown manufacturer and Unknown Model IDs if present.
    /// If not present, returns Guid.Empty for that item.
    /// </summary>
    public static async Task<(Guid ManufacturerId, Guid ModelId)> GetUnknownCatalogIdsAsync(AppDbContext db)
    {
        if (db == null)
        {
            throw new ArgumentNullException(nameof(db));
        }

        var unknownManufacturer = await db.Manufacturers.FirstOrDefaultAsync(m => m.Name == "Unknown");
        Guid manufacturerId = unknownManufacturer != null ? unknownManufacturer.Id : Guid.Empty;

        Guid modelId = Guid.Empty;
        if (manufacturerId != Guid.Empty)
        {
            var unknownModel = await db.Models.FirstOrDefaultAsync(m => m.Name == "Unknown Model" && m.ManufacturerId == manufacturerId);
            if (unknownModel != null)
            {
                modelId = unknownModel.Id;
            }
        }

        return (manufacturerId, modelId);
    }
}
