/**
 * Unit tests for the Simple-mode ↔ OrcaProcessSettings mapping utilities.
 * Covers bed-adhesion fan-out, support_type preservation, and default fallbacks.
 */
import { describe, it, expect } from 'vitest';
import {
  convertOrcaProcessProfileToSettings,
  orcaToSimpleSettings,
  resolveAdhesionOverrides,
  simpleToOrcaSettings,
} from '../simpleSlicerMappings';
import type { OrcaProcessProfile } from '@/services/slicerProfilesService';
import type { OrcaProcessSettings } from '@/features/slicer/components/settings/slicerSettingsTypes';

const EMPTY_ORCA: OrcaProcessSettings = {} as OrcaProcessSettings;

describe('orcaToSimpleSettings — defaults', () => {
  it('returns sensible defaults when OrcaProcessSettings is empty', () => {
    const result = orcaToSimpleSettings(EMPTY_ORCA);
    expect(result.layerHeight).toBe(0.2);
    expect(result.infillPercent).toBe(15);
    expect(result.infillPattern).toBe('grid');
    expect(result.topShellLayers).toBe(4);
    expect(result.bottomShellLayers).toBe(3);
    expect(result.wallLoops).toBe(2);
    expect(result.supportEnabled).toBe(false);
    expect(result.supportType).toBe('normal(auto)');
    expect(result.bedAdhesionType).toBe('profile');
    expect(result.bedAdhesionProfileLabel).toBe('Profile-controlled (inherited value unknown)');
  });

  it('normalizes loaded support types to the auto variants used by Simple mode', () => {
    expect(orcaToSimpleSettings({ ...EMPTY_ORCA, support_type: 'normal(manual)' }).supportType).toBe('normal(auto)');
    expect(orcaToSimpleSettings({ ...EMPTY_ORCA, support_type: 'tree(manual)' }).supportType).toBe('tree(auto)');
    expect(orcaToSimpleSettings({ ...EMPTY_ORCA, support_type: 'tree(auto)' }).supportType).toBe('tree(auto)');
  });

  it('maps numeric orca fields directly', () => {
    const orca: OrcaProcessSettings = {
      ...EMPTY_ORCA,
      layer_height: 0.3,
      sparse_infill_density: 40,
      sparse_infill_pattern: 'gyroid',
      top_shell_layers: 6,
      bottom_shell_layers: 5,
      wall_loops: 4,
    };
    const result = orcaToSimpleSettings(orca);
    expect(result.layerHeight).toBe(0.3);
    expect(result.infillPercent).toBe(40);
    expect(result.infillPattern).toBe('gyroid');
    expect(result.topShellLayers).toBe(6);
    expect(result.bottomShellLayers).toBe(5);
    expect(result.wallLoops).toBe(4);
  });
});

describe('orcaToSimpleSettings — bed adhesion read', () => {
  const known = (raft: number, skirt: number, brim: string): OrcaProcessSettings =>
    ({ ...EMPTY_ORCA, raft_layers: raft, skirt_loops: skirt, brim_type: brim });

  it('returns none when every adhesion feature is explicitly off', () => {
    expect(orcaToSimpleSettings(known(0, 0, 'no_brim')).bedAdhesionType).toBe('none');
  });

  it('returns brim for any non-no_brim brim_type value when raft and skirt are off', () => {
    for (const bt of ['outer_only', 'inner_only', 'outer_and_inner', 'auto_brim', 'brim_ears', 'painted']) {
      expect(orcaToSimpleSettings(known(0, 0, bt)).bedAdhesionType).toBe('brim');
    }
  });

  it('returns skirt when only skirt_loops > 0', () => {
    expect(orcaToSimpleSettings(known(0, 2, 'no_brim')).bedAdhesionType).toBe('skirt');
  });

  it('returns raft when only raft_layers > 0', () => {
    expect(orcaToSimpleSettings(known(3, 0, 'no_brim')).bedAdhesionType).toBe('raft');
  });

  it.each([
    [known(3, 1, 'no_brim'), 'Profile: raft + skirt'],
    [known(3, 0, 'outer_only'), 'Profile: raft + brim'],
    [known(0, 1, 'outer_only'), 'Profile: skirt + brim'],
    [known(2, 1, 'auto_brim'), 'Profile: raft + skirt + brim'],
  ])('reports a combination as profile-controlled instead of picking one (%#)', (orca, label) => {
    const result = orcaToSimpleSettings(orca);
    expect(result.bedAdhesionType).toBe('profile');
    expect(result.bedAdhesionProfileLabel).toBe(label);
  });

  it.each([
    [{ ...EMPTY_ORCA, brim_type: 'no_brim' }],
    [{ ...EMPTY_ORCA, raft_layers: 0, skirt_loops: 0 }],
    [{ ...EMPTY_ORCA, raft_layers: 0, brim_type: 'outer_only' }],
    [{ ...EMPTY_ORCA, raft_layers: 0, skirt_loops: 0, brim_type: '' }],
  ])('reports an unresolved adhesion key as unknown, never as None (%#)', (orca) => {
    const result = orcaToSimpleSettings(orca as OrcaProcessSettings);
    expect(result.bedAdhesionType).toBe('profile');
    expect(result.bedAdhesionProfileLabel).toBe('Profile-controlled (inherited value unknown)');
  });

  it('does not set a profile label for a single choice', () => {
    expect(orcaToSimpleSettings(known(0, 0, 'no_brim')).bedAdhesionProfileLabel).toBeUndefined();
  });
});

describe('simpleToOrcaSettings — profile-controlled adhesion', () => {
  const base = { wallLoops: 2, topShellLayers: 4, bottomShellLayers: 3, infillPercent: 15, infillPattern: 'grid', supportEnabled: false, supportType: 'normal(auto)', layerHeight: 0.2 };

  it('keeps the profile adhesion keys, including absent ones, while the state is profile', () => {
    const prev: OrcaProcessSettings = { ...EMPTY_ORCA, skirt_loops: 1, brim_type: 'outer_only' };
    const result = simpleToOrcaSettings({ ...base, infillPercent: 30, bedAdhesionType: 'profile' }, prev);
    expect(result.skirt_loops).toBe(1);
    expect(result.brim_type).toBe('outer_only');
    expect(result.raft_layers).toBeUndefined();
    expect(result.sparse_infill_density).toBe(30);
  });
});

describe('simpleToOrcaSettings — bed adhesion write', () => {
  const base = { wallLoops: 2, topShellLayers: 4, bottomShellLayers: 3, infillPercent: 15, infillPattern: 'grid', supportEnabled: false, supportType: 'normal(auto)', layerHeight: 0.2 };

  it('writes no_brim + 0 skirt_loops + 0 raft_layers for none', () => {
    const result = simpleToOrcaSettings({ ...base, bedAdhesionType: 'none' }, EMPTY_ORCA);
    expect(result.brim_type).toBe('no_brim');
    expect(result.skirt_loops).toBe(0);
    expect(result.raft_layers).toBe(0);
  });

  it('writes outer_only + 0 skirt_loops + 0 raft_layers for brim', () => {
    const result = simpleToOrcaSettings({ ...base, bedAdhesionType: 'brim' }, EMPTY_ORCA);
    expect(result.brim_type).toBe('outer_only');
    expect(result.skirt_loops).toBe(0);
    expect(result.raft_layers).toBe(0);
  });

  it('writes no_brim + 1 skirt_loop + 0 raft_layers for skirt', () => {
    const result = simpleToOrcaSettings({ ...base, bedAdhesionType: 'skirt' }, EMPTY_ORCA);
    expect(result.brim_type).toBe('no_brim');
    expect(result.skirt_loops).toBe(1);
    expect(result.raft_layers).toBe(0);
  });

  it('writes no_brim + 0 skirt_loops + 3 raft_layers for raft', () => {
    const result = simpleToOrcaSettings({ ...base, bedAdhesionType: 'raft' }, EMPTY_ORCA);
    expect(result.brim_type).toBe('no_brim');
    expect(result.skirt_loops).toBe(0);
    expect(result.raft_layers).toBe(3);
  });

  it('clears previous raft when switching to brim', () => {
    const prev: OrcaProcessSettings = { ...EMPTY_ORCA, raft_layers: 3, skirt_loops: 0, brim_type: 'no_brim' };
    const result = simpleToOrcaSettings({ ...base, bedAdhesionType: 'brim' }, prev);
    expect(result.brim_type).toBe('outer_only');
    expect(result.raft_layers).toBe(0);
  });

  it('clears previous brim when switching to skirt', () => {
    const prev: OrcaProcessSettings = { ...EMPTY_ORCA, brim_type: 'outer_only', skirt_loops: 0, raft_layers: 0 };
    const result = simpleToOrcaSettings({ ...base, bedAdhesionType: 'skirt' }, prev);
    expect(result.brim_type).toBe('no_brim');
    expect(result.skirt_loops).toBe(1);
    expect(result.raft_layers).toBe(0);
  });
});

describe('simpleToOrcaSettings — support_type preservation', () => {
  const base = { wallLoops: 2, topShellLayers: 4, bottomShellLayers: 3, infillPercent: 15, infillPattern: 'grid', bedAdhesionType: 'none' as const, layerHeight: 0.2 };

  it('writes support_type when supports are enabled', () => {
    const result = simpleToOrcaSettings(
      { ...base, supportEnabled: true, supportType: 'tree(auto)' },
      EMPTY_ORCA
    );
    expect(result.support_type).toBe('tree(auto)');
    expect(result.enable_support).toBe(true);
  });

  it('preserves previous support_type when supports are disabled', () => {
    const prev: OrcaProcessSettings = { ...EMPTY_ORCA, support_type: 'tree(manual)' };
    const result = simpleToOrcaSettings(
      { ...base, supportEnabled: false, supportType: 'normal(auto)' },
      prev
    );
    expect(result.enable_support).toBe(false);
    expect(result.support_type).toBe('tree(manual)'); // prev value preserved
  });
});

describe('round-trip: orcaToSimpleSettings → simpleToOrcaSettings', () => {
  it('round-trips brim correctly', () => {
    const orca: OrcaProcessSettings = { ...EMPTY_ORCA, brim_type: 'outer_only', skirt_loops: 0, raft_layers: 0 };
    const simple = orcaToSimpleSettings(orca);
    expect(simple.bedAdhesionType).toBe('brim');
    const back = simpleToOrcaSettings(simple, orca);
    expect(back.brim_type).toBe('outer_only');
    expect(back.skirt_loops).toBe(0);
    expect(back.raft_layers).toBe(0);
  });

  it('round-trips raft correctly', () => {
    const orca: OrcaProcessSettings = { ...EMPTY_ORCA, raft_layers: 3, brim_type: 'no_brim', skirt_loops: 0 };
    const simple = orcaToSimpleSettings(orca);
    expect(simple.bedAdhesionType).toBe('raft');
    const back = simpleToOrcaSettings(simple, orca);
    expect(back.raft_layers).toBe(3);
    expect(back.brim_type).toBe('no_brim');
    expect(back.skirt_loops).toBe(0);
  });

  it('round-trips skirt correctly (lossy: N loops → 1 loop after round-trip)', () => {
    // Simple mode always writes skirt_loops: 1; reading any skirt_loops > 0 → 'skirt'
    const orca: OrcaProcessSettings = { ...EMPTY_ORCA, skirt_loops: 2, brim_type: 'no_brim', raft_layers: 0 };
    const simple = orcaToSimpleSettings(orca);
    expect(simple.bedAdhesionType).toBe('skirt');
    const back = simpleToOrcaSettings(simple, orca);
    expect(back.skirt_loops).toBe(1); // intentional: Simple mode normalises to 1 loop
    expect(back.brim_type).toBe('no_brim');
    expect(back.raft_layers).toBe(0);
  });

  it('round-trips none correctly', () => {
    const orca: OrcaProcessSettings = { ...EMPTY_ORCA, brim_type: 'no_brim', skirt_loops: 0, raft_layers: 0 };
    const simple = orcaToSimpleSettings(orca);
    expect(simple.bedAdhesionType).toBe('none');
    const back = simpleToOrcaSettings(simple, orca);
    expect(back.brim_type).toBe('no_brim');
    expect(back.skirt_loops).toBe(0);
    expect(back.raft_layers).toBe(0);
  });
});

describe('convertOrcaProcessProfileToSettings', () => {
  const profile = (overrides: Partial<OrcaProcessProfile>): OrcaProcessProfile =>
    ({ name: 'Draft', layerHeight: 0.2, infillPercentage: 20, printSpeed: 60, supports: false, ...overrides }) as OrcaProcessProfile;

  it('shows the raw profile infill ("15%"), not the server-derived fallback, so Simple mode matches what is sliced', () => {
    const settings = convertOrcaProcessProfileToSettings(
      profile({ infillPercentage: 20, settings: { sparse_infill_density: '15%' } }),
    );

    expect(settings.sparse_infill_density).toBe(15);
    expect(orcaToSimpleSettings(settings).infillPercent).toBe(15);
  });

  it('falls back to the promoted infillPercentage when the raw settings omit the key', () => {
    const settings = convertOrcaProcessProfileToSettings(profile({ infillPercentage: 25, settings: {} }));

    expect(settings.sparse_infill_density).toBe(25);
  });

  it('resolves an inherited skirt_loops = 0 so a brim profile is a single brim and is sent as such', () => {
    const settings = convertOrcaProcessProfileToSettings(
      profile({ settings: { skirt_loops: '0', brim_type: 'outer_only', raft_layers: '0' } }),
    );

    expect(orcaToSimpleSettings(settings).bedAdhesionType).toBe('brim');
    expect(resolveAdhesionOverrides(settings)).toEqual({ brim_type: 'outer_only', skirt_loops: 0, raft_layers: 0 });
  });

  it('does not use the metadata skirt_loops default as evidence of a skirt + brim combination', () => {
    const settings = convertOrcaProcessProfileToSettings(
      profile({ settings: { brim_type: 'outer_only', raft_layers: '0' } }),
    );

    expect(settings.skirt_loops).toBeUndefined();
    const simple = orcaToSimpleSettings(settings);
    expect(simple.bedAdhesionType).toBe('profile');
    expect(simple.bedAdhesionProfileLabel).toBe('Profile-controlled (inherited value unknown)');
    expect(resolveAdhesionOverrides(settings)).toEqual({});
  });

  it('reports a profile that truly declares skirt + brim as that combination and forces nothing', () => {
    const settings = convertOrcaProcessProfileToSettings(
      profile({ settings: { skirt_loops: '2', brim_type: 'outer_only', raft_layers: '0' } }),
    );

    expect(orcaToSimpleSettings(settings).bedAdhesionProfileLabel).toBe('Profile: skirt + brim');
    expect(resolveAdhesionOverrides(settings)).toEqual({});
  });

  it('sends explicit no_brim for a profile that explicitly declares no adhesion', () => {
    const settings = convertOrcaProcessProfileToSettings(
      profile({ settings: { skirt_loops: '0', brim_type: 'no_brim', raft_layers: '0' } }),
    );

    expect(orcaToSimpleSettings(settings).bedAdhesionType).toBe('none');
    expect(resolveAdhesionOverrides(settings)).toEqual({ brim_type: 'no_brim', skirt_loops: 0, raft_layers: 0 });
  });

  it('keeps an unknown profile adhesion through unrelated Simple edits', () => {
    const settings = convertOrcaProcessProfileToSettings(profile({ settings: { brim_type: 'outer_only' } }));
    const edited = simpleToOrcaSettings({ ...orcaToSimpleSettings(settings), wallLoops: 5 }, settings);

    expect(edited.wall_loops).toBe(5);
    expect(resolveAdhesionOverrides(edited)).toEqual({});
  });

  it.each([
    ['none', { brim_type: 'no_brim', skirt_loops: 0, raft_layers: 0 }],
    ['brim', { brim_type: 'outer_only', skirt_loops: 0, raft_layers: 0 }],
    ['skirt', { brim_type: 'no_brim', skirt_loops: 1, raft_layers: 0 }],
    ['raft', { brim_type: 'no_brim', skirt_loops: 0, raft_layers: 3 }],
  ] as const)('sends all three keys once the user replaces profile adhesion with %s', (choice, expected) => {
    const settings = convertOrcaProcessProfileToSettings(
      profile({ settings: { skirt_loops: '1', brim_type: 'outer_only', raft_layers: '0' } }),
    );
    const edited = simpleToOrcaSettings({ ...orcaToSimpleSettings(settings), bedAdhesionType: choice }, settings);

    expect(resolveAdhesionOverrides(edited)).toEqual(expected);
  });
});
