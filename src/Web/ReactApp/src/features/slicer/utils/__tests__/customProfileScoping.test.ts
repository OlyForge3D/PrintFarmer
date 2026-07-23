import { describe, it, expect } from 'vitest';
import {
  classifyCustomProfileScope,
  legacyMachineProfileMatchesPrinter,
  legacyProcessProfileMatchesMachine,
} from '../customProfileScoping';

describe('classifyCustomProfileScope', () => {
  const QIDI_MODEL = 'model-qidi-plus-4';
  const RATRIG_MODEL = 'model-ratrig-vcore-4';

  it('returns match when printerModelId equals the selected model', () => {
    expect(
      classifyCustomProfileScope({ printerModelId: QIDI_MODEL }, QIDI_MODEL),
    ).toBe('match');
  });

  it('returns mismatch when printerModelId is a different model (the Qidi-on-RatRig bug)', () => {
    expect(
      classifyCustomProfileScope({ printerModelId: QIDI_MODEL }, RATRIG_MODEL),
    ).toBe('mismatch');
  });

  it('returns mismatch when scoped profile has no selected model id yet', () => {
    expect(classifyCustomProfileScope({ printerModelId: QIDI_MODEL }, null)).toBe('mismatch');
    expect(classifyCustomProfileScope({ printerModelId: QIDI_MODEL }, undefined)).toBe('mismatch');
    expect(classifyCustomProfileScope({ printerModelId: QIDI_MODEL }, '')).toBe('mismatch');
  });

  it('returns unscoped for legacy profiles with no printerModelId', () => {
    expect(classifyCustomProfileScope({ printerModelId: null }, QIDI_MODEL)).toBe('unscoped');
    expect(classifyCustomProfileScope({ printerModelId: undefined }, QIDI_MODEL)).toBe('unscoped');
    expect(classifyCustomProfileScope({ printerModelId: '' }, QIDI_MODEL)).toBe('unscoped');
  });
});

describe('legacyMachineProfileMatchesPrinter', () => {
  it('matches via printer_model embedded in rawJson', () => {
    const profile = {
      name: 'My Custom',
      rawJson: JSON.stringify({ printer_model: 'RatRig V-Core 4' }),
    };
    expect(legacyMachineProfileMatchesPrinter(profile, 'RatRig', 'V-Core 4.0')).toBe(true);
  });

  it('does not match a different printer via printer_model', () => {
    const profile = {
      name: 'Qidi Plus 4 0.4 nozzle',
      rawJson: JSON.stringify({ printer_model: 'Qidi Plus 4' }),
    };
    expect(legacyMachineProfileMatchesPrinter(profile, 'RatRig', 'V-Core 4.0')).toBe(false);
  });

  it('falls back to name matching when rawJson lacks printer_model', () => {
    const profile = { name: 'RatRig V-Core custom', rawJson: JSON.stringify({ foo: 'bar' }) };
    expect(legacyMachineProfileMatchesPrinter(profile, 'RatRig', 'V-Core 4.0')).toBe(true);
  });

  it('allows the profile when there is no printer context', () => {
    const profile = { name: 'Anything' };
    expect(legacyMachineProfileMatchesPrinter(profile, undefined, undefined)).toBe(true);
  });
});

describe('legacyProcessProfileMatchesMachine', () => {
  const MACHINE = 'RatRig V-Core 4 HYBRID 400 0.4 nozzle';

  it('matches when compatible_printers includes the selected machine', () => {
    const profile = { rawJson: JSON.stringify({ compatible_printers: [MACHINE] }) };
    expect(legacyProcessProfileMatchesMachine(profile, MACHINE)).toBe(true);
  });

  it('does not match a different machine', () => {
    const profile = { rawJson: JSON.stringify({ compatible_printers: ['Qidi Plus 4 0.4 nozzle'] }) };
    expect(legacyProcessProfileMatchesMachine(profile, MACHINE)).toBe(false);
  });

  it('hides profiles with no machine selected, no rawJson, or unparseable rawJson', () => {
    expect(legacyProcessProfileMatchesMachine({ rawJson: JSON.stringify({ compatible_printers: [MACHINE] }) }, '')).toBe(false);
    expect(legacyProcessProfileMatchesMachine({ rawJson: undefined }, MACHINE)).toBe(false);
    expect(legacyProcessProfileMatchesMachine({ rawJson: '{not json' }, MACHINE)).toBe(false);
  });
});
