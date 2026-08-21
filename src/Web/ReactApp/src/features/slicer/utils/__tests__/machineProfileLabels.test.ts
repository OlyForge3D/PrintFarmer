import { describe, it, expect } from 'vitest';
import {
  mentionsHighFlow,
  stripNozzleSuffix,
  buildMachineProfileLabels,
  isProcessProfileCoreOneVariantCompatible,
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

  it('detects the unspaced form, which a \\b-anchored pattern misses', () => {
    // stripNozzleSuffix happily trims "HF0.4 nozzle" to a label ending in "HF",
    // so detection must agree or the row shows "HF" with no badge.
    expect(mentionsHighFlow('Prusa MK4S HF0.4 nozzle')).toBe(true);
    expect(stripNozzleSuffix('Prusa MK4S HF0.4 nozzle')).toBe('Prusa MK4S HF');
  });

  it('is false for standard profiles', () => {
    expect(mentionsHighFlow('Prusa CORE One 0.4 nozzle')).toBe(false);
    expect(mentionsHighFlow('Phrozen Arco 0.4 nozzle')).toBe(false);
  });

  it('does not match HF embedded inside a longer word', () => {
    expect(mentionsHighFlow('Shelfhorse 0.4 nozzle')).toBe(false);
    expect(mentionsHighFlow('HFX600 0.4 nozzle')).toBe(false);
    expect(mentionsHighFlow('shelfhf')).toBe(false);
  });

  it('matches hyphen- and underscore-delimited forms', () => {
    expect(mentionsHighFlow('MK4-HF-test')).toBe(true);
    expect(mentionsHighFlow('MK4_HF_test')).toBe(true);
  });
});

describe('isProcessProfileCoreOneVariantCompatible', () => {
  // Regression coverage for the issue: a process profile that lists BOTH the
  // standard and HF machine in `compatiblePrinters` was dropped for the
  // standard machine because the old guard joined the whole compatiblePrinters
  // list into one string before testing for "HF" — the joined text mentions
  // HF even though the profile is genuinely dual-compatible.
  it('keeps a dual-compatible profile for BOTH the standard and HF selection', () => {
    const compatiblePrinters = ['Prusa CORE One 0.4 nozzle', 'Prusa CORE One HF 0.4 nozzle'];

    expect(isProcessProfileCoreOneVariantCompatible('0.20mm Standard @CORE One', compatiblePrinters, false))
      .toBe(true);
    expect(isProcessProfileCoreOneVariantCompatible('0.20mm Standard @CORE One', compatiblePrinters, true))
      .toBe(true);
  });

  it('still hides an HF-only profile from the standard machine, and vice versa', () => {
    expect(isProcessProfileCoreOneVariantCompatible(
      '0.20mm Standard @CORE One HF',
      ['Prusa CORE One HF 0.4 nozzle'],
      false,
    )).toBe(false);

    expect(isProcessProfileCoreOneVariantCompatible(
      '0.20mm Standard @CORE One',
      ['Prusa CORE One 0.4 nozzle'],
      true,
    )).toBe(false);
  });

  it('falls back to the profile NAME when compatiblePrinters has no CORE One entries', () => {
    expect(isProcessProfileCoreOneVariantCompatible('0.20mm Standard @CORE One HF', undefined, false)).toBe(false);
    expect(isProcessProfileCoreOneVariantCompatible('0.20mm Standard @CORE One HF', [], true)).toBe(true);
    expect(isProcessProfileCoreOneVariantCompatible('0.20mm Standard @CORE One', undefined, false)).toBe(true);
  });

  it('ignores unrelated compatiblePrinters entries when scoping the variant check', () => {
    // A profile shared across families should not have its CORE One variant
    // decided by an entry belonging to a different printer.
    expect(isProcessProfileCoreOneVariantCompatible(
      'Generic 0.20mm',
      ['Prusa MK4S HF0.4 nozzle', 'Prusa CORE One 0.4 nozzle'],
      false,
    )).toBe(true);
    expect(isProcessProfileCoreOneVariantCompatible(
      'Generic 0.20mm',
      ['Prusa MK4S HF0.4 nozzle', 'Prusa CORE One 0.4 nozzle'],
      true,
    )).toBe(false);
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

  it('documents why callers must scope the set to one nozzle group', () => {
    // Passing a whole multi-nozzle printer collides by construction, which
    // disables trimming for EVERY row — the bug this guard once caused.
    const wholePrinter = buildMachineProfileLabels([
      'Prusa CORE One 0.25 nozzle',
      'Prusa CORE One 0.4 nozzle',
      'Prusa CORE One HF 0.4 nozzle',
    ]);
    expect(wholePrinter.get('Prusa CORE One HF 0.4 nozzle')).toBe('Prusa CORE One HF 0.4 nozzle');

    // Scoped to the 0.4 group, trimming is unique and therefore applies.
    const oneGroup = buildMachineProfileLabels([
      'Prusa CORE One 0.4 nozzle',
      'Prusa CORE One HF 0.4 nozzle',
    ]);
    expect(oneGroup.get('Prusa CORE One 0.4 nozzle')).toBe('Prusa CORE One');
    expect(oneGroup.get('Prusa CORE One HF 0.4 nozzle')).toBe('Prusa CORE One HF');
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
