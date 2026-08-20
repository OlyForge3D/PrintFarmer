import { describe, it, expect } from 'vitest';
import {
  mentionsHighFlow,
  stripNozzleSuffix,
  buildMachineProfileLabels,
} from '../machineProfileLabels';

describe('stripNozzleSuffix', () => {
  // Names below are verbatim from
  // GET /api/slicer/profiles/machine/for-model on a live deployment.
  it.each([
    ['Phrozen Arco 0.4 nozzle', 'Phrozen Arco'],
    ['Prusa MK4 0.25 nozzle', 'Prusa MK4'],
    ['Prusa CORE One 0.4 nozzle', 'Prusa CORE One'],
    ['Prusa CORE One L 0.6 nozzle', 'Prusa CORE One L'],
  ])('strips the trailing nozzle token from %s', (raw, expected) => {
    expect(stripNozzleSuffix(raw)).toBe(expected);
  });

  it('keeps the HF marker, which is the only thing separating the variants', () => {
    expect(stripNozzleSuffix('Prusa CORE One HF 0.4 nozzle')).toBe('Prusa CORE One HF');
    expect(stripNozzleSuffix('Prusa CORE One L HF 0.8 nozzle')).toBe('Prusa CORE One L HF');
  });

  it('tolerates "mm" and hyphenated spellings', () => {
    expect(stripNozzleSuffix('Voron 2.4 0.4mm nozzle')).toBe('Voron 2.4');
    expect(stripNozzleSuffix('Ratrig VCore - 0.6 nozzle')).toBe('Ratrig VCore');
  });

  it('leaves a name with no nozzle token untouched', () => {
    expect(stripNozzleSuffix('My custom machine')).toBe('My custom machine');
  });

  it('returns the original when stripping would leave nothing', () => {
    expect(stripNozzleSuffix('0.4 nozzle')).toBe('0.4 nozzle');
  });

  it('does not strip a nozzle token that is not at the end', () => {
    expect(stripNozzleSuffix('0.4 nozzle profile')).toBe('0.4 nozzle profile');
  });
});

describe('mentionsHighFlow', () => {
  it('detects the HF marker regardless of case', () => {
    expect(mentionsHighFlow('Prusa CORE One HF 0.4 nozzle')).toBe(true);
    expect(mentionsHighFlow('prusa core one hf 0.4 nozzle')).toBe(true);
  });

  it('is false for standard profiles', () => {
    expect(mentionsHighFlow('Prusa CORE One 0.4 nozzle')).toBe(false);
    expect(mentionsHighFlow('Phrozen Arco 0.4 nozzle')).toBe(false);
  });

  it('does not match HF embedded inside a longer word', () => {
    expect(mentionsHighFlow('Shelfhorse 0.4 nozzle')).toBe(false);
    expect(mentionsHighFlow('HFX600 0.4 nozzle')).toBe(false);
  });
});

describe('buildMachineProfileLabels', () => {
  it('strips labels while every result stays unique', () => {
    const labels = buildMachineProfileLabels([
      'Prusa CORE One 0.4 nozzle',
      'Prusa CORE One HF 0.4 nozzle',
    ]);

    expect(labels.get('Prusa CORE One 0.4 nozzle')).toBe('Prusa CORE One');
    expect(labels.get('Prusa CORE One HF 0.4 nozzle')).toBe('Prusa CORE One HF');
  });

  it('falls back to raw names when stripping would collapse two entries', () => {
    // Same model, two nozzles: stripping leaves both as "Prusa MK4".
    const labels = buildMachineProfileLabels([
      'Prusa MK4 0.4 nozzle',
      'Prusa MK4 0.6 nozzle',
    ]);

    expect(labels.get('Prusa MK4 0.4 nozzle')).toBe('Prusa MK4 0.4 nozzle');
    expect(labels.get('Prusa MK4 0.6 nozzle')).toBe('Prusa MK4 0.6 nozzle');
  });

  it('keys every supplied name, including on the fallback path', () => {
    const names = ['Prusa MK4 0.4 nozzle', 'Prusa MK4 0.6 nozzle', 'Custom tune'];
    const labels = buildMachineProfileLabels(names);
    expect([...labels.keys()]).toEqual(names);
  });

  it('handles an empty set', () => {
    expect(buildMachineProfileLabels([]).size).toBe(0);
  });
});
