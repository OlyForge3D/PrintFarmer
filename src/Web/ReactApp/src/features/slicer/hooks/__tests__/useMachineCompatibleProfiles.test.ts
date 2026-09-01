import { describe, expect, it } from 'vitest';
import type {
  CustomProfile,
  OrcaMachineProfile,
  OrcaProcessProfile,
} from '@/services/slicerProfilesService';
import {
  filterCustomMachineProfiles,
  filterCustomProcessProfiles,
  isProcessProfileCompatibleWithMachine,
  mergeCustomProfilesIntoVisibleList,
} from '@/features/slicer/hooks/useMachineCompatibleProfiles';

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

const customProfile = (
  id: string,
  profileType: CustomProfile['profileType'],
  printerModelId?: string | null,
): CustomProfile => ({
  id,
  name: id,
  profileType,
  printerModelId,
  isSystem: false,
  createdAt: '2026-09-01T00:00:00Z',
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

describe('custom-profile list helpers', () => {
  it('keeps only custom machine profiles scoped to the selected catalog model', () => {
    const profiles = [
      customProfile('selected', 'machine', 'model-1'),
      customProfile('other', 'machine', 'model-2'),
    ];

    expect(filterCustomMachineProfiles(profiles, 'model-1', 'Prusa', 'CORE One'))
      .toEqual([profiles[0]]);
  });

  it('keeps only custom process profiles scoped to the selected catalog model', () => {
    const profiles = [
      customProfile('selected', 'process', 'model-1'),
      customProfile('other', 'process', 'model-2'),
    ];

    expect(filterCustomProcessProfiles(profiles, 'model-1', 'Prusa CORE One 0.4 nozzle'))
      .toEqual([profiles[0]]);
  });

  it('places custom profiles before system profiles in the visible list', () => {
    expect(mergeCustomProfilesIntoVisibleList(['system'], ['custom']))
      .toEqual(['custom', 'system']);
  });
});
