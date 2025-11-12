import React, { ReactNode, useEffect, useMemo } from 'react';
import { SlicerUIRegistry, type ISlicerUIRegistry } from '../services/slicer-registry/SlicerUIRegistry';
import { registerAllSlicerUI } from '../services/slicer-registry/registerSlicerUI';
import { SlicerUIContext } from './SlicerUIContextValue';

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
 * Automatically registers all available slicer UI libraries on mount.
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
    const registryInstance = useMemo(() => registry ?? new SlicerUIRegistry(), [registry]);

    useEffect(() => {
        // Register all slicer UI libraries on provider mount
        registerAllSlicerUI(registryInstance);
    }, [registryInstance]);

    return <SlicerUIContext.Provider value={registryInstance}>{children}</SlicerUIContext.Provider>;
};
