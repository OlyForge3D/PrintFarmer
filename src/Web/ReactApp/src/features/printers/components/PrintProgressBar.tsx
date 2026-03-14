import React from 'react';
import { NozzleIcon, BedIcon, ArrowRightIcon } from '@/common/components/icons/MdiIcons';

interface PrintProgressBarProps {
  progress: number | undefined;
  jobName: string | undefined | null;
  isActive: boolean;
  progressRef?: React.RefObject<HTMLDivElement | null>;
  showInactiveState?: boolean;
  showTemperatures?: boolean;
  hotendTemp?: number | null;
  bedTemp?: number | null;
  hotendTarget?: number | null;
  bedTarget?: number | null;
  isOnline?: boolean;
  /** Queue indicator shown to the right of the job name, e.g. "2 of 5" */
  queueLabel?: string;
}

export function PrintProgressBar({
  progress: rawProgress,
  jobName,
  isActive,
  progressRef,
  showInactiveState = false,
  showTemperatures = false,
  hotendTemp,
  bedTemp,
  hotendTarget,
  bedTarget,
  isOnline = true,
  queueLabel,
}: PrintProgressBarProps) {
  const progress = rawProgress ?? 0;
  const clampedProgress = Math.max(0, Math.min(100, progress));

  // Job name display logic (path stripping done server-side in PrinterStatusCache)
  const jobNameDisplay = (() => {
    if (isActive) {
      return jobName || 'Printing...';
    }
    if (showInactiveState) {
      return <span className="italic text-pf-text-tertiary">No active print</span>;
    }
    return jobName || '\u00A0';
  })();

  return (
    <div>
      <div className="flex justify-between text-xs text-pf-text-secondary mb-1">
        <span className="truncate flex-1">{jobNameDisplay}</span>
        {queueLabel && (
          <span className="shrink-0 ml-2 text-pf-text-tertiary" title="Queued jobs for this printer">
            {queueLabel}
          </span>
        )}
        {isActive && <span className="font-semibold ml-2">{Math.round(progress)}%</span>}
      </div>
      <div
        className="w-full bg-pf-border-dark rounded-full h-2 overflow-hidden"
        role="progressbar"
        aria-label="Print progress"
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={isActive ? Math.round(clampedProgress) : 0}
      >
        <div
          ref={progressRef}
          className="bg-pf-success-bg h-2 rounded-full transition-all duration-300"
          style={{ width: `${isActive ? clampedProgress : 0}%` }}
        >
          <span className="sr-only">Print progress: {isActive ? Math.round(clampedProgress) : 0}%</span>
        </div>
      </div>
      {/* Temperature readouts */}
      {showTemperatures && isOnline && (
        <div className="grid grid-cols-2 gap-3 mt-3 text-xs text-pf-text-secondary">
          <span className="flex items-center gap-1" title="Hotend temperature">
            <NozzleIcon className={`w-3.5 h-3.5 ${hotendTemp != null ? 'text-pf-error' : ''}`} isOn={(hotendTemp ?? 0) > 50} />
            <span>{hotendTemp != null ? `${Math.round(hotendTemp)}°` : '--°'}</span>
            {hotendTarget != null && hotendTarget > 0 && (
              <>
                <ArrowRightIcon className="w-2.5 h-2.5" />
                <span>{Math.round(hotendTarget)}°</span>
              </>
            )}
          </span>
          <span className="flex items-center gap-1" title="Bed temperature">
            <BedIcon className={`w-3.5 h-3.5 ${bedTemp != null ? 'text-pf-accent' : ''}`} isOn={(bedTemp ?? 0) > 35} />
            <span>{bedTemp != null ? `${Math.round(bedTemp)}°` : '--°'}</span>
            {bedTarget != null && bedTarget > 0 && (
              <>
                <ArrowRightIcon className="w-2.5 h-2.5" />
                <span>{Math.round(bedTarget)}°</span>
              </>
            )}
          </span>
        </div>
      )}
    </div>
  );
}
