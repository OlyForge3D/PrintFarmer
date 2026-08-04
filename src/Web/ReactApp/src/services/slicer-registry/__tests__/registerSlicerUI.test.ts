import { describe, expect, it, vi } from 'vitest';
import { registerOrcaSlicerUI } from '../registerSlicerUI';
import { SlicerUIRegistry } from '../SlicerUIRegistry';

vi.mock('@/features/slicer/orca', () => ({
  OrcaImportWizard: () => null,
  orcaProfilesService: {},
}));

describe('registerOrcaSlicerUI', () => {
  it('registers the stable OrcaSlicer 2.4.2 UI', () => {
    const registry = new SlicerUIRegistry();

    registerOrcaSlicerUI(registry);

    expect(registry.listRegistered()).toEqual([
      { name: 'OrcaSlicer', version: '2.4.2' },
    ]);
    expect(registry.getUI('OrcaSlicer', '2.4.2')).toMatchObject({
      slicerName: 'OrcaSlicer',
      slicerVersion: '2.4.2',
    });
    expect(registry.getUI('OrcaSlicer', '2.4.0')).toBeNull();
  });
});
