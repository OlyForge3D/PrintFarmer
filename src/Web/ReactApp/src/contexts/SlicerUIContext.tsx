import React, { ReactNode } from 'react';
import { SlicerUIRegistry, type ISlicerUIRegistry } from '../services/slicer-registry/SlicerUIRegistry';
import { SlicerUIContext } from './SlicerUIContext';

/**
 * Props for SlicerUIProvider
 */
interface SlicerUIProviderProps {
  children: ReactNode;
  registry?: ISlicerUIRegistry;
}

/**
 * SlicerUIProvider component
 *
 * Wraps the application with access to the SlicerUIRegistry.
 * If no registry is provided, a new one is created.
 *
 * @example
 * ```tsx
 * <SlicerUIProvider>
 *   <App />
 * </SlicerUIProvider>
 * ```
 */
export const SlicerUIProvider: React.FC<SlicerUIProviderProps> = ({ children, registry }) => {
  const registryInstance = registry ?? new SlicerUIRegistry();

  return <SlicerUIContext.Provider value={registryInstance}>{children}</SlicerUIContext.Provider>;
};
