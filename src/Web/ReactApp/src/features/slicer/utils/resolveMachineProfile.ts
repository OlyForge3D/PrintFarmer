import type { CustomProfile, OrcaMachineProfile } from '@/services/slicerProfilesService';

export interface ResolvedMachineProfile {
  settings: Record<string, unknown>;
  manufacturer: string;
}

function customSettings(profile: CustomProfile): Record<string, unknown> | undefined {
  if (!profile.rawJson) return undefined;
  try {
    const parsed: unknown = JSON.parse(profile.rawJson);
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return undefined;
    const raw = parsed as Record<string, unknown>;
    const nested = raw.settings;
    return nested && typeof nested === 'object' && !Array.isArray(nested)
      ? { ...raw, ...nested as Record<string, unknown> } : raw;
  } catch (error) {
    if (!(error instanceof SyntaxError)) throw error;
    return undefined;
  }
}

/** Custom layers must terminate at one fully resolved worker preset. */
export function resolveMachineProfile(
  selected: OrcaMachineProfile | undefined,
  customId: string | undefined,
  customProfiles: CustomProfile[],
  systemProfiles: OrcaMachineProfile[],
): ResolvedMachineProfile | undefined {
  if (!selected) return undefined;
  if (!customId && systemProfiles.includes(selected)) {
    return selected.inheritanceResolved === true && selected.settings
      ? { settings: selected.settings, manufacturer: selected.manufacturer } : undefined;
  }
  const start = customProfiles.filter(profile => profile.profileType === 'machine'
    && (customId ? profile.id === customId : profile.name === selected.name));
  if (start.length !== 1) return undefined;
  const seen = new Set<string>();
  const layers: Record<string, unknown>[] = [];
  let current = start[0];
  while (current) {
    if (seen.has(current.id)) return undefined;
    seen.add(current.id);
    const settings = customSettings(current);
    if (!settings) return undefined;
    layers.push(settings);
    const parent = settings.from === 'system' ? settings.name : settings.inherits;
    if (typeof parent !== 'string' || !parent) return undefined;
    const system = systemProfiles.filter(profile => profile.name === parent);
    const custom = settings.from === 'system' ? [] : customProfiles.filter(profile =>
      profile.profileType === 'machine' && profile.name === parent);
    if (system.length + custom.length !== 1) return undefined;
    if (system.length === 1) {
      const base = system[0];
      if (base.inheritanceResolved !== true || !base.settings) return undefined;
      return {
        settings: Object.assign({}, base.settings, ...layers.reverse()),
        manufacturer: base.manufacturer,
      };
    }
    current = custom[0];
  }
  return undefined;
}
