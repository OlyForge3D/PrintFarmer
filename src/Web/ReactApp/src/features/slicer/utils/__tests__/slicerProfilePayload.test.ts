import { describe, it, expect } from 'vitest';
import { buildSlicerProfileJson, stripProcessPresetPrefix } from '../slicerProfilePayload';

function parse(json: string) {
  return JSON.parse(json) as Record<string, unknown>;
}

const base = {
  machineProfileName: 'Prusa CORE One HF 0.4 nozzle',
  filamentProfileName: 'Prusa Generic PLA',
  processPresetId: 'system:0.20mm Standard @CORE One',
  overrides: { layer_height: 0.2 },
};

describe('buildSlicerProfileJson', () => {
  it('sends the canonical machine profile name, never a trimmed display label', () => {
    // This is the load-bearing contract: the picker displays
    // "Prusa CORE One HF" but the worker resolves the full name.
    const payload = parse(buildSlicerProfileJson(base));
    expect(payload.machineProfileName).toBe('Prusa CORE One HF 0.4 nozzle');
    expect(payload.machineProfileName).not.toBe('Prusa CORE One HF');
  });

  it('strips the system: prefix from the process profile name', () => {
    const payload = parse(buildSlicerProfileJson(base));
    expect(payload.processProfileName).toBe('0.20mm Standard @CORE One');
  });

  it('strips the custom: prefix from the process profile name', () => {
    const payload = parse(buildSlicerProfileJson({ ...base, processPresetId: 'custom:abc-123' }));
    expect(payload.processProfileName).toBe('abc-123');
  });

  it('passes an unprefixed process preset through unchanged', () => {
    const payload = parse(buildSlicerProfileJson({ ...base, processPresetId: 'Bare Name' }));
    expect(payload.processProfileName).toBe('Bare Name');
  });

  it('omits multi-extruder and colour keys when they are not supplied', () => {
    const payload = buildSlicerProfileJson(base);
    expect(payload).not.toContain('filamentProfileNames');
    expect(payload).not.toContain('filamentColours');
    expect(payload).not.toContain('filamentColour"');
  });

  it('includes per-extruder names and colours for multi-toolhead printers', () => {
    const payload = parse(buildSlicerProfileJson({
      ...base,
      filamentProfileNames: ['PLA A', 'PLA B'],
      filamentColours: ['#AABBCC', '#112233'],
    }));
    expect(payload.filamentProfileNames).toEqual(['PLA A', 'PLA B']);
    expect(payload.filamentColours).toEqual(['#AABBCC', '#112233']);
  });

  it('includes a single filament colour when supplied', () => {
    const payload = parse(buildSlicerProfileJson({ ...base, filamentColour: '#AD8428' }));
    expect(payload.filamentColour).toBe('#AD8428');
  });

  it('forwards overrides verbatim', () => {
    const payload = parse(buildSlicerProfileJson({
      ...base,
      overrides: { layer_height: 0.28, sparse_infill_density: '15%' },
    }));
    expect(payload.overrides).toEqual({ layer_height: 0.28, sparse_infill_density: '15%' });
  });
});

describe('stripProcessPresetPrefix', () => {
  it.each([
    ['system:0.20mm Standard', '0.20mm Standard'],
    ['custom:abc-123', 'abc-123'],
    ['no-prefix', 'no-prefix'],
    ['', ''],
  ])('%s -> %s', (input, expected) => {
    expect(stripProcessPresetPrefix(input)).toBe(expected);
  });

  it('only strips a leading prefix, not one appearing later', () => {
    expect(stripProcessPresetPrefix('0.2mm system:thing')).toBe('0.2mm system:thing');
  });
});
