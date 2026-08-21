import { describe, expect, it } from 'vitest';
import { HARDENED_BY_MATERIAL, isHardenedByMaterial } from '../nozzleHardness';
import { NozzleType } from '@/types/api';

/**
 * HARDENED_BY_MATERIAL duplicates NozzleModelDefinition.IsHardenedByMaterial
 * (src/infra/Domain/ComponentModels.cs) so the nozzle form can explain what "Auto"
 * resolves to without a round-trip. The backend stays authoritative — these tests exist
 * so the copy cannot drift silently when a material is added on either side.
 */
describe('nozzleHardness', () => {
  it('matches IsHardenedByMaterial in infra/Domain/ComponentModels.cs', () => {
    expect([...HARDENED_BY_MATERIAL].sort()).toEqual([
      'Abrasive',
      'Diamond',
      'HardenedSteel',
      'Ruby',
      'ToolSteel',
      'TungstenCarbide',
    ]);
  });

  it('classifies every NozzleType member, so a new material cannot be missed', () => {
    const soft = Object.values(NozzleType).filter((m) => !isHardenedByMaterial(m));

    expect(soft.sort()).toEqual(['Brass', 'PlatedCopper', 'StainlessSteel', 'Unknown']);
  });

  it('only contains real NozzleType members', () => {
    const members = new Set<string>(Object.values(NozzleType));
    for (const material of HARDENED_BY_MATERIAL) {
      expect(members.has(material), `${material} is not a NozzleType`).toBe(true);
    }
  });

  it('treats Diamond as hardened so a Diamondback nozzle accepts abrasive filament', () => {
    expect(isHardenedByMaterial(NozzleType.Diamond)).toBe(true);
    expect(isHardenedByMaterial(NozzleType.Brass)).toBe(false);
  });
});
