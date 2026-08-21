using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Catalog;

namespace Farm.Slicer.Module.Api.Services;

/// <summary>
/// Builds the set of OrcaSlicer worker hierarchy model-group keys (<c>printer_model</c> values) that
/// are eligible for seeding into the database for a given printer catalog.
/// </summary>
/// <remarks>
/// <para>
/// The OrcaSlicer worker groups its <c>ByHierarchy</c> structure by each machine profile's
/// <c>printer_model</c> field. High-flow (HF) machine variants declare their own distinct
/// <c>printer_model</c> — for example <c>"Prusa CORE One HF 0.4 nozzle"</c> declares
/// <c>printer_model: "Prusa CORE One HF"</c>, which is never equal to the base catalog model's
/// <c>Name</c> (<c>"Prusa CORE One"</c>). It exists only as a configured OrcaSlicer <em>alias</em>
/// of that catalog model. Matching hierarchy groups against base catalog names alone therefore
/// silently skips every alias-only group, dropping all of its machine, filament, and process
/// profiles at seed time (issue #1779).
/// </para>
/// <para>
/// This helper is deliberately shared by every seeding path so they cannot drift apart again:
/// the alias handling was previously fixed in the admin-triggered seeds only, while the
/// registration-triggered seed that actually populates a deployment kept matching on base names
/// and continued to drop the HF profiles.
/// </para>
/// </remarks>
internal static class OrcaSlicerCatalogModelNames
{
    /// <summary>
    /// Returns the catalog model names plus every configured OrcaSlicer alias for those models,
    /// compared case-insensitively.
    /// </summary>
    /// <param name="catalogService">Catalog service used to resolve per-model slicer aliases.</param>
    /// <param name="catalogModels">The printer models present in the catalog.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<HashSet<string>> BuildAsync(
        ICatalogService catalogService,
        IReadOnlyList<PrinterModelDto> catalogModels,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalogService);
        ArgumentNullException.ThrowIfNull(catalogModels);

        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);

        foreach (PrinterModelDto model in catalogModels)
        {
            if (!string.IsNullOrWhiteSpace(model.Name))
            {
                names.Add(model.Name.Trim());
            }

            IEnumerable<SlicerModelAliasDto> aliases = await catalogService.GetModelAliasesAsync(model.Id, ct);
            foreach (SlicerModelAliasDto alias in aliases)
            {
                if (string.Equals(alias.SlicerType, "OrcaSlicer", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(alias.SlicerModelName))
                {
                    names.Add(alias.SlicerModelName.Trim());
                }
            }
        }

        return names;
    }
}
