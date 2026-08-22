/**
 * Shared helper for rendering a printer's manufacturer/model subtitle.
 *
 * The catalog stores unidentified printers against a real "Unknown"
 * manufacturer linked to a real "Unknown Model" model (see
 * `EfCatalogRepository.GetUnknownModelId`), so `manufacturerName` and
 * `modelName` legitimately arrive from the API as the literal strings
 * "Unknown" and "Unknown Model". Naively concatenating those two fields
 * (`${manufacturerName} ${modelName}`) therefore renders the confusing
 * "Unknown Unknown Model" instead of a single coherent fallback.
 */

const UNKNOWN_MANUFACTURER_SENTINELS = new Set(['unknown']);
const UNKNOWN_MODEL_SENTINELS = new Set(['unknown', 'unknown model']);
const UNKNOWN_MODEL_LABEL = 'Unknown model';

function isUnknownManufacturer(manufacturerName: string | null | undefined): boolean {
  const normalized = manufacturerName?.trim().toLowerCase() ?? '';
  return !normalized || UNKNOWN_MANUFACTURER_SENTINELS.has(normalized);
}

function isUnknownModel(modelName: string | null | undefined): boolean {
  const normalized = modelName?.trim().toLowerCase() ?? '';
  return !normalized || UNKNOWN_MODEL_SENTINELS.has(normalized);
}

/**
 * Compute the label to show for a printer's manufacturer + model, collapsing
 * the catalog's "Unknown" manufacturer / "Unknown Model" model pair (and any
 * missing metadata) into a single coherent fallback instead of duplicating it.
 *
 * @param manufacturerName - Manufacturer name from the printer/catalog, if known
 * @param modelName - Model name from the printer/catalog, if known
 * @returns A single display-ready label, never a duplicated "Unknown Unknown Model" string
 */
export function formatPrinterModelSubtitle(
  manufacturerName: string | null | undefined,
  modelName: string | null | undefined,
): string {
  const manufacturerUnknown = isUnknownManufacturer(manufacturerName);
  const modelUnknown = isUnknownModel(modelName);

  if (manufacturerUnknown && modelUnknown) {
    return UNKNOWN_MODEL_LABEL;
  }

  if (manufacturerUnknown) {
    return modelName!.trim();
  }

  if (modelUnknown) {
    return manufacturerName!.trim();
  }

  return `${manufacturerName!.trim()} ${modelName!.trim()}`;
}
