/**
 * Slicer Status Bar Component
 * Bottom status bar matching OrcaSlicer's interface
 */
import React from 'react';
import { Button } from '@/common/components/ui';
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
  /** Custom label for the slice button, e.g. "Slice Plate 2 (3 models)". */
  sliceButtonLabel?: string;
  /** One-line disclosure shown near the slice button, e.g. when other plates have models. */
  sliceNote?: string;
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
  sliceButtonLabel,
  sliceNote,
}) => {
  const hasSliceInfo = slicesRemaining !== undefined && slicesTotal !== undefined;

  return (
    <div className="flex items-center justify-between px-4 py-2 bg-pf-bg-1 border-t border-pf-border shrink-0">
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
        {sliceNote && (
          <span className="text-xs text-pf-text-secondary" data-testid="slice-note">
            {sliceNote}
          </span>
        )}
        {hasSliceInfo && (
          <div className="flex items-center gap-2 text-sm text-pf-text-secondary">
            <span>
              {slicesRemaining} / {slicesTotal} left
            </span>
            <Button
              type="button"
              variant="subtle"
              title="Slice information"
              className="p-1"
            >
              <InfoIcon className="w-4 h-4" />
            </Button>
          </div>
        )}
        
        <Button
          type="button"
          variant="primary"
          onClick={onSlice}
          disabled={!canSlice || slicing}
          className="px-6 py-1.5"
        >
          {slicing ? 'Slicing...' : (sliceButtonLabel ?? 'Slice')}
        </Button>
      </div>
    </div>
  );
};

export default SlicerStatusBar;
