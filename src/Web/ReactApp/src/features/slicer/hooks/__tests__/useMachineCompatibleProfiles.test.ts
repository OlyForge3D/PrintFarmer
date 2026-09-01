import { describe, expect, it } from 'vitest';
import type { OrcaMachineProfile, OrcaProcessProfile } from '@/services/slicerProfilesService';
import { isProcessProfileCompatibleWithMachine } from '@/features/slicer/hooks/useMachineCompatibleProfiles';

const processProfile = (
  name: string,
  compatiblePrinters?: string[] | null,
): OrcaProcessProfile => ({
  name,
  quality: 'Standard',
  layerHeight: 0.2,
  infillPercentage: 15,
  printSpeed: 60,
  supports: false,
  compatible_printers: compatiblePrinters,
});

const machineProfile = (name: string, isHighFlowNozzle?: boolean): OrcaMachineProfile => ({
  name,
  manufacturer: 'Prusa',
  isHighFlowNozzle,
});

describe('isProcessProfileCompatibleWithMachine', () => {
  it('requires exact compatible_printers membership when the list is present', () => {
    const selected = machineProfile('Prusa MK4S 0.4 nozzle');

    expect(isProcessProfileCompatibleWithMachine(
      processProfile('0.20mm Standard @MK4S', ['Prusa MK4S 0.4 nozzle']),
      selected,
    )).toBe(true);
    expect(isProcessProfileCompatibleWithMachine(
      processProfile('0.20mm Standard @MK4S', ['Prusa MK4S HF0.4 nozzle']),
      selected,
    )).toBe(false);
  });

  it.each([undefined, null, []])('treats %s compatibility as universal', (compatiblePrinters) => {
    expect(isProcessProfileCompatibleWithMachine(
      processProfile('0.20mm Standard @CORE One', compatiblePrinters),
      machineProfile('Prusa CORE One 0.4 nozzle'),
    )).toBe(true);
  });

  it('keeps dual-compatible CORE One profiles for both standard and HF machines', () => {
    const profile = processProfile('0.20mm Standard @CORE One', [
      'Prusa CORE One 0.4 nozzle',
      'Prusa CORE One HF 0.4 nozzle',
    ]);

    expect(isProcessProfileCompatibleWithMachine(
      profile,
      machineProfile('Prusa CORE One 0.4 nozzle', false),
    )).toBe(true);
    expect(isProcessProfileCompatibleWithMachine(
      profile,
      machineProfile('Prusa CORE One HF 0.4 nozzle', true),
    )).toBe(true);
  });

  it('rejects an HF-only CORE One profile for the standard machine', () => {
    expect(isProcessProfileCompatibleWithMachine(
      processProfile('0.20mm Standard @CORE One HF', ['Prusa CORE One HF 0.4 nozzle']),
      machineProfile('Prusa CORE One 0.4 nozzle', false),
    )).toBe(false);
  });
});
