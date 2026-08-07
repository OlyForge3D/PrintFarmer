import React, { Suspense } from 'react';
import { render, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

const importWizardModuleLoad = vi.hoisted(() => vi.fn());

vi.mock('@/features/slicer/orca/components/OrcaImportWizard', () => {
  importWizardModuleLoad();
  return {
    OrcaImportWizard: () => null,
  };
});

vi.mock('@/features/slicer/orca/services/orcaProfilesService', () => ({
  orcaProfilesService: {},
}));

import { registerOrcaSlicerUI } from '../registerSlicerUI';
import { SlicerUIRegistry } from '../SlicerUIRegistry';

describe('registerOrcaSlicerUI', () => {
  it('registers the stable OrcaSlicer 2.4.2 UI without loading its import wizard', async () => {
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
    expect(importWizardModuleLoad).not.toHaveBeenCalled();

    const ImportComponent = registry.getUI('OrcaSlicer', '2.4.2')?.ImportComponent;
    expect(ImportComponent).toBeDefined();

    render(
      React.createElement(
        Suspense,
        { fallback: null },
        React.createElement(ImportComponent!),
      ),
    );

    await waitFor(() => expect(importWizardModuleLoad).toHaveBeenCalledTimes(1));
  });
});
