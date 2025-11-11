import { createContext } from 'react';
import type { ISlicerUIRegistry } from '../services/slicer-registry/SlicerUIRegistry';

/**
 * React Context for accessing the SlicerUIRegistry.
 * Provides access to slicer-specific UI components and services throughout the app.
 */
export const SlicerUIContext = createContext<ISlicerUIRegistry | null>(null);
