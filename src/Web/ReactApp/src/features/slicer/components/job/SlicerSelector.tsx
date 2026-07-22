import React from 'react';
import clsx from 'clsx';
import { Button } from '@/common/components/ui';
import { GearIcon } from '@/common/components/icons/MdiIcons';
import { getSlicerIconSrc } from '@/common/utils/slicerEngineIcon';
import type { SlicerEngineOption } from './types';

interface SlicerSelectorProps {
  /** Currently selected slicer ID (1=OrcaSlicer, 2=PrusaSlicer) */
  selectedSlicerId: number;
  /** Callback when slicer selection changes */
  onSlicerChange: (slicerId: number) => void;
  /** Available slicer engine options */
  engineOptions: SlicerEngineOption[];
  /** Optional CSS class name */
  className?: string;
}

/**
 * Parse engine label into name + version.
 * e.g. "OrcaSlicer v2.3.1" → { name: "OrcaSlicer", version: "v2.3.1" }
 */
function parseLabel(label: string): { name: string; version?: string } {
  const match = label.match(/^(.+?)\s+(v\d.*)$/i);
  if (match) return { name: match[1].trim(), version: match[2].trim() };
  return { name: label };
}

/**
 * Slicer engine card selector with logos.
 * Displays each available slicer as a selectable card with its icon, name, and version.
 */
export const SlicerSelector: React.FC<SlicerSelectorProps> = ({
  selectedSlicerId,
  onSlicerChange,
  engineOptions,
  className,
}) => {
  return (
    <div className={clsx('bg-pf-panel border border-pf-border rounded-lg p-2.5', className)}>
      <label className="block text-sm font-semibold text-pf-text-primary mb-1.5">Slicer Engine</label>
      <div className="flex flex-col gap-2">
        {engineOptions.map(opt => {
          const isSelected = opt.value === selectedSlicerId;
          const iconSrc = getSlicerIconSrc(opt.value);
          const { name, version } = parseLabel(opt.label);

          return (
            <Button
              key={opt.value}
              variant="unstyled"
              type="button"
              onClick={() => onSlicerChange(opt.value)}
              aria-pressed={isSelected}
              className={clsx(
                'w-full px-3 py-1.5 rounded-lg border-2 transition-all cursor-pointer',
                'hover:border-pf-accent hover:bg-pf-accent-bg/10',
                'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-pf-accent',
                isSelected
                  ? 'border-pf-accent bg-pf-accent-bg/15 shadow-sm'
                  : 'border-pf-border bg-pf-bg-1',
              )}
            >
              <span className="flex w-full items-center gap-3 text-left">
                {iconSrc ? (
                  <img
                    src={iconSrc}
                    alt=""
                    className="h-8 w-8 shrink-0 rounded-lg object-contain"
                  />
                ) : (
                  <span className="h-8 w-8 shrink-0 flex items-center justify-center rounded-lg bg-pf-bg-2" aria-hidden="true">
                    <GearIcon className="w-5 h-5 text-pf-text-muted" />
                  </span>
                )}
                <span className="min-w-0">
                  <span className={clsx(
                    'block text-sm leading-tight font-semibold truncate',
                    isSelected ? 'text-pf-accent' : 'text-pf-text-primary',
                  )}>
                    {name}
                  </span>
                  {version && (
                    <span className="block text-xs leading-tight text-pf-text-muted truncate mt-0.5">{version}</span>
                  )}
                </span>
              </span>
            </Button>
          );
        })}
      </div>
    </div>
  );
};

export default SlicerSelector;
