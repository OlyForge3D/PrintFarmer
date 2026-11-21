import { describe, expect, it, vi } from 'vitest';
import type { CustomProfile, OrcaMachineProfile } from '@/services/slicerProfilesService';
import { customFilamentMatchesMachine, filamentFamilyMachineNames } from '../customProfileScoping';

const system: OrcaMachineProfile[] = [
  { name: 'Qidi 0.4', manufacturer: 'Qidi', inherits: 'Qidi base' },
  { name: 'Qidi 0.6', manufacturer: 'Qidi', inherits: 'Qidi base' },
  { name: 'Other', manufacturer: 'Other', inherits: 'Other base' },
];

function machine(id: string, name: string, parent: string): CustomProfile {
  return {
    id, name, profileType: 'machine', isSystem: false, createdAt: '',
    rawJson: JSON.stringify({ inherits: parent }),
  };
}

const customs = [
  machine('a', 'Custom A', 'Qidi 0.4'),
  machine('b', 'Custom B', 'Qidi 0.6'),
  machine('child', 'Custom child', 'Custom A'),
  machine('other', 'Custom other', 'Other'),
];

describe('custom filament printer-family scoping', () => {
  it.each([
    ['system', system[0], undefined],
    ['custom', { name: 'Custom A', manufacturer: '' }, 'a'],
    ['nested custom', { name: 'Custom child', manufacturer: '' }, 'child'],
  ])('matches siblings and ancestors for a %s machine, not another family', (_, selected, id) => {
    const names = filamentFamilyMachineNames(selected, id, system, customs);
    const match = (printer: string) => customFilamentMatchesMachine({
      name: 'Filament', compatiblePrinters: [printer],
    }, names);
    expect(match('Qidi 0.4')).toBe(true);
    expect(match('Qidi 0.6')).toBe(true);
    expect(match('Custom A')).toBe(true);
    expect(match('Custom B')).toBe(true);
    expect(match('Custom child')).toBe(true);
    expect(match('Other')).toBe(false);
    expect(match('Custom other')).toBe(false);
  });

  it('shows unscoped profiles and uses the stored association rather than stale raw JSON', () => {
    const names = ['Qidi 0.4'];
    expect(customFilamentMatchesMachine({ name: 'Unscoped', compatiblePrinters: null }, names)).toBe(true);
    expect(customFilamentMatchesMachine({ name: 'Empty', compatiblePrinters: [] }, names)).toBe(true);
    expect(customFilamentMatchesMachine({
      name: 'Cleared', compatiblePrinters: null, rawJson: JSON.stringify({ compatible_printers: ['Other'] }),
    }, names)).toBe(true);
    expect(customFilamentMatchesMachine({
      name: 'Stored', compatiblePrinters: ['Other'], rawJson: JSON.stringify({ compatible_printers: ['Qidi 0.4'] }),
    }, names)).toBe(false);
  });

  it('supports legacy raw JSON and does not treat invalid data as unscoped', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    try {
      expect(customFilamentMatchesMachine({ name: 'Legacy', rawJson: '{}' }, ['Qidi 0.4'])).toBe(true);
      expect(customFilamentMatchesMachine({
        name: 'Nested', rawJson: JSON.stringify({ settings: { compatible_printers: ['Qidi 0.4'] } }),
      }, ['Qidi 0.4'])).toBe(true);
      expect(customFilamentMatchesMachine({ name: 'Missing' }, ['Qidi 0.4'])).toBe(false);
      expect(customFilamentMatchesMachine({ name: 'Malformed', rawJson: '{' }, ['Qidi 0.4'])).toBe(false);
      expect(customFilamentMatchesMachine({
        name: 'Invalid', rawJson: JSON.stringify({ compatible_printers: 'Qidi 0.4' }),
      }, ['Qidi 0.4'])).toBe(false);
      expect(warn).toHaveBeenCalledTimes(2);
    } finally {
      warn.mockRestore();
    }
  });
});
