import { describe, expect, it } from 'vitest';
import type { CustomProfile, OrcaMachineProfile } from '@/services/slicerProfilesService';
import { resolveMachineProfile } from '@/features/slicer/utils/resolveMachineProfile';

const system: OrcaMachineProfile = {
  name: 'Base',
  manufacturer: 'Qidi',
  inheritanceResolved: true,
  settings: { model_id: 'Q1', default_bed_type: '3', inherited: 'base' },
};

function custom(id: string, name: string, settings: Record<string, unknown>): CustomProfile {
  return { id, name, profileType: 'machine', isSystem: false, createdAt: '', rawJson: JSON.stringify(settings) };
}

describe('resolveMachineProfile', () => {
  it('overlays a multi-level custom inheritance chain on the resolved worker preset', () => {
    const parent = custom('parent', 'Parent', { inherits: 'Base', default_bed_type: '4', layer: 'parent' });
    const child = custom('child', 'Child', { inherits: 'Parent', layer: 'child' });
    const selected: OrcaMachineProfile = { name: 'Child', manufacturer: '' };
    expect(resolveMachineProfile(selected, 'child', [parent, child], [system])).toEqual({
      manufacturer: 'Qidi',
      settings: { model_id: 'Q1', default_bed_type: '4', inherited: 'base', inherits: 'Parent', layer: 'child' },
    });
  });

  it('resolves system clones using their source name', () => {
    const clone = custom('clone', 'Clone', { from: 'system', name: 'Base', default_bed_type: '4' });
    expect(resolveMachineProfile({ name: 'Clone', manufacturer: '' }, 'clone', [clone], [system])?.settings.default_bed_type)
      .toBe('4');
  });

  it('refuses unresolved, ambiguous, missing and cyclic chains', () => {
    const child = custom('child', 'Child', { inherits: 'Parent' });
    const parent = custom('parent', 'Parent', { inherits: 'Child' });
    const selected: OrcaMachineProfile = { name: 'Child', manufacturer: '' };
    expect(resolveMachineProfile(selected, 'child', [child, parent], [system])).toBeUndefined();
    expect(resolveMachineProfile(selected, 'child', [child], [system])).toBeUndefined();
    expect(resolveMachineProfile(selected, 'child', [child, parent, { ...parent, id: 'duplicate' }], [system]))
      .toBeUndefined();
    expect(resolveMachineProfile(system, undefined, [], [{ ...system, inheritanceResolved: false }]))
      .toBeUndefined();
  });
});
