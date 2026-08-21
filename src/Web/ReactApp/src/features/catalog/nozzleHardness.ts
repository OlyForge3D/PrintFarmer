import { NozzleType } from '@/types/api';

/**
 * Materials the backend treats as hardened when a nozzle model's `hardnessOverride`
 * is `Auto`.
 *
 * Mirrors `NozzleModelDefinition.IsHardenedByMaterial` in
 * `src/infra/Domain/ComponentModels.cs`. The backend remains authoritative — this copy
 * exists only so the nozzle form can explain what `Auto` resolves to without a
 * round-trip. `__tests__/nozzleHardness.test.ts` pins it so the two cannot drift silently.
 */
export const HARDENED_BY_MATERIAL: ReadonlySet<string> = new Set<string>([
  NozzleType.HardenedSteel,
  NozzleType.TungstenCarbide,
  NozzleType.Abrasive,
  NozzleType.Diamond,
  NozzleType.Ruby,
  NozzleType.ToolSteel,
]);

/**
 * Whether a material implies a hardened nozzle, i.e. what `Auto` resolves to.
 *
 * @param nozzleType - Backend `NozzleType` enum name.
 * @returns True when the material is abrasion-resistant.
 */
export function isHardenedByMaterial(nozzleType: string): boolean {
  return HARDENED_BY_MATERIAL.has(nozzleType);
}
