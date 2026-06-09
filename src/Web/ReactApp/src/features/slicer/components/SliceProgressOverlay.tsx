import { useCallback } from 'react';
import { Button } from '@/common/components/ui';
import { DownloadIcon } from '@/common/components/icons/MdiIcons';
import { getApiBaseUrl } from '@/common/utils/apiUrlHelpers';
import { sliceJobService } from '@/services/sliceJobService';
import type { SliceJobProgressState } from '@/features/slicer/hooks/useSliceJobProgress';

interface SliceProgressOverlayProps {
  jobId: string;
  progress: SliceJobProgressState;
  onNewJob: () => void;
  onRetry: () => void;
}

/**
 * Full-viewport overlay shown on the 3D workspace during slicing.
 * Inspired by OrcaSlicer's progress dialog — prominent, centered, 
 * with real-time progress ring and status details.
 */
export function SliceProgressOverlay({ jobId, progress, onNewJob, onRetry }: SliceProgressOverlayProps) {
  const isCompleted = progress.status === 'Completed';
  const isFailed = progress.status === 'Failed';
  const isCancelled = progress.status === 'Cancelled';
  const isTerminal = isCompleted || isFailed || isCancelled;
  const percent = isCompleted ? 100 : progress.progressPercent;

  const handleDownload = useCallback(() => {
    window.open(`${getApiBaseUrl()}/artifacts/job/${jobId}`, '_blank');
  }, [jobId]);

  // SVG progress ring dimensions
  const size = 160;
  const strokeWidth = 10;
  const radius = (size - strokeWidth) / 2;
  const circumference = 2 * Math.PI * radius;
  const offset = circumference - (percent / 100) * circumference;

  const ringColor = isFailed
    ? 'stroke-pf-error'
    : isCompleted
      ? 'stroke-pf-success'
      : 'stroke-pf-accent';

  return (
    <div className="absolute inset-0 z-30 flex items-center justify-center bg-black/60 backdrop-blur-sm rounded-lg">
      <div className="flex flex-col items-center gap-4 p-8 max-w-sm text-center">

        {/* Progress ring */}
        <div className="relative">
          <svg width={size} height={size} className="transform -rotate-90">
            {/* Background track */}
            <circle
              cx={size / 2}
              cy={size / 2}
              r={radius}
              fill="none"
              strokeWidth={strokeWidth}
              className="stroke-white/10"
            />
            {/* Progress arc */}
            <circle
              cx={size / 2}
              cy={size / 2}
              r={radius}
              fill="none"
              strokeWidth={strokeWidth}
              strokeLinecap="round"
              strokeDasharray={circumference}
              strokeDashoffset={offset}
              className={`${ringColor} transition-[stroke-dashoffset] duration-500 ease-out`}
            />
          </svg>
          {/* Center label */}
          <div className="absolute inset-0 flex flex-col items-center justify-center">
            {!isTerminal && (
              <>
                <span className="text-3xl font-bold text-white tabular-nums">{percent}%</span>
                <span className="text-xs text-white/60 mt-0.5">slicing</span>
              </>
            )}
            {isCompleted && (
              <span className="text-2xl text-pf-success font-semibold">Done</span>
            )}
            {isFailed && (
              <span className="text-2xl text-pf-error font-semibold">Failed</span>
            )}
            {isCancelled && (
              <span className="text-2xl text-pf-warning font-semibold">Cancelled</span>
            )}
          </div>
        </div>

        {/* Status message */}
        {progress.progressMessage && !isTerminal && (
          <p className="text-sm text-white/80 animate-pulse">
            {progress.progressMessage}
          </p>
        )}

        {/* Waiting state */}
        {!isTerminal && !progress.status && (
          <p className="text-sm text-white/60 italic">
            {progress.isConnected ? 'Queued — waiting for worker…' : 'Connecting…'}
          </p>
        )}

        {/* Metadata chips (print time, filament) */}
        {(progress.estimatedPrintTimeSeconds != null || progress.filamentUsedGrams != null) && (
          <div className="flex flex-wrap gap-3 justify-center">
            {progress.estimatedPrintTimeSeconds != null && progress.estimatedPrintTimeSeconds > 0 && (
              <span className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-full bg-white/10 text-xs text-white/80">
                🕐 {sliceJobService.formatPrintTime(progress.estimatedPrintTimeSeconds)}
              </span>
            )}
            {progress.filamentUsedGrams != null && progress.filamentUsedGrams > 0 && (
              <span className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-full bg-white/10 text-xs text-white/80">
                🧵 {sliceJobService.formatFilamentUsed(progress.filamentUsedGrams)}
              </span>
            )}
          </div>
        )}

        {/* Error message */}
        {isFailed && progress.error && (
          <p className="text-xs text-pf-error bg-pf-error/10 border border-pf-error/20 rounded-lg px-3 py-2 max-h-24 overflow-y-auto">
            {progress.error}
          </p>
        )}

        {/* Terminal actions */}
        {isCompleted && (
          <div className="flex items-center gap-3 mt-2">
            {progress.resultFileUrl && (
              <Button
                variant="success"
                size="sm"
                iconLeft={<DownloadIcon className="w-4 h-4" />}
                onClick={handleDownload}
              >
                Download G-code
              </Button>
            )}
            <Button variant="secondary" size="sm" onClick={onNewJob}>
              New Job
            </Button>
          </div>
        )}

        {(isFailed || isCancelled) && (
          <div className="flex items-center gap-3 mt-2">
            <Button variant="primary" size="sm" onClick={onRetry}>
              Retry
            </Button>
            <Button variant="secondary" size="sm" onClick={onNewJob}>
              New Job
            </Button>
          </div>
        )}

        {/* Job ID */}
        <span className="text-[10px] text-white/30 font-mono mt-2">
          {jobId.substring(0, 8)}
        </span>
      </div>
    </div>
  );
}
