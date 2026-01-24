/**
 * Slicer Status Bar Component
 * Bottom status bar matching OrcaSlicer's interface
 */
import React from 'react';
import { InfoIcon } from './SlicerToolbarIcons';

export interface SlicerStatusBarProps {
  objectCount: number;
  bedWidth: number;
  bedDepth: number;
  bedHeight: number;
  slicesRemaining?: number;
  slicesTotal?: number;
  onSlice?: () => void;
  slicing?: boolean;
  canSlice?: boolean;
}

export const SlicerStatusBar: React.FC<SlicerStatusBarProps> = ({
  objectCount,
  bedWidth,
  bedDepth,
  bedHeight,
  slicesRemaining,
  slicesTotal,
  onSlice,
  slicing = false,
  canSlice = true,
}) => {
  const hasSliceInfo = slicesRemaining !== undefined && slicesTotal !== undefined;

  return (
    <div className="flex items-center justify-between px-4 py-2 bg-pf-bg-1 border-t border-pf-border">
      {/* Left side: Object info and bed dimensions */}
      <div className="flex items-center gap-6 text-sm text-pf-text-secondary">
        <span className="font-medium">
          {objectCount} object{objectCount !== 1 ? 's' : ''}
        </span>
        <span>
          {bedWidth} x {bedDepth} x {bedHeight} mm
        </span>
      </div>

      {/* Right side: Slice info and button */}
      <div className="flex items-center gap-4">
        {hasSliceInfo && (
          <div className="flex items-center gap-2 text-sm text-pf-text-secondary">
            <span>
              {slicesRemaining} / {slicesTotal} left
            </span>
            <button
              title="Slice information"
              className="p-1 rounded hover:bg-pf-bg-2 transition-colors"
            >
              <InfoIcon className="w-4 h-4" />
            </button>
          </div>
        )}
        
        <button
          onClick={onSlice}
          disabled={!canSlice || slicing}
          className={`
            px-6 py-1.5 rounded-md font-medium text-sm transition-colors
            ${canSlice && !slicing
              ? 'bg-pf-accent text-white hover:bg-pf-accent-hover'
              : 'bg-pf-disabled text-pf-text-secondary cursor-not-allowed'
            }
          `}
        >
          {slicing ? 'Slicing...' : 'Slice'}
        </button>
      </div>
    </div>
  );
};

export default SlicerStatusBar;
