/**
 * Register Slicer UI
 *
 * Initializes and registers all slicer UI libraries with the SlicerUIRegistry.
 * Slicer UI components are now integrated directly into the ReactApp.
 */

/* eslint-disable local/pf-no-unguarded-console */
import type React from 'react';
import { lazyWithPreload } from '@/common/utils/lazyWithPreload';
import { orcaProfilesService } from '@/features/slicer/orca/services/orcaProfilesService';
import type { ISlicerUIRegistry, SlicerUIExports } from './SlicerUIRegistry';

type OrcaImportWizardComponent = typeof import(
  '@/features/slicer/orca/components/OrcaImportWizard'
)['OrcaImportWizard'];

const OrcaImportWizard = lazyWithPreload<
  React.ComponentProps<OrcaImportWizardComponent>,
  OrcaImportWizardComponent
>(
  () => import('@/features/slicer/orca/components/OrcaImportWizard')
    .then((module) => ({ default: module.OrcaImportWizard })),
);

/**
 * Register OrcaSlicer UI
 * 
 * Dynamically imports OrcaSlicer UI from the workspace package and registers
 * it with the SlicerUIRegistry. Handles load failures gracefully.
 */
export function registerOrcaSlicerUI(registry: ISlicerUIRegistry): void {
  try {
    const orcaExports: SlicerUIExports = {
      slicerName: "OrcaSlicer",
      slicerVersion: "2.4.2",
      ImportComponent: OrcaImportWizard,
      profilesService: orcaProfilesService,
      types: {},
    };

    registry.registerUI("OrcaSlicer", "2.4.2", orcaExports);
    console.info("[registerSlicerUI] Registered OrcaSlicer v2.4.2");
  } catch (err) {
    console.error("[registerSlicerUI] Failed to register OrcaSlicer:", err);
  }
}

/**
 * Register PrusaSlicer UI
 * 
 * Dynamically imports PrusaSlicer UI from the workspace package.
 * This is a placeholder for future PrusaSlicer support.
 */
// export function registerPrusaSlicerUI(registry: ISlicerUIRegistry): void {
//   import("@farm/slicers-prasalicer-v2_9_x")
//     .then((module) => {
//       const prusaExports: SlicerUIExports = {
//         slicerName: "PrusaSlicer",
//         slicerVersion: "2.9.x",
//         ImportComponent: module.PrusaImportWizard,
//         profilesService: module.prusaProfilesService,
//         types: {},
//       };
//
//       registry.registerUI("PrusaSlicer", "2.9.x", prusaExports);
//       console.info("[registerSlicerUI] Registered PrusaSlicer v2.9.x");
//     })
//     .catch((err) => {
//       console.error("[registerSlicerUI] Failed to register PrusaSlicer:", err);
//     });
// }

/**
 * Register all slicer UI libraries
 *
 * Called once during app initialization to set up all available slicers.
 * Registers OrcaSlicer first; PrusaSlicer support will be added when ready.
 */
export function registerAllSlicerUI(registry: ISlicerUIRegistry): void {
  registerOrcaSlicerUI(registry);
  // registerPrusaSlicerUI(registry);
  // Future: registerCrealitySlicerUI(registry);
}
