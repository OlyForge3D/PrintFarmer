/**
 * Register Slicer UI
 *
 * Initializes and registers all slicer UI libraries with the SlicerUIRegistry.
 * This module imports slicer UI exports and registers them so they can be
 * discovered and used dynamically by the React app.
 */

import type { ISlicerUIRegistry, SlicerUIExports } from './SlicerUIRegistry';

/**
 * Register OrcaSlicer UI
 */
export function registerOrcaSlicerUI(registry: ISlicerUIRegistry): void {
  // Import OrcaSlicer exports from the library
  // Note: We import these here to ensure they're loaded before registration
  import('@farm/slicers-orcaslicer-v2_3_x').then((module) => {
    const orcaExports: SlicerUIExports = {
      slicerName: 'OrcaSlicer',
      slicerVersion: '2.3.1',
      ImportComponent: module.OrcaImportWizard,
      profilesService: module.orcaProfilesService,
      types: {},
    };

    registry.registerUI('OrcaSlicer', '2.3.1', orcaExports);
    console.info('[registerSlicerUI] Registered OrcaSlicer v2.3.1');
  }).catch((err) => {
    console.error('[registerSlicerUI] Failed to register OrcaSlicer:', err);
  });
}

/**
 * Register PrusaSlicer UI
 */
export function registerPrusaSlicerUI(registry: ISlicerUIRegistry): void {
  // Import PrusaSlicer exports from the library
  import('@farm/slicers-prasalicer-v2_9_x').then((module) => {
    const prusaExports: SlicerUIExports = {
      slicerName: 'PrusaSlicer',
      slicerVersion: '2.9.x',
      ImportComponent: module.PrusaImportWizard,
      profilesService: module.prusaProfilesService,
      types: {},
    };

    registry.registerUI('PrusaSlicer', '2.9.x', prusaExports);
    console.info('[registerSlicerUI] Registered PrusaSlicer v2.9.x');
  }).catch((err) => {
    console.error('[registerSlicerUI] Failed to register PrusaSlicer:', err);
  });
}

/**
 * Register all slicer UI libraries
 *
 * This is called once during app initialization to set up all available slicers.
 */
export function registerAllSlicerUI(registry: ISlicerUIRegistry): void {
  registerOrcaSlicerUI(registry);
  registerPrusaSlicerUI(registry);
  // Future: registerCrealitySlicerUI(registry);
}
