/**
 * Version-aware settings resolver tests (issue #578 — Path B).
 *
 * Proves the LIVE editor's version scoping mechanic:
 *   - Real Orca 2.4 additions are hidden for 2.3.1
 *   - Real Orca 2.4 additions are present for 2.4.1
 *   - Stale keys are scrubbed from in-flight payloads on version downgrade
 *   - Renamed fields migrate to the version-correct key (via injected delta,
 *     until a real 2.3↔2.4 rename is identified — the mechanic is proven
 *     independently so it can be enabled without new plumbing)
 *   - Unfiltered/engine-agnostic callers get the full union bundle
 */
import { describe, it, expect } from 'vitest';
import {
  getMetadataForVersion,
  computeVersionScopedMetadata,
  scrubSettingsForVersion,
} from '../orcaSettingsMetadataResolver';
import type { OrcaVersionDelta } from '@/features/slicer/generated/orca-settings-version-delta';

// Real OrcaSlicer 2.4 additions cross-checked against orcaSettingsMetadata.json
const REAL_2_4_ADDITIONS = ['precise_z_height', 'alternate_extra_wall', 'interlocking_beam'];

describe('orcaSettingsMetadataResolver — real 2.4 additions', () => {
  it('2.4.1 includes real 2.4 additions in the process bundle', () => {
    const scoped = getMetadataForVersion('2.4.1');
    for (const key of REAL_2_4_ADDITIONS) {
      expect(scoped.profileTypes.process.settings[key]).toBeDefined();
    }
    expect(scoped.resolvedFor).toBe('2.4.1');
  });

  it('2.3.1 hides every real 2.4 addition from the process bundle', () => {
    const scoped = getMetadataForVersion('2.3.1');
    for (const key of REAL_2_4_ADDITIONS) {
      expect(scoped.profileTypes.process.settings[key]).toBeUndefined();
    }
    expect(scoped.resolvedFor).toBe('2.3.1');
  });

  it('2.3.1 tabs no longer expose 2.4-added field references', () => {
    const scoped = getMetadataForVersion('2.3.1');
    const allFieldKeys = new Set<string>();
    for (const t of scoped.profileTypes.process.tabs) {
      for (const s of t.sections) for (const f of s.fields) allFieldKeys.add(f.key);
    }
    for (const key of REAL_2_4_ADDITIONS) {
      expect(allFieldKeys.has(key)).toBe(false);
    }
  });

  it('unfiltered (no engine version) returns the full union bundle', () => {
    const scoped = getMetadataForVersion(undefined);
    for (const key of REAL_2_4_ADDITIONS) {
      expect(scoped.profileTypes.process.settings[key]).toBeDefined();
    }
    expect(scoped.resolvedFor).toBe('unfiltered');
  });
});

describe('scrubSettingsForVersion — payload correctness on version switch', () => {
  it('drops 2.4-only keys when downgrading to 2.3.1', () => {
    const draft = {
      precise_z_height: 0.05,          // real 2.4 addition — must be scrubbed
      alternate_extra_wall: true,       // real 2.4 addition — must be scrubbed
      layer_height: 0.2,                // present in both — must survive
    };
    const scrubbed = scrubSettingsForVersion(draft, 'process', '2.3.1');
    expect(scrubbed.precise_z_height).toBeUndefined();
    expect(scrubbed.alternate_extra_wall).toBeUndefined();
    expect(scrubbed.layer_height).toBe(0.2);
  });

  it('keeps 2.4-only keys on 2.4.1', () => {
    const draft = {
      precise_z_height: 0.05,
      alternate_extra_wall: true,
      layer_height: 0.2,
    };
    const scrubbed = scrubSettingsForVersion(draft, 'process', '2.4.1');
    expect(scrubbed.precise_z_height).toBe(0.05);
    expect(scrubbed.alternate_extra_wall).toBe(true);
    expect(scrubbed.layer_height).toBe(0.2);
  });

  it('is idempotent — scrubbing a clean payload preserves all keys', () => {
    const draft = { layer_height: 0.2, sparse_infill_density: 20 };
    const scrubbed = scrubSettingsForVersion(draft, 'process', '2.3.1');
    expect(scrubbed).toEqual(draft);
  });
});

describe('scrubSettingsForVersion — added/removed/renamed with injected delta', () => {
  // Injected test delta proves the added/removed/renamed mechanic against a
  // known-good real setting key (`enable_arc_fitting` exists in 2.4 metadata
  // and is a plausible rename target). This lets the test assert exact
  // semantics without waiting on a real 2.3↔2.4 rename being wired into
  // production data.
  const testDelta: { minVersion: string; delta: OrcaVersionDelta }[] = [
    {
      minVersion: '2.4.0',
      delta: {
        addedIn: ['precise_z_height'],
        retiredIn: [],
        renamedIn: {
          // In 2.4: enable_arc_fitting. In earlier hypothetical: enable_arc_fit_legacy.
          enable_arc_fitting: 'enable_arc_fit_legacy',
        },
      },
    },
  ];

  it('2.4.1 keeps NEW key, rejects OLD key', () => {
    const draft = {
      enable_arc_fitting: true,           // 2.4 canonical name
      enable_arc_fit_legacy: true,        // 2.3 pre-rename name — must be dropped
      layer_height: 0.2,
    };
    const scrubbed = scrubSettingsForVersion(draft, 'process', '2.4.1', testDelta);
    expect(scrubbed.enable_arc_fitting).toBe(true);
    expect(scrubbed.enable_arc_fit_legacy).toBeUndefined();
    expect(scrubbed.layer_height).toBe(0.2);
  });

  it('2.3.1 MIGRATES new-key value onto old key (rename backwards)', () => {
    const draft = {
      enable_arc_fitting: true,           // user edited on 2.4 then switched down
      layer_height: 0.2,
    };
    const scrubbed = scrubSettingsForVersion(draft, 'process', '2.3.1', testDelta);
    // Value migrated to the older key
    expect(scrubbed.enable_arc_fit_legacy).toBe(true);
    // New key gone from payload
    expect(scrubbed.enable_arc_fitting).toBeUndefined();
    expect(scrubbed.layer_height).toBe(0.2);
  });

  it('2.3.1 metadata surfaces old rename key, NOT new one', () => {
    const scoped = computeVersionScopedMetadata('2.3.1', testDelta);
    expect(scoped.profileTypes.process.settings.enable_arc_fit_legacy).toBeDefined();
    expect(scoped.profileTypes.process.settings.enable_arc_fitting).toBeUndefined();
    expect(scoped.renameFromNewToThis.enable_arc_fitting).toBe('enable_arc_fit_legacy');
  });

  it('2.4.1 metadata surfaces new rename key, NOT old one', () => {
    const scoped = computeVersionScopedMetadata('2.4.1', testDelta);
    expect(scoped.profileTypes.process.settings.enable_arc_fitting).toBeDefined();
    expect(scoped.profileTypes.process.settings.enable_arc_fit_legacy).toBeUndefined();
  });

  it('switching 2.3.1 → 2.4.1 does not carry old-key stale state', () => {
    // Simulate user edited on 2.3.1 then switched up to 2.4.1
    const draft = {
      enable_arc_fit_legacy: true,        // was legal on 2.3
      layer_height: 0.2,
    };
    const scrubbed = scrubSettingsForVersion(draft, 'process', '2.4.1', testDelta);
    // Old key dropped — user must explicitly opt into new field
    expect(scrubbed.enable_arc_fit_legacy).toBeUndefined();
    expect(scrubbed.layer_height).toBe(0.2);
  });
});

describe('multi-state scrub → submit-payload diff (Vasquez r7)', () => {
  // Reproduces the full NewSliceJobPage lifecycle: user edits a 2.4-only key
  // in the LIVE editor (which writes to `slicerSettings`, NOT
  // `advancedProcessSettings`), then switches engine to 2.3.1. The scrub
  // effect must remove the key from `slicerSettings` AND
  // `originalProcessSettings` so `diffProcessOverrides()` at submit time
  // cannot leak the 2.4-only key into the legacy worker's overrides payload.
  it('DEFENSE-IN-DEPTH: submit-time scrub of merged overrides also strips a 2.4-only key on 2.3.1', () => {
    // Reproduces the r7 leak path Bishop identified: even if per-state scrubs
    // are bypassed or a future state path is added, scrubbing the merged
    // overrides at submit time is a final guarantee.
    const advancedProcessSettings = { precise_z_height: 0.05 } as Record<string, unknown>;
    const modifiedProcessOverrides = { alternate_extra_wall: true, layer_height: 0.3 } as Record<string, unknown>;
    const merged = { ...advancedProcessSettings, ...modifiedProcessOverrides };
    const scrubbed = scrubSettingsForVersion(merged, 'process', '2.3.1');
    expect(scrubbed.precise_z_height).toBeUndefined();
    expect(scrubbed.alternate_extra_wall).toBeUndefined();
    expect(scrubbed.layer_height).toBe(0.3);
  });

  it('scrubbed slicerSettings + originalProcessSettings yields empty overrides for a 2.4-only edit on 2.3.1', () => {
    // BEFORE version change (on 2.4.1): user edited precise_z_height
    const originalOn24 = { layer_height: 0.2 } as Record<string, unknown>;
    const slicerSettingsOn24 = {
      layer_height: 0.2,
      precise_z_height: 0.05, // 2.4-only value the user picked
    } as Record<string, unknown>;

    // Version switch: 2.4.1 -> 2.3.1. Apply scrub to BOTH states.
    const scrubbedSettings = scrubSettingsForVersion(slicerSettingsOn24, 'process', '2.3.1');
    const scrubbedOriginal = scrubSettingsForVersion(originalOn24, 'process', '2.3.1');

    // 2.4-only key must be gone from both states.
    expect(scrubbedSettings.precise_z_height).toBeUndefined();
    expect(scrubbedOriginal.precise_z_height).toBeUndefined();
    // Common key survives.
    expect(scrubbedSettings.layer_height).toBe(0.2);

    // Compute overrides the way NewSliceJobPage's submit path does.
    const overrides: Record<string, unknown> = {};
    for (const [key, value] of Object.entries(scrubbedSettings)) {
      if (JSON.stringify(value) !== JSON.stringify(scrubbedOriginal[key])) {
        overrides[key] = value;
      }
    }
    // Payload sent to the 2.3.1 worker must not contain the 2.4-only key.
    expect(overrides.precise_z_height).toBeUndefined();
    // And since layer_height was unchanged, no key at all leaks.
    expect(Object.keys(overrides)).toHaveLength(0);
  });

  it('scrubbing slicerSettings AND originalProcessSettings preserves a legitimate user edit on the target version', () => {
    // User edited layer_height (present in both engine versions) on 2.4.1.
    const originalOn24 = {
      layer_height: 0.2,
      precise_z_height: 0.0,
    } as Record<string, unknown>;
    const slicerSettingsOn24 = {
      layer_height: 0.3, // user edit
      precise_z_height: 0.0,
    } as Record<string, unknown>;

    const scrubbedSettings = scrubSettingsForVersion(slicerSettingsOn24, 'process', '2.3.1');
    const scrubbedOriginal = scrubSettingsForVersion(originalOn24, 'process', '2.3.1');

    const overrides: Record<string, unknown> = {};
    for (const [key, value] of Object.entries(scrubbedSettings)) {
      if (JSON.stringify(value) !== JSON.stringify(scrubbedOriginal[key])) {
        overrides[key] = value;
      }
    }
    // Legitimate cross-version edit survives.
    expect(overrides.layer_height).toBe(0.3);
    // 2.4-only key never reaches the 2.3.1 worker.
    expect(overrides.precise_z_height).toBeUndefined();
  });
});

describe('metadata identity/caching', () => {
  it('same version returns metadata with same setting count', () => {
    const a = getMetadataForVersion('2.4.1');
    const b = getMetadataForVersion('2.4.1');
    expect(Object.keys(a.profileTypes.process.settings).length).toBe(
      Object.keys(b.profileTypes.process.settings).length,
    );
  });

  it('different versions yield different setting counts (real 2.4 delta)', () => {
    const older = getMetadataForVersion('2.3.1');
    const newer = getMetadataForVersion('2.4.1');
    // 2.4.1 must have at least the 3 real additions more than 2.3.1
    expect(
      Object.keys(newer.profileTypes.process.settings).length -
        Object.keys(older.profileTypes.process.settings).length,
    ).toBeGreaterThanOrEqual(REAL_2_4_ADDITIONS.length);
  });
});
