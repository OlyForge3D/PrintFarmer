import React from 'react';
import { NozzleIcon, BedIcon } from '@/common/components/icons/MdiIcons';

interface PrintProgressBarProps {
  progress: number | undefined;
  jobName: string | undefined | null;
  isActive: boolean;
  progressRef?: React.RefObject<HTMLDivElement>;
  showInactiveState?: boolean;
  showTemperatures?: boolean;
  hotendTemp?: number | null;
  bedTemp?: number | null;
  hotendTarget?: number | null;
  bedTarget?: number | null;
  isOnline?: boolean;
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
}: PrintProgressBarProps) {
  const progress = rawProgress ?? 0;
  const clampedProgress = Math.max(0, Math.min(100, progress));

  // Job name display logic
  const jobNameDisplay = (() => {
    if (isActive) {
      return jobName || 'Printing...';
    }
    if (showInactiveState) {
      return <span className="italic text-pf-text-tertiary">No active print</span>;
    }
    // For DetailedPrinterCard: show non-breaking space to prevent layout shift
    return jobName || '\u00A0';
  })();

  return (
    <div>
      <div className="flex justify-between text-xs text-pf-text-secondary mb-1">
        <span className="truncate flex-1">{jobNameDisplay}</span>
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
      {showTemperatures && isOnline && (hotendTemp != null || bedTemp != null) && (
        <div className="flex items-center gap-3 mt-1.5 text-xs text-pf-text-secondary">
          {hotendTemp != null && (
            <span className="flex items-center gap-1" title="Hotend temperature">
              <NozzleIcon className="w-3.5 h-3.5" isOn={(hotendTemp ?? 0) > 50} />
              <span>{Math.round(hotendTemp)}°{hotendTarget ? ` / ${Math.round(hotendTarget)}°` : ''}</span>
            </span>
          )}
          {bedTemp != null && (
            <span className="flex items-center gap-1" title="Bed temperature">
              <BedIcon className="w-3.5 h-3.5" isOn={(bedTemp ?? 0) > 35} />
              <span>{Math.round(bedTemp)}°{bedTarget ? ` / ${Math.round(bedTarget)}°` : ''}</span>
            </span>
          )}
        </div>
      )}
    </div>
  );
}
