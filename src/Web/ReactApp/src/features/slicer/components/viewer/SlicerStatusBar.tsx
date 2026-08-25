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
    // Issue #1974: at narrow (390px) viewports there isn't room for the left
    // (object/bed info) and right (slice note/button) groups on one line. Without
    // `flex-wrap` the row squeezed both groups via flexbox shrink instead of
    // wrapping, and unprotected text spans (no `shrink-0`/`whitespace-nowrap`)
    // collapsed into a narrow, barely-readable vertical column next to the
    // slice button. `flex-wrap` lets the right group drop to its own row
    // instead, and `min-w-0` on that group lets its slice-note text wrap
    // normally (by word) across the full row width rather than being squeezed.
    <div className="flex flex-wrap items-center justify-between gap-x-2 gap-y-1 px-4 py-2 bg-pf-bg-1 border-t border-pf-border shrink-0">
      {/* Left side: Object info and bed dimensions */}
      <div className="flex items-center gap-6 text-sm text-pf-text-secondary whitespace-nowrap">
        <span className="font-medium">
          {objectCount} object{objectCount !== 1 ? 's' : ''}
        </span>
        <span>
          {bedWidth} x {bedDepth} x {bedHeight} mm
        </span>
      </div>

      {/* Right side: Slice info and button */}
      <div className="flex flex-wrap items-center justify-end gap-x-4 gap-y-1 min-w-0">
        {sliceNote && (
          <span
            className="w-full text-right text-xs text-pf-text-secondary sm:w-auto sm:text-left"
            data-testid="slice-note"
          >
            {sliceNote}
          </span>
        )}
        {hasSliceInfo && (
          <div className="flex shrink-0 items-center gap-2 text-sm text-pf-text-secondary">
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
          className="shrink-0 px-6 py-1.5"
        >
          {slicing ? 'Slicing...' : (sliceButtonLabel ?? 'Slice')}
        </Button>
      </div>
    </div>
  );
};

export default SlicerStatusBar;
