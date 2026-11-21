/**
 * Scoping helpers for user-imported custom slicer profiles.
 *
 * Custom machine and process profiles carry an authoritative catalog
 * PrinterModel association (`printerModelId`) that the backend resolves from the
 * raw profile JSON at import time. When present, that association is the single
 * source of truth for which printer a profile belongs to — a profile imported
 * for a Qidi Plus 4 must never be offered for a RatRig.
 *
 * Older profiles imported before the association existed have no
 * `printerModelId`. Those fall back to fuzzy matching so they remain usable.
 */

import type { CustomProfile, OrcaMachineProfile } from '@/services/slicerProfilesService';

export type ProfileScope = 'match' | 'mismatch' | 'unscoped';

export interface ScopeableCustomProfile {
  /** Catalog PrinterModel association (machine/process only). Null for legacy/unscoped. */
  printerModelId?: string | null;
}

/**
 * Classify a custom profile against the selected printer's catalog model id.
 *
 * - `match`    — profile is explicitly scoped to the selected printer's model.
 * - `mismatch` — profile is explicitly scoped to a DIFFERENT model (hide it).
 * - `unscoped` — profile has no explicit association; caller may apply a
 *                legacy/fuzzy fallback to decide visibility.
 */
export function classifyCustomProfileScope(
  profile: ScopeableCustomProfile,
  selectedPrinterModelId: string | null | undefined,
): ProfileScope {
  const hasModelId = profile.printerModelId != null && profile.printerModelId !== '';
  if (!hasModelId) {
    return 'unscoped';
  }
  if (!selectedPrinterModelId) {
    // The profile insists on a specific model but we don't know the selected
    // printer's model yet — withhold it rather than leak it onto every printer.
    return 'mismatch';
  }
  return profile.printerModelId === selectedPrinterModelId ? 'match' : 'mismatch';
}

/**
 * Fuzzy fallback used for legacy machine profiles that predate `printerModelId`.
 * Matches the profile's embedded `printer_model` (from rawJson) or its name
 * against the selected printer's manufacturer/model.
 */
export function legacyMachineProfileMatchesPrinter(
  profile: { name: string; rawJson?: string },
  manufacturerName: string | undefined,
  modelName: string | undefined,
): boolean {
  const mfr = manufacturerName?.toLowerCase() ?? '';
  const model = modelName?.toLowerCase() ?? '';

  // No printer context — cannot meaningfully scope, so allow.
  if (!mfr && !model) {
    return true;
  }

  const modelWords = model.split(/[\s\-_]+/).filter((w) => w.length > 2);

  if (profile.rawJson) {
    try {
      const parsed = JSON.parse(profile.rawJson) as Record<string, unknown>;
      const printerModel = (parsed.printer_model as string)?.toLowerCase();
      if (printerModel) {
        if (modelWords.some((w) => printerModel.includes(w))) return true;
        if (printerModel.split(/[\s\-_]+/).some((w) => w.length > 2 && model.includes(w))) return true;
        return false;
      }
    } catch {
      /* fall through to name matching */
    }
  }

  const nameLower = profile.name.toLowerCase();
  const mfrWords = mfr.split(/[\s\-_]+/).filter((w) => w.length > 2);
  const matchesModel = modelWords.length > 0 && modelWords.some((w) => nameLower.includes(w));
  const matchesMfr = mfrWords.length > 0 && mfrWords.some((w) => nameLower.includes(w));
  return matchesModel || (matchesMfr && modelWords.length === 0);
}

/**
 * Fuzzy fallback used for legacy process profiles that predate `printerModelId`.
 * Matches the profile's `compatible_printers` (from rawJson) against the
 * selected machine profile name.
 */
export function legacyProcessProfileMatchesMachine(
  profile: { rawJson?: string },
  selectedMachineProfileId: string,
): boolean {
  if (!selectedMachineProfileId || !profile.rawJson) {
    return false;
  }

  try {
    const parsed = JSON.parse(profile.rawJson) as Record<string, unknown>;
    const compatible = parsed.compatible_printers as string[] | undefined;
    if (compatible && compatible.length > 0) {
      return compatible.some((c) => c === selectedMachineProfileId);
    }
  } catch {
    /* hide profile if it can't be parsed */
  }
  return false;
}

function machineParent(profile: CustomProfile): string | undefined {
  if (!profile.rawJson) return undefined;
  try {
    const parsed: unknown = JSON.parse(profile.rawJson);
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return undefined;
    const raw = parsed as Record<string, unknown>;
    const nested = raw.settings;
    const settings = nested && typeof nested === 'object' && !Array.isArray(nested)
      ? { ...raw, ...nested as Record<string, unknown> } : raw;
    const parent = settings.from === 'system' ? settings.name : settings.inherits;
    return typeof parent === 'string' && parent ? parent : undefined;
  } catch (error) {
    if (!(error instanceof SyntaxError)) throw error;
    console.warn(`Cannot read machine profile inheritance for "${profile.name}".`);
    return undefined;
  }
}

/**
 * Collect related machine names within the selected printer model. A custom
 * chain can terminate at a system preset; system siblings share their direct
 * parent, but generic ancestors beyond that parent must not join families.
 */
export function filamentFamilyMachineNames(
  selected: OrcaMachineProfile,
  selectedCustomId: string | undefined,
  systemProfiles: readonly OrcaMachineProfile[],
  customProfiles: readonly CustomProfile[],
): string[] {
  const systems = new Map(systemProfiles.map(profile => [profile.name, profile]));
  const customs = customProfiles.filter(profile => profile.profileType === 'machine');

  function lineage(name: string, customId?: string): Set<string> {
    const names = new Set<string>();
    let current: string | undefined = name;
    let first = true;
    while (current && !names.has(current)) {
      names.add(current);
      const candidates = customs.filter(profile => first && customId
        ? profile.id === customId : profile.name === current);
      if (first && customId) {
        current = candidates.length === 1 ? machineParent(candidates[0]) : undefined;
      } else {
        const system = systems.get(current);
        if (system) {
          if (system.inherits) names.add(system.inherits);
          break;
        }
        current = candidates.length === 1 ? machineParent(candidates[0]) : undefined;
      }
      first = false;
    }
    return names;
  }

  const selectedLineage = lineage(selected.name, selectedCustomId);
  const result = new Set(selectedLineage);
  for (const profile of [...systemProfiles, ...customs]) {
    const candidateLineage = lineage(profile.name, 'profileType' in profile ? profile.id : undefined);
    if ([...candidateLineage].some(name => selectedLineage.has(name))) {
      candidateLineage.forEach(name => result.add(name));
    }
  }
  return [...result];
}

export function customFilamentMatchesMachine(
  profile: Pick<CustomProfile, 'name' | 'rawJson' | 'compatiblePrinters'>,
  machineNames: readonly string[],
): boolean {
  let compatible = profile.compatiblePrinters;
  if (compatible === undefined) {
    if (!profile.rawJson) return false;
    try {
      const parsed: unknown = JSON.parse(profile.rawJson);
      if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return false;
      const raw = parsed as Record<string, unknown>;
      const nested = raw.settings;
      const settings = nested && typeof nested === 'object' && !Array.isArray(nested)
        ? { ...raw, ...nested as Record<string, unknown> } : raw;
      const value = settings.compatible_printers;
      if (value !== undefined && (!Array.isArray(value) || !value.every(item => typeof item === 'string'))) {
        console.warn(`Invalid compatible printers for filament profile "${profile.name}".`);
        return false;
      }
      compatible = value as string[] | undefined;
    } catch (error) {
      if (!(error instanceof SyntaxError)) throw error;
      console.warn(`Cannot read compatibility for filament profile "${profile.name}".`);
      return false;
    }
  }
  return !compatible?.length || compatible.some(name => machineNames.includes(name));
}
