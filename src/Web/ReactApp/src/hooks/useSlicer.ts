import { useContext } from 'react';
import { SlicerContext, SlicerContextValue } from '@/contexts/SlicerTypes';

/**
 * Hook to access slicer availability state
 * 
 * @example
 * ```tsx
 * const { isSlicerAvailable, workerCount } = useSlicer();
 * if (!isSlicerAvailable) {
 *   return <div>Slicing is not available</div>;
 * }
 * ```
 */
export const useSlicer = (): SlicerContextValue => {
  const context = useContext(SlicerContext);
  if (context === undefined) {
    throw new Error('useSlicer must be used within a SlicerProvider');
  }
  return context;
};
