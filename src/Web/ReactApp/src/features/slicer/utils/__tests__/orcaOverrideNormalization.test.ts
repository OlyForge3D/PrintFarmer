import { describe, expect, it } from 'vitest';
import type { OrcaProcessSettings } from '@/features/slicer/components/settings/slicerSettingsTypes';
import type { SlicerSettings } from '@/features/slicer/components/job/SlicerSettingsPanel';
import { orcaToSimpleSettings, simpleToOrcaSettings } from '@/features/slicer/pages/simpleSlicerMappings';
import {
  buildProcessOverrides,
  normalizeOrcaOverrides,
} from '@/features/slicer/utils/orcaOverrideNormalization';

// A profile that explicitly declares no adhesion (genuinely None).
const baseline = {
  layer_height: 0.2,
  sparse_infill_density: 15,
  sparse_infill_pattern: 'grid',
  top_shell_layers: 4,
  bottom_shell_layers: 3,
  wall_loops: 2,
  enable_support: false,
  support_type: 'normal(auto)',
  skirt_loops: 0,
  raft_layers: 0,
  brim_type: 'no_brim',
} as unknown as OrcaProcessSettings;

function build(
  current: OrcaProcessSettings,
  opts: { simpleMode?: boolean; advanced?: Record<string, unknown> } = {},
) {
  return buildProcessOverrides({
    slicerSettings: current,
    originalProcessSettings: baseline as unknown as Record<string, unknown>,
    advancedProcessSettings: opts.advanced ?? {},
    simpleMode: opts.simpleMode ?? true,
    engineVersion: undefined,
  });
}

function editSimple(patch: Partial<SlicerSettings>): OrcaProcessSettings {
  const simple = { ...orcaToSimpleSettings(baseline), ...patch };
  return simpleToOrcaSettings(simple, baseline);
}

describe('buildProcessOverrides (Simple mode)', () => {
  it('sends explicit no-brim adhesion for an untouched job whose profile declares no adhesion', () => {
    expect(orcaToSimpleSettings(baseline).bedAdhesionType).toBe('none');
    expect(build(baseline)).toEqual({ brim_type: 'no_brim', skirt_loops: '0', raft_layers: '0' });
  });

  it.each([
    ['brim', { brim_type: 'outer_only', skirt_loops: '0', raft_layers: '0' }],
    ['skirt', { brim_type: 'no_brim', skirt_loops: '1', raft_layers: '0' }],
    ['raft', { brim_type: 'no_brim', skirt_loops: '0', raft_layers: '3' }],
    ['none', { brim_type: 'no_brim', skirt_loops: '0', raft_layers: '0' }],
  ] as const)('maps adhesion %s to Orca string keys', (bedAdhesionType, expected) => {
    expect(build(editSimple({ bedAdhesionType }))).toMatchObject(expected);
  });

  it('keeps a profile brim style when the profile displays as brim', () => {
    const profileWithBrim = { ...baseline, brim_type: 'auto_brim' } as OrcaProcessSettings;
    expect(build(profileWithBrim)).toMatchObject({ brim_type: 'auto_brim', skirt_loops: '0', raft_layers: '0' });
  });

  it('serialises edited Simple fields as Orca strings', () => {
    const overrides = build(
      editSimple({
        wallLoops: 3,
        topShellLayers: 5,
        bottomShellLayers: 4,
        infillPercent: 25,
        infillPattern: 'gyroid',
        supportEnabled: true,
        supportType: 'tree(auto)',
      }),
    );
    expect(overrides).toEqual({
      wall_loops: '3',
      top_shell_layers: '5',
      bottom_shell_layers: '4',
      sparse_infill_density: '25%',
      sparse_infill_pattern: 'gyroid',
      enable_support: '1',
      support_type: 'tree(auto)',
      brim_type: 'no_brim',
      skirt_loops: '0',
      raft_layers: '0',
    });
  });

  it('disabling supports sends "0"', () => {
    const withSupport = { ...baseline, enable_support: true } as OrcaProcessSettings;
    const overrides = buildProcessOverrides({
      slicerSettings: { ...withSupport, enable_support: false } as OrcaProcessSettings,
      originalProcessSettings: withSupport as unknown as Record<string, unknown>,
      advancedProcessSettings: {},
      simpleMode: true,
      engineVersion: undefined,
    });
    expect(overrides.enable_support).toBe('0');
  });
});

describe('buildProcessOverrides (Simple mode, compound skirt + brim profile)', () => {
  const compound = { ...baseline, skirt_loops: 2, brim_type: 'outer_only' } as OrcaProcessSettings;
  const buildFrom = (current: OrcaProcessSettings) =>
    buildProcessOverrides({
      slicerSettings: current,
      originalProcessSettings: compound as unknown as Record<string, unknown>,
      advancedProcessSettings: {},
      simpleMode: true,
      engineVersion: undefined,
    });
  const edit = (patch: Partial<SlicerSettings>) =>
    simpleToOrcaSettings({ ...orcaToSimpleSettings(compound), ...patch }, compound);

  it('sends no adhesion overrides when untouched, so the profile keeps skirt and brim', () => {
    expect(buildFrom(compound)).toEqual({});
  });

  it('preserves skirt + brim across unrelated Simple edits', () => {
    const current = edit({ wallLoops: 3, infillPercent: 30 });
    expect(current).toMatchObject({ skirt_loops: 2, brim_type: 'outer_only', raft_layers: 0 });
    expect(buildFrom(current)).toEqual({ wall_loops: '3', sparse_infill_density: '30%' });
  });

  it.each([
    ['none', { brim_type: 'no_brim', skirt_loops: '0', raft_layers: '0' }],
    ['brim', { brim_type: 'outer_only', skirt_loops: '0', raft_layers: '0' }],
    ['raft', { brim_type: 'no_brim', skirt_loops: '0', raft_layers: '3' }],
  ] as const)('an explicit change to %s overrides the compound profile', (bedAdhesionType, expected) => {
    expect(buildFrom(edit({ bedAdhesionType }))).toMatchObject(expected);
  });
});

describe('buildProcessOverrides (Simple mode, unresolved inherited brim_type)', () => {
  const inherited = { ...baseline } as Record<string, unknown>;
  delete inherited.brim_type;
  const unresolved = inherited as unknown as OrcaProcessSettings;
  const buildFrom = (current: OrcaProcessSettings) =>
    buildProcessOverrides({
      slicerSettings: current,
      originalProcessSettings: inherited,
      advancedProcessSettings: {},
      simpleMode: true,
      engineVersion: undefined,
    });

  it('shows profile-controlled and forces no adhesion keys', () => {
    expect(orcaToSimpleSettings(unresolved).bedAdhesionType).toBe('profile');
    expect(buildFrom(unresolved)).toEqual({});
  });

  it('sends all three keys once the user picks an explicit choice', () => {
    const current = simpleToOrcaSettings(
      { ...orcaToSimpleSettings(unresolved), bedAdhesionType: 'none' },
      unresolved,
    );
    expect(buildFrom(current)).toEqual({ brim_type: 'no_brim', skirt_loops: '0', raft_layers: '0' });
  });
});

describe('buildProcessOverrides (Advanced mode)', () => {
  it('sends only real edits and does not force adhesion keys', () => {
    expect(build(baseline, { simpleMode: false })).toEqual({});
  });

  it('normalises raw advanced profile values together with editor edits', () => {
    const overrides = build({ ...baseline, wall_loops: 4 } as OrcaProcessSettings, {
      simpleMode: false,
      advanced: { sparse_infill_density: '15%', enable_support: 'true', layer_height: '0.2' },
    });
    expect(overrides).toEqual({
      sparse_infill_density: '15%',
      enable_support: '1',
      layer_height: '0.2',
      wall_loops: '4',
    });
  });
});

describe('normalizeOrcaOverrides', () => {
  it('formats percents, booleans and numbers the way Orca profiles store them', () => {
    expect(
      normalizeOrcaOverrides({
        sparse_infill_density: 20,
        infill_wall_overlap: '15',
        enable_support: false,
        bridge_no_support: 'false',
        layer_height: 0.28,
        brim_type: 'no_brim',
        wall_filament: 1,
        unknown_flag: true,
      }),
    ).toEqual({
      sparse_infill_density: '20%',
      infill_wall_overlap: '15%',
      enable_support: '0',
      bridge_no_support: '0',
      layer_height: '0.28',
      brim_type: 'no_brim',
      wall_filament: '1',
      unknown_flag: '1',
    });
  });

  it('leaves already-native strings and non-numeric percent text untouched', () => {
    expect(
      normalizeOrcaOverrides({ sparse_infill_density: '15%', outer_wall_line_width: '105%', seam_position: 'aligned' }),
    ).toEqual({ sparse_infill_density: '15%', outer_wall_line_width: '105%', seam_position: 'aligned' });
  });
});
