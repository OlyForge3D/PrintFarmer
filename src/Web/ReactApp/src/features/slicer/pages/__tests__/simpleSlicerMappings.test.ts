/**
 * Unit tests for the Simple-mode ↔ OrcaProcessSettings mapping utilities.
 * Covers bed-adhesion fan-out, support_type preservation, and default fallbacks.
 */
import { describe, it, expect } from 'vitest';
import { orcaToSimpleSettings, simpleToOrcaSettings } from '../simpleSlicerMappings';
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
    expect(result.bedAdhesionType).toBe('none');
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
  it('returns none when brim_type is no_brim', () => {
    const result = orcaToSimpleSettings({ ...EMPTY_ORCA, brim_type: 'no_brim' });
    expect(result.bedAdhesionType).toBe('none');
  });

  it('returns none when brim_type is absent', () => {
    const result = orcaToSimpleSettings(EMPTY_ORCA);
    expect(result.bedAdhesionType).toBe('none');
  });

  it('returns brim when brim_type is outer_only', () => {
    const result = orcaToSimpleSettings({ ...EMPTY_ORCA, brim_type: 'outer_only' });
    expect(result.bedAdhesionType).toBe('brim');
  });

  it('returns brim for any non-no_brim brim_type value', () => {
    for (const bt of ['inner_only', 'outer_and_inner', 'auto_brim', 'brim_ears', 'painted']) {
      const result = orcaToSimpleSettings({ ...EMPTY_ORCA, brim_type: bt });
      expect(result.bedAdhesionType).toBe('brim');
    }
  });

  it('returns skirt when skirt_loops > 0', () => {
    const result = orcaToSimpleSettings({ ...EMPTY_ORCA, skirt_loops: 2 });
    expect(result.bedAdhesionType).toBe('skirt');
  });

  it('returns raft when raft_layers > 0', () => {
    const result = orcaToSimpleSettings({ ...EMPTY_ORCA, raft_layers: 3 });
    expect(result.bedAdhesionType).toBe('raft');
  });

  it('raft takes priority over skirt when both are set', () => {
    const result = orcaToSimpleSettings({ ...EMPTY_ORCA, raft_layers: 3, skirt_loops: 1 });
    expect(result.bedAdhesionType).toBe('raft');
  });

  it('raft takes priority over brim when both are set', () => {
    const result = orcaToSimpleSettings({ ...EMPTY_ORCA, raft_layers: 3, brim_type: 'outer_only' });
    expect(result.bedAdhesionType).toBe('raft');
  });

  it('skirt takes priority over brim when both are set', () => {
    const result = orcaToSimpleSettings({ ...EMPTY_ORCA, skirt_loops: 1, brim_type: 'outer_only' });
    expect(result.bedAdhesionType).toBe('skirt');
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
