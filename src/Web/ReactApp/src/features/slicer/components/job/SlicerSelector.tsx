import React from 'react';
import clsx from 'clsx';
import { Button } from '@/common/components/ui';
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
    <div className={clsx('bg-pf-panel border border-pf-border rounded-lg p-4', className)}>
      <label className="block text-sm font-semibold text-pf-text-primary mb-3">Slicer Engine</label>
      <div className="flex flex-col gap-3">
        {engineOptions.map(opt => {
          const isSelected = opt.value === selectedSlicerId;
          const iconSrc = getSlicerIconSrc(opt.value);
          const { name, version } = parseLabel(opt.label);

          return (
            <Button
              key={opt.value}
              variant="unstyled"
              onClick={() => onSlicerChange(opt.value)}
              aria-pressed={isSelected}
              className={clsx(
                'flex w-full items-center gap-3 px-4 py-3 rounded-lg border-2 transition-all cursor-pointer',
                'hover:border-pf-accent hover:bg-pf-accent-bg/10',
                'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-pf-accent',
                isSelected
                  ? 'border-pf-accent bg-pf-accent-bg/15 shadow-sm'
                  : 'border-pf-border bg-pf-bg-1',
              )}
            >
              {iconSrc ? (
                <img
                  src={iconSrc}
                  alt=""
                  className="h-10 w-10 shrink-0 rounded"
                />
              ) : (
                <span className="h-10 w-10 shrink-0 flex items-center justify-center text-2xl" role="img" aria-hidden="true">
                  🔪
                </span>
              )}
              <div className="text-left min-w-0">
                <span className={clsx(
                  'block text-sm font-semibold truncate',
                  isSelected ? 'text-pf-accent' : 'text-pf-text-primary',
                )}>
                  {name}
                </span>
                {version && (
                  <span className="block text-xs text-pf-text-muted truncate">{version}</span>
                )}
              </div>
              {isSelected && (
                <svg className="h-5 w-5 shrink-0 text-pf-accent ml-auto" viewBox="0 0 20 20" fill="currentColor" aria-hidden="true">
                  <path fillRule="evenodd" d="M16.704 4.153a.75.75 0 01.143 1.052l-8 10.5a.75.75 0 01-1.127.075l-4.5-4.5a.75.75 0 011.06-1.06l3.894 3.893 7.48-9.817a.75.75 0 011.05-.143z" clipRule="evenodd" />
                </svg>
              )}
            </Button>
          );
        })}
      </div>
    </div>
  );
};

export default SlicerSelector;
