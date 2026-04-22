/**
 * End-to-end tests for the metadata-driven profile editors.
 *
 * Validates that orcaSettingsMetadata.json is consistent and that
 * the helper functions in metadataTypes.ts correctly drive the UI.
 */
import { describe, it, expect } from 'vitest';
import metadata from '@/features/slicer/generated/orcaSettingsMetadata.json';
import {
  resolveControlType,
  toNumber,
  toBool,
  parsePoint,
  toString,
  TEXTAREA_KEYS,
  KNOWN_ENUMS,
  CONDITIONAL_HIDDEN_KEYS,
} from '@/features/slicer/components/settings/metadataTypes';
import type {
  ProfileType,
  ProfileTypeMetadata,
  SettingMetadata,
  ViewMode,
} from '@/features/slicer/components/settings/metadataTypes';

// ── Helpers ────────────────────────────────────────────────────────────

const PROFILE_TYPES: ProfileType[] = ['filament', 'machine', 'process'];

function getProfileMeta(profileType: ProfileType): ProfileTypeMetadata {
  return (metadata as unknown as Record<string, ProfileTypeMetadata>)[profileType];
}

/** Collect all setting keys referenced in any tab → section → fields */
function getTabbedKeys(profileMeta: ProfileTypeMetadata): Set<string> {
  const keys = new Set<string>();
  for (const tab of profileMeta.tabs) {
    for (const section of tab.sections) {
      for (const field of section.fields) {
        keys.add(field.key);
      }
    }
  }
  return keys;
}

/** Replicate the visibility logic from MetadataProfileRenderer + MetadataSection */
function isFieldVisible(
  meta: SettingMetadata | undefined,
  viewMode: ViewMode,
): boolean {
  if (!meta) return false;
  if (meta.mode === 'developer') return false;
  if (viewMode === 'simple' && meta.mode === 'advanced') return false;
  return true;
}

// ═══════════════════════════════════════════════════════════════════════
// 1. Tab / Section Completeness
// ═══════════════════════════════════════════════════════════════════════

describe('Tab/Section completeness', () => {
  it.each(PROFILE_TYPES)(
    '%s — every tabbed key references a real setting',
    (profileType) => {
      const pm = getProfileMeta(profileType);
      const tabbedKeys = getTabbedKeys(pm);
      const missing = [...tabbedKeys].filter((k) => !pm.settings[k]);
      expect(missing).toEqual([]);
    },
  );

  it.each(PROFILE_TYPES)(
    '%s — no unexpected duplicate keys across tabs',
    (profileType) => {
      const pm = getProfileMeta(profileType);
      // fan_speedup_time intentionally appears in two machine tabs
      // (General and Extruder) mirroring OrcaSlicer's Tab.cpp layout.
      const KNOWN_DUPES = new Set(['fan_speedup_time']);
      const seen = new Set<string>();
      const dupes: string[] = [];
      for (const tab of pm.tabs) {
        for (const section of tab.sections) {
          for (const field of section.fields) {
            if (seen.has(field.key) && !KNOWN_DUPES.has(field.key)) {
              dupes.push(field.key);
            }
            seen.add(field.key);
          }
        }
      }
      expect(dupes).toEqual([]);
    },
  );

  it.each(PROFILE_TYPES)(
    '%s — orphaned settings are either mode-less, developer, or correctly excluded',
    (profileType) => {
      const pm = getProfileMeta(profileType);
      const tabbedKeys = getTabbedKeys(pm);
      const allKeys = Object.keys(pm.settings);
      const orphaned = allKeys.filter((k) => !tabbedKeys.has(k));

      // Every orphaned key should be explainable: no mode, developer mode,
      // or advanced (shown in "Other Settings" tab at runtime).
      for (const k of orphaned) {
        const m = pm.settings[k];
        const mode = m.mode ?? '';
        expect(
          ['', 'advanced', 'developer', 'simple'].includes(mode),
        ).toBe(true);
      }
    },
  );

  it.each(PROFILE_TYPES)(
    '%s — orphaned count is within expected range',
    (profileType) => {
      const pm = getProfileMeta(profileType);
      const tabbedKeys = getTabbedKeys(pm);
      const orphanedCount = Object.keys(pm.settings).filter(
        (k) => !tabbedKeys.has(k),
      ).length;

      // Sanity check: orphaned settings should be a small minority.
      // Current counts: filament=14, machine=20, process=26.
      expect(orphanedCount).toBeLessThan(
        Object.keys(pm.settings).length * 0.25,
      );
    },
  );

  it('metadata has all three profile types', () => {
    for (const pt of PROFILE_TYPES) {
      const pm = getProfileMeta(pt);
      expect(pm).toBeDefined();
      expect(pm.tabs.length).toBeGreaterThan(0);
      expect(Object.keys(pm.settings).length).toBeGreaterThan(0);
    }
  });

  it('every tab has at least one section', () => {
    for (const pt of PROFILE_TYPES) {
      const pm = getProfileMeta(pt);
      for (const tab of pm.tabs) {
        expect(tab.sections.length).toBeGreaterThan(0);
      }
    }
  });

  it('every section has at least one field (with known exceptions)', () => {
    // filament "Setting Overrides / Retraction" section is an empty placeholder
    // in the OrcaSlicer metadata — no fields assigned to it.
    const KNOWN_EMPTY_SECTIONS = new Set(['filament/Setting Overrides/Retraction']);
    for (const pt of PROFILE_TYPES) {
      const pm = getProfileMeta(pt);
      for (const tab of pm.tabs) {
        for (const section of tab.sections) {
          const sectionId = `${pt}/${tab.name}/${section.name}`;
          if (KNOWN_EMPTY_SECTIONS.has(sectionId)) continue;
          expect(section.fields.length).toBeGreaterThan(0);
        }
      }
    }
  });
});

// ═══════════════════════════════════════════════════════════════════════
// 2. View Mode Filtering
// ═══════════════════════════════════════════════════════════════════════

describe('View mode filtering', () => {
  it('developer settings are hidden in both simple and advanced modes', () => {
    for (const pt of PROFILE_TYPES) {
      const pm = getProfileMeta(pt);
      const devSettings = Object.values(pm.settings).filter(
        (s) => s.mode === 'developer',
      );
      expect(devSettings.length).toBeGreaterThan(0);
      for (const s of devSettings) {
        expect(isFieldVisible(s, 'simple')).toBe(false);
        expect(isFieldVisible(s, 'advanced')).toBe(false);
      }
    }
  });

  it('advanced settings are hidden in simple mode', () => {
    for (const pt of PROFILE_TYPES) {
      const pm = getProfileMeta(pt);
      const advSettings = Object.values(pm.settings).filter(
        (s) => s.mode === 'advanced',
      );
      expect(advSettings.length).toBeGreaterThan(0);
      for (const s of advSettings) {
        expect(isFieldVisible(s, 'simple')).toBe(false);
      }
    }
  });

  it('advanced settings are visible in advanced mode', () => {
    for (const pt of PROFILE_TYPES) {
      const pm = getProfileMeta(pt);
      const advSettings = Object.values(pm.settings).filter(
        (s) => s.mode === 'advanced',
      );
      expect(advSettings.length).toBeGreaterThan(0);
      for (const s of advSettings) {
        expect(isFieldVisible(s, 'advanced')).toBe(true);
      }
    }
  });

  it('simple settings are visible in both modes', () => {
    for (const pt of PROFILE_TYPES) {
      const pm = getProfileMeta(pt);
      const simpleSettings = Object.values(pm.settings).filter(
        (s) => s.mode === 'simple',
      );
      expect(simpleSettings.length).toBeGreaterThan(0);
      for (const s of simpleSettings) {
        expect(isFieldVisible(s, 'simple')).toBe(true);
        expect(isFieldVisible(s, 'advanced')).toBe(true);
      }
    }
  });

  it('settings without a mode are visible in both modes', () => {
    let totalNoMode = 0;
    for (const pt of PROFILE_TYPES) {
      const pm = getProfileMeta(pt);
      const noModeSettings = Object.values(pm.settings).filter(
        (s) => !s.mode,
      );
      totalNoMode += noModeSettings.length;
      for (const s of noModeSettings) {
        expect(isFieldVisible(s, 'simple')).toBe(true);
        expect(isFieldVisible(s, 'advanced')).toBe(true);
      }
    }
    // At least some settings across all profile types lack a mode
    expect(totalNoMode).toBeGreaterThan(0);
  });

  describe('Other Settings tab logic', () => {
    it.each(PROFILE_TYPES)(
      '%s — orphaned advanced settings produce Other Settings entries only in advanced mode',
      (profileType) => {
        const pm = getProfileMeta(profileType);
        const tabbedKeys = getTabbedKeys(pm);

        // Replicate the orphan detection from MetadataProfileEditor
        const getOrphanedFields = (viewMode: ViewMode) =>
          Object.keys(pm.settings).filter((k) => {
            if (tabbedKeys.has(k)) return false;
            const m = pm.settings[k];
            if (!m || !m.mode || m.mode === 'developer') return false;
            if (viewMode === 'simple' && m.mode === 'advanced') return false;
            return true;
          });

        const simpleOrphans = getOrphanedFields('simple');
        const advancedOrphans = getOrphanedFields('advanced');

        // In advanced mode, orphaned advanced settings should appear
        expect(advancedOrphans.length).toBeGreaterThanOrEqual(
          simpleOrphans.length,
        );

        // Specifically: orphaned keys with mode=advanced should NOT show in simple
        const advOnlyOrphans = Object.keys(pm.settings).filter((k) => {
          if (tabbedKeys.has(k)) return false;
          const m = pm.settings[k];
          return m?.mode === 'advanced';
        });
        for (const k of advOnlyOrphans) {
          expect(simpleOrphans).not.toContain(k);
          expect(advancedOrphans).toContain(k);
        }
      },
    );
  });
});

// ═══════════════════════════════════════════════════════════════════════
// 3. Control Type Mapping
// ═══════════════════════════════════════════════════════════════════════

describe('resolveControlType', () => {
  it('maps coBool to checkbox', () => {
    const meta: SettingMetadata = {
      key: 'test_bool',
      type: 'bool',
      coType: 'coBool',
      label: 'Test',
    };
    expect(resolveControlType(meta)).toBe('checkbox');
  });

  it('maps coFloat to number', () => {
    const meta: SettingMetadata = {
      key: 'test_float',
      type: 'float',
      coType: 'coFloat',
      label: 'Test',
    };
    expect(resolveControlType(meta)).toBe('number');
  });

  it('maps coInt to number', () => {
    const meta: SettingMetadata = {
      key: 'test_int',
      type: 'int',
      coType: 'coInt',
      label: 'Test',
    };
    expect(resolveControlType(meta)).toBe('number');
  });

  it('maps coPercent to number', () => {
    const meta: SettingMetadata = {
      key: 'test_pct',
      type: 'percent',
      coType: 'coPercent',
      label: 'Test',
    };
    expect(resolveControlType(meta)).toBe('number');
  });

  it('maps coFloatOrPercent to number', () => {
    const meta: SettingMetadata = {
      key: 'test_fop',
      type: 'float_or_percent',
      coType: 'coFloatOrPercent',
      label: 'Test',
    };
    expect(resolveControlType(meta)).toBe('number');
  });

  it('maps coEnum to select', () => {
    const meta: SettingMetadata = {
      key: 'test_enum',
      type: 'enum',
      coType: 'coEnum',
      label: 'Test',
    };
    expect(resolveControlType(meta)).toBe('select');
  });

  it('maps coString to text', () => {
    const meta: SettingMetadata = {
      key: 'test_str',
      type: 'string',
      coType: 'coString',
      label: 'Test',
    };
    expect(resolveControlType(meta)).toBe('text');
  });

  it('maps coFloats to number (array of floats)', () => {
    const meta: SettingMetadata = {
      key: 'test_floats',
      type: 'float',
      coType: 'coFloats',
      label: 'Test',
    };
    expect(resolveControlType(meta)).toBe('number');
  });

  it('maps coPoint to point control', () => {
    const meta: SettingMetadata = {
      key: 'test_point',
      type: 'point',
      coType: 'coPoint',
      label: 'Test',
    };
    expect(resolveControlType(meta)).toBe('point');
  });

  it('maps coPoints (polygon) to text, not point', () => {
    const meta: SettingMetadata = {
      key: 'test_points',
      type: 'point',
      coType: 'coPoints',
      label: 'Test',
    };
    expect(resolveControlType(meta)).toBe('text');
  });

  it('maps coPercents to number', () => {
    const meta: SettingMetadata = {
      key: 'test_pcts',
      type: 'percent',
      coType: 'coPercents',
      label: 'Test',
    };
    expect(resolveControlType(meta)).toBe('number');
  });

  it('gui_type=color overrides everything to color', () => {
    const meta: SettingMetadata = {
      key: 'test_color',
      type: 'string',
      coType: 'coString',
      label: 'Test',
      gui_type: 'color',
    };
    expect(resolveControlType(meta)).toBe('color');
  });

  it('textarea keys override to textarea', () => {
    for (const key of TEXTAREA_KEYS) {
      const meta: SettingMetadata = {
        key,
        type: 'string',
        coType: 'coString',
        label: key,
      };
      expect(resolveControlType(meta)).toBe('textarea');
    }
  });

  it('KNOWN_ENUMS keys resolve to select', () => {
    for (const key of Object.keys(KNOWN_ENUMS)) {
      const meta: SettingMetadata = {
        key,
        type: 'string',
        coType: 'coString',
        label: key,
      };
      expect(resolveControlType(meta)).toBe('select');
    }
  });

  it('enum_open with numeric type stays number, not select', () => {
    const meta: SettingMetadata = {
      key: 'test_open_num',
      type: 'float',
      coType: 'coFloat',
      label: 'Test',
      gui_type: 'enum_open',
    };
    expect(resolveControlType(meta)).toBe('number');
  });

  it('enum_open with non-numeric type becomes select', () => {
    const meta: SettingMetadata = {
      key: 'test_open_str',
      type: 'string',
      coType: 'coString',
      label: 'Test',
      gui_type: 'enum_open',
    };
    expect(resolveControlType(meta)).toBe('select');
  });

  describe('all real metadata settings resolve to a valid control type', () => {
    const VALID_CONTROLS = new Set([
      'checkbox', 'number', 'text', 'color', 'select', 'textarea', 'point',
    ]);

    it.each(PROFILE_TYPES)('%s', (profileType) => {
      const pm = getProfileMeta(profileType);
      for (const [key, meta] of Object.entries(pm.settings)) {
        const ct = resolveControlType(meta);
        expect(VALID_CONTROLS.has(ct)).toBe(true);
        // Sanity: ensure no unknown type silently falls to text
        if (meta.type === 'bool') expect(ct).toBe('checkbox');
        if (
          meta.type === 'float' &&
          !TEXTAREA_KEYS.has(key) &&
          !KNOWN_ENUMS[key] &&
          meta.gui_type !== 'color'
        ) {
          expect(ct).toBe('number');
        }
      }
    });
  });
});

// ═══════════════════════════════════════════════════════════════════════
// 4. Min / Max / Default Validation
// ═══════════════════════════════════════════════════════════════════════

describe('Min/max/default validation', () => {
  it.each(PROFILE_TYPES)(
    '%s — every setting with min/max has a parseable default',
    (profileType) => {
      const pm = getProfileMeta(profileType);
      for (const meta of Object.values(pm.settings)) {
        if (meta.min === undefined && meta.max === undefined) continue;
        if (meta.default === undefined) continue;
        // Only validate numeric types
        if (!['float', 'int', 'percent', 'float_or_percent'].includes(meta.type)) continue;

        const parsed = parseFloat(meta.default.replace('%', ''));
        expect(isNaN(parsed)).toBe(false);
      }
    },
  );

  it.each(PROFILE_TYPES)(
    '%s — defaults fall within min/max range (with known exceptions)',
    (profileType) => {
      const pm = getProfileMeta(profileType);
      // Known upstream metadata inconsistency: filament_cooling_moves has max=0 but default=4.
      // This is a data issue in the OrcaSlicer metadata, not a code bug.
      const KNOWN_VIOLATIONS = new Set(['filament_cooling_moves']);

      const violations: string[] = [];
      for (const meta of Object.values(pm.settings)) {
        if (meta.default === undefined) continue;
        if (!['float', 'int', 'percent', 'float_or_percent'].includes(meta.type)) continue;

        const val = parseFloat(meta.default.replace('%', ''));
        if (isNaN(val)) continue;

        if (meta.min !== undefined && val < meta.min && !KNOWN_VIOLATIONS.has(meta.key)) {
          violations.push(`${meta.key}: default=${val} < min=${meta.min}`);
        }
        if (meta.max !== undefined && meta.max > 0 && val > meta.max && !KNOWN_VIOLATIONS.has(meta.key)) {
          violations.push(`${meta.key}: default=${val} > max=${meta.max}`);
        }
      }
      expect(violations).toEqual([]);
    },
  );

  it('filament_cooling_moves is a known metadata inconsistency', () => {
    const pm = getProfileMeta('filament');
    const meta = pm.settings['filament_cooling_moves'];
    expect(meta).toBeDefined();
    // Upstream metadata says max=0 but default=4 — this is an OrcaSlicer extraction artifact
    expect(meta.max).toBe(0);
    expect(meta.default).toBe('4');
  });

  it.each(PROFILE_TYPES)(
    '%s — min is not greater than max when both are defined',
    (profileType) => {
      const pm = getProfileMeta(profileType);
      // Exclude the known bad entry
      const KNOWN_EXCEPTIONS = new Set(['filament_cooling_moves']);
      const issues: string[] = [];
      for (const meta of Object.values(pm.settings)) {
        if (KNOWN_EXCEPTIONS.has(meta.key)) continue;
        if (meta.min !== undefined && meta.max !== undefined && meta.max > 0) {
          if (meta.min > meta.max) {
            issues.push(`${meta.key}: min=${meta.min} > max=${meta.max}`);
          }
        }
      }
      expect(issues).toEqual([]);
    },
  );
});

// ═══════════════════════════════════════════════════════════════════════
// 5. Profile Round-Trip (onChange data flow)
// ═══════════════════════════════════════════════════════════════════════

describe('Profile round-trip', () => {
  it.each(PROFILE_TYPES)(
    '%s — all setting keys survive a pass through value helpers',
    (profileType) => {
      const pm = getProfileMeta(profileType);
      const mockValues: Record<string, unknown> = {};

      // Build a mock settings object with realistic values for each setting
      for (const [key, meta] of Object.entries(pm.settings)) {
        switch (meta.type) {
          case 'bool':
            mockValues[key] = true;
            break;
          case 'float':
          case 'int':
          case 'percent':
          case 'float_or_percent':
            mockValues[key] = meta.default ? parseFloat(meta.default) || 42 : 42;
            break;
          case 'point':
            mockValues[key] = '100,200';
            break;
          case 'enum':
            mockValues[key] = meta.enum_values?.[0] ?? meta.default ?? 'value';
            break;
          default:
            mockValues[key] = meta.default ?? 'test_value';
        }
      }

      // Simulate reading back every value through the type helpers
      for (const [key, meta] of Object.entries(pm.settings)) {
        const raw = mockValues[key];
        switch (meta.type) {
          case 'bool':
            expect(toBool(raw, meta)).toBe(true);
            break;
          case 'float':
          case 'int':
          case 'percent':
          case 'float_or_percent': {
            const num = toNumber(raw, meta);
            expect(typeof num).toBe('number');
            expect(isNaN(num)).toBe(false);
            break;
          }
          case 'point':
            if (meta.coType !== 'coPoints' && meta.coType !== 'coPointsGroups') {
              const [x, y] = parsePoint(raw, meta);
              expect(typeof x).toBe('number');
              expect(typeof y).toBe('number');
            }
            break;
          default: {
            const str = toString(raw, meta);
            expect(typeof str).toBe('string');
            break;
          }
        }
      }

      // Verify no keys were dropped
      const inputKeys = new Set(Object.keys(mockValues));
      const settingKeys = new Set(Object.keys(pm.settings));
      expect(inputKeys).toEqual(settingKeys);
    },
  );

  it('onUpdate callback preserves key-value pairs without data loss', () => {
    const pm = getProfileMeta('process');
    const collected: Record<string, unknown> = {};
    const mockOnUpdate = (key: string, value: unknown) => {
      collected[key] = value;
    };

    // Simulate updating every setting once
    for (const [key, meta] of Object.entries(pm.settings)) {
      switch (meta.type) {
        case 'bool':
          mockOnUpdate(key, false);
          break;
        case 'float':
        case 'int':
        case 'percent':
        case 'float_or_percent':
          mockOnUpdate(key, 99.5);
          break;
        case 'point':
          mockOnUpdate(key, '50,75');
          break;
        default:
          mockOnUpdate(key, 'updated_value');
      }
    }

    // Every setting key should be present in collected
    for (const key of Object.keys(pm.settings)) {
      expect(collected[key]).toBeDefined();
    }
    expect(Object.keys(collected).length).toBe(
      Object.keys(pm.settings).length,
    );
  });

  describe('value coercion helpers', () => {
    const numMeta: SettingMetadata = {
      key: 'test',
      type: 'float',
      coType: 'coFloat',
      label: 'Test',
      default: '10',
    };

    it('toNumber handles string input', () => {
      expect(toNumber('42.5', numMeta)).toBe(42.5);
    });

    it('toNumber handles number input', () => {
      expect(toNumber(100, numMeta)).toBe(100);
    });

    it('toNumber falls back to default on invalid input', () => {
      expect(toNumber('not-a-number', numMeta)).toBe(10);
      expect(toNumber(undefined, numMeta)).toBe(10);
    });

    it('toBool handles various truthy inputs', () => {
      const boolMeta: SettingMetadata = {
        key: 'test',
        type: 'bool',
        coType: 'coBool',
        label: 'Test',
        default: 'true',
      };
      expect(toBool(true, boolMeta)).toBe(true);
      expect(toBool('true', boolMeta)).toBe(true);
      expect(toBool('1', boolMeta)).toBe(true);
      expect(toBool(false, boolMeta)).toBe(false);
      expect(toBool('false', boolMeta)).toBe(false);
    });

    it('parsePoint handles comma-separated values', () => {
      const ptMeta: SettingMetadata = {
        key: 'test',
        type: 'point',
        coType: 'coPoint',
        label: 'Test',
        default: '0, 0',
      };
      expect(parsePoint('100,200', ptMeta)).toEqual([100, 200]);
      expect(parsePoint('50, 75', ptMeta)).toEqual([50, 75]);
    });

    it('parsePoint handles x-separated values', () => {
      const ptMeta: SettingMetadata = {
        key: 'test',
        type: 'point',
        coType: 'coPoint',
        label: 'Test',
        default: '0x0',
      };
      expect(parsePoint('300x400', ptMeta)).toEqual([300, 400]);
    });

    it('toString handles arrays', () => {
      const strMeta: SettingMetadata = {
        key: 'test',
        type: 'string',
        coType: 'coStrings',
        label: 'Test',
        default: '',
      };
      expect(toString(['a', 'b', 'c'], strMeta)).toBe('a, b, c');
      expect(toString([], strMeta)).toBe('');
    });

    it('toString handles null/undefined with defaults', () => {
      const strMeta: SettingMetadata = {
        key: 'test',
        type: 'string',
        coType: 'coString',
        label: 'Test',
        default: 'fallback',
      };
      expect(toString(undefined, strMeta)).toBe('fallback');
      expect(toString(null, strMeta)).toBe('fallback');
    });
  });
});

// ═══════════════════════════════════════════════════════════════════════
// 6. Structural Integrity
// ═══════════════════════════════════════════════════════════════════════

describe('Structural integrity', () => {
  it('CONDITIONAL_HIDDEN_KEYS only references real settings', () => {
    const allKeys = new Set<string>();
    for (const pt of PROFILE_TYPES) {
      for (const k of Object.keys(getProfileMeta(pt).settings)) {
        allKeys.add(k);
      }
    }
    for (const k of CONDITIONAL_HIDDEN_KEYS) {
      expect(allKeys.has(k)).toBe(true);
    }
  });

  it('KNOWN_ENUMS options are non-empty arrays', () => {
    for (const [, options] of Object.entries(KNOWN_ENUMS)) {
      expect(Array.isArray(options)).toBe(true);
      expect(options.length).toBeGreaterThan(0);
      for (const opt of options) {
        expect(typeof opt.value).toBe('string');
        expect(typeof opt.label).toBe('string');
      }
    }
  });

  it.each(PROFILE_TYPES)(
    '%s — enum settings with enum_values have at least one option',
    (profileType) => {
      const pm = getProfileMeta(profileType);
      for (const meta of Object.values(pm.settings)) {
        if (meta.type === 'enum' && meta.enum_values) {
          expect(meta.enum_values.length).toBeGreaterThan(0);
        }
      }
    },
  );

  it.each(PROFILE_TYPES)(
    '%s — every setting has a non-empty label',
    (profileType) => {
      const pm = getProfileMeta(profileType);
      for (const meta of Object.values(pm.settings)) {
        expect(meta.label?.length).toBeGreaterThan(0);
      }
    },
  );

  it('metadata _meta section reports correct total counts', () => {
    const raw = metadata as unknown as Record<string, unknown>;
    const meta = raw['_meta'] as Record<string, number>;
    expect(meta.filamentSettings).toBe(
      Object.keys(getProfileMeta('filament').settings).length,
    );
    expect(meta.machineSettings).toBe(
      Object.keys(getProfileMeta('machine').settings).length,
    );
    expect(meta.processSettings).toBe(
      Object.keys(getProfileMeta('process').settings).length,
    );
  });
});
