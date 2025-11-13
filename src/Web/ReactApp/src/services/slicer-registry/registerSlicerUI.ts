/**
 * Register Slicer UI
 *
 * Initializes and registers all slicer UI libraries with the SlicerUIRegistry.
 * This module dynamically imports slicer UI exports from npm workspace packages
 * and registers them so they can be discovered and used by the React app.
 * 
 * Slicer packages are managed as npm workspaces, allowing them to be versioned
 * and developed independently while being included in the build.
 */

import type { ISlicerUIRegistry, SlicerUIExports } from "./SlicerUIRegistry";

/**
 * Register OrcaSlicer UI
 * 
 * Dynamically imports OrcaSlicer UI from the workspace package and registers
 * it with the SlicerUIRegistry. Handles load failures gracefully.
 */
export function registerOrcaSlicerUI(registry: ISlicerUIRegistry): void {
  import("@farm/slicers-orcaslicer-v2_3_1")
    .then((module) => {
      const orcaExports: SlicerUIExports = {
        slicerName: "OrcaSlicer",
        slicerVersion: "2.3.1",
        ImportComponent: module.OrcaImportWizard,
        profilesService: module.orcaProfilesService,
        types: {},
      };

      registry.registerUI("OrcaSlicer", "2.3.1", orcaExports);
      console.info("[registerSlicerUI] Registered OrcaSlicer v2.3.1");
    })
    .catch((err) => {
      console.error("[registerSlicerUI] Failed to register OrcaSlicer:", err);
    });
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
