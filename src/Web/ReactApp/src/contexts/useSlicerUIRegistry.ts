import { useContext } from "react";
import { SlicerUIContext } from "./SlicerUIContextValue";
import type { ISlicerUIRegistry } from "../services/slicer-registry/SlicerUIRegistry";

/**
 * Hook to access the SlicerUIRegistry from anywhere in the app.
 *
 * @throws Error if used outside of SlicerUIProvider
 *
 * @example
 * ```tsx
 * const slicerRegistry = useSlicerUIRegistry();
 * const importComponent = slicerRegistry.getComponent('OrcaSlicer', 'ImportComponent');
 * ```
 */
export const useSlicerUIRegistry = (): ISlicerUIRegistry => {
  const registry = useContext(SlicerUIContext);
  if (!registry) {
    throw new Error("useSlicerUIRegistry must be used within SlicerUIProvider");
  }
  return registry;
};
