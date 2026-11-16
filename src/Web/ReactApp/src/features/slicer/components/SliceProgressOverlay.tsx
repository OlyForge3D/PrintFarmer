import { useCallback, useEffect, useRef, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { Button } from '@/common/components/ui';
import { CloseIcon, DownloadIcon, EyeIcon, PrinterIcon, SaveIcon } from '@/common/components/icons/MdiIcons';
import { sliceJobService } from '@/services/sliceJobService';
import { GcodePreviewModal } from '@/features/slicer/components/GcodePreviewModal';
import { SendToPrinterModal } from '@/features/slicer/components/SendToPrinterModal';
import type { SliceJobProgressState } from '@/features/slicer/hooks/useSliceJobProgress';
import {
  downloadGcodeArtifact,
  resolveGcodeArtifactForAction,
  saveGcodeArtifactToLibrary,
} from '@/features/slicer/utils/sliceArtifactActions';

interface SliceProgressOverlayProps {
  jobId: string;
  defaultPrinterId?: string | null;
  progress: SliceJobProgressState;
  onNewJob: () => void;
  onRetry: () => void;
  /**
   * Hides the overlay for a COMPLETED job without resetting any slicer state.
   * Only offered once slicing has completed; active slicing is never dismissible.
   */
  onDismiss?: () => void;
  /** Move focus to the dismiss control on mount (used when the user explicitly reopens results). */
  focusDismissOnMount?: boolean;
  /**
   * Per-kg filament cost (from the selected OrcaSlicer filament profile's
   * `filament_cost`). Used for a best-effort material cost estimate. When
   * unavailable the material cost is omitted.
   */
  filamentCostPerKg?: number | null;
  /** Resolved per-gram cost — takes precedence over filamentCostPerKg when set. */
  resolvedCostPerGram?: number | null;
  /** Source of the resolved cost, shown as a small indicator on the cost chip. */
  costSource?: 'spoolman' | 'profile' | null;
  /** Selected Spoolman spool ID — forwarded to the queue submission. */
  selectedSpoolId?: number | null;
  /** Required printer model derived from slice selections — forwarded to the queue form. */
  requiredPrinterModel?: string;
  /** Required material type derived from slice selections — forwarded to the queue form. */
  requiredMaterialType?: string;
  /** Required nozzle diameter derived from slice selections — forwarded to the queue form. */
  requiredNozzleDiameter?: number;
  /**
   * True while a Retry-triggered resubmission is in flight. Disables the
   * Retry button so a double-click can't fire a second overlapping
   * `POST /api/slice/` for the same failed job (issue #2374).
   */
  retryDisabled?: boolean;
}

/**
 * Full-viewport overlay shown on the 3D workspace during slicing.
 * Inspired by OrcaSlicer's progress dialog — prominent, centered, 
 * with real-time progress ring and status details.
 */
export function SliceProgressOverlay({ jobId, defaultPrinterId, progress, onNewJob, onRetry, onDismiss, focusDismissOnMount, filamentCostPerKg, resolvedCostPerGram, costSource, selectedSpoolId, requiredPrinterModel, requiredMaterialType, requiredNozzleDiameter, retryDisabled }: SliceProgressOverlayProps) {
  const queryClient = useQueryClient();
  const isCompleted = progress.status === 'Completed';
  const isFailed = progress.status === 'Failed';
  const isCancelled = progress.status === 'Cancelled';
  const isTerminal = isCompleted || isFailed || isCancelled;
  const percent = isCompleted ? 100 : progress.progressPercent;

  const [previewOpen, setPreviewOpen] = useState(false);
  const [sendOpen, setSendOpen] = useState(false);
  const [printArtifactId, setPrintArtifactId] = useState<string | null>(null);
  const [isResolvingPrint, setIsResolvingPrint] = useState(false);
  const [isDownloading, setIsDownloading] = useState(false);
  const [isSaving, setIsSaving] = useState(false);
  const [saveFeedback, setSaveFeedback] = useState<{
    kind: 'success' | 'error';
    message: string;
  } | null>(null);
  const saveInFlightRef = useRef(false);
  const printResolutionInFlightRef = useRef(false);
  const canDismiss = isCompleted && onDismiss != null;
  const dismissButtonRef = useRef<HTMLButtonElement>(null);
  // Captured once so live progress updates can never re-trigger focus.
  const focusDismissOnMountRef = useRef(focusDismissOnMount);

  useEffect(() => {
    if (focusDismissOnMountRef.current) {
      dismissButtonRef.current?.focus();
    }
  }, []);

  const handleDownload = useCallback(async () => {
    if (!progress.artifactsRoute) return;
    setIsDownloading(true);
    try {
      await downloadGcodeArtifact(progress.artifactsRoute);
    } catch (error) {
      toast.error(error instanceof Error ? error.message : 'Failed to download G-code.');
    } finally {
      setIsDownloading(false);
    }
  }, [progress.artifactsRoute]);

  const handleSave = useCallback(async () => {
    if (!progress.artifactsRoute || saveInFlightRef.current) return;
    saveInFlightRef.current = true;
    setIsSaving(true);
    setSaveFeedback(null);
    try {
      const result = await saveGcodeArtifactToLibrary(progress.artifactsRoute, jobId);
      if (result.createdNew) {
        await queryClient.invalidateQueries({ queryKey: ['file-browser'] });
      }
      const message = result.createdNew
        ? 'Saved to Library'
        : 'Already in File Library';
      setSaveFeedback({ kind: 'success', message });
      toast.success(message);
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Failed to save G-code.';
      setSaveFeedback({ kind: 'error', message });
      toast.error(message);
    } finally {
      saveInFlightRef.current = false;
      setIsSaving(false);
    }
  }, [jobId, progress.artifactsRoute, queryClient]);

  const handlePrint = useCallback(async () => {
    if (!progress.artifactsRoute || printResolutionInFlightRef.current) return;
    printResolutionInFlightRef.current = true;
    setIsResolvingPrint(true);
    try {
      const artifact = await resolveGcodeArtifactForAction(progress.artifactsRoute);
      if (!artifact) {
        throw new Error('No G-code artifact is available for this job.');
      }
      setPrintArtifactId(artifact.id);
      setSendOpen(true);
    } catch (error) {
      toast.error(error instanceof Error ? error.message : 'Failed to prepare G-code for printing.');
    } finally {
      printResolutionInFlightRef.current = false;
      setIsResolvingPrint(false);
    }
  }, [progress.artifactsRoute]);

  const materialCost = resolvedCostPerGram != null
    ? sliceJobService.computeMaterialCostPerGram(progress.filamentUsedGrams, resolvedCostPerGram)
    : sliceJobService.computeMaterialCost(progress.filamentUsedGrams, filamentCostPerKg);
  const materialCostFormatted = materialCost != null ? `$${materialCost.toFixed(2)}` : null;

  // SVG progress ring dimensions
  const size = 160;
  const strokeWidth = 10;
  const radius = (size - strokeWidth) / 2;
  const circumference = 2 * Math.PI * radius;
  const offset = circumference - (percent / 100) * circumference;
  const ringFillClassName = isFailed
    ? 'stroke-pf-error'
    : isCompleted
      ? 'stroke-pf-success'
      : 'stroke-pf-progress-fill';

  return (
    <div
      role="region"
      aria-label={isCompleted ? 'Slice results' : 'Slice progress'}
      className="absolute inset-0 z-30 flex justify-center overflow-y-auto bg-black/60 backdrop-blur-sm rounded-lg"
    >
      {canDismiss && (
        <Button
          ref={dismissButtonRef}
          variant="ghost"
          size="sm"
          control
          className="absolute top-3 right-3 text-white/80"
          aria-label="Hide slice results"
          title="Hide slice results"
          iconCenter={<CloseIcon className="w-4 h-4" />}
          onClick={onDismiss}
        />
      )}
      <div className="my-auto flex w-full flex-col items-center gap-4 p-8 max-w-sm text-center">

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
              className="stroke-pf-progress-track"
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
              className={`${ringFillClassName} transition-[stroke-dashoffset] duration-500 ease-out`}
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
              <span className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-xs bg-white/10 text-xs text-white/80">
                🕐 {sliceJobService.formatPrintTime(progress.estimatedPrintTimeSeconds)}
              </span>
            )}
            {progress.filamentUsedGrams != null && progress.filamentUsedGrams > 0 && (
              <span className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-xs bg-white/10 text-xs text-white/80">
                🧵 {sliceJobService.formatFilamentUsed(progress.filamentUsedGrams)}
              </span>
            )}
            {materialCostFormatted && (
              <span className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-xs bg-white/10 text-xs text-white/80">
                💰 {materialCostFormatted}
                {costSource && (
                  <span className="text-[10px] opacity-60">
                    ({costSource === 'spoolman' ? 'Spoolman' : 'Profile'})
                  </span>
                )}
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
          <div className="mt-2 flex w-full flex-col gap-3">
            <div
              role="group"
              aria-label="Sliced G-code actions"
              className="grid w-full grid-cols-[repeat(auto-fit,minmax(8.5rem,1fr))] gap-2"
            >
              {progress.artifactsRoute && (
                <Button
                  variant="primary"
                  size="sm"
                  control
                  className="w-full"
                  iconLeft={<EyeIcon className="w-4 h-4" />}
                  onClick={() => setPreviewOpen(true)}
                >
                  Preview
                </Button>
              )}
              <Button
                variant="success"
                size="sm"
                control
                className="w-full"
                iconLeft={<PrinterIcon className="w-4 h-4" />}
                onClick={handlePrint}
                loading={isResolvingPrint}
                disabled={!progress.artifactsRoute || isResolvingPrint}
              >
                Print
              </Button>
              {progress.artifactsRoute && (
                <Button
                  variant="secondary"
                  size="sm"
                  control
                  className="w-full"
                  iconLeft={<DownloadIcon className="w-4 h-4" />}
                  onClick={handleDownload}
                  loading={isDownloading}
                  disabled={isDownloading}
                >
                  Download G-code
                </Button>
              )}
              <Button
                variant="secondary"
                size="sm"
                control
                className="w-full"
                iconLeft={<SaveIcon className="w-4 h-4" />}
                onClick={handleSave}
                loading={isSaving}
                disabled={!progress.artifactsRoute || isSaving}
                title={progress.artifactsRoute ? undefined : 'No staged G-code artifact is available.'}
              >
                Save to Library
              </Button>
            </div>
            {saveFeedback && (
              <p
                role={saveFeedback.kind === 'error' ? 'alert' : 'status'}
                className={saveFeedback.kind === 'error' ? 'text-xs text-pf-error' : 'text-xs text-pf-success'}
              >
                {saveFeedback.message}
              </p>
            )}
            <div className="flex w-full flex-wrap items-center justify-between gap-2 border-t border-white/10 pt-3">
              {canDismiss ? (
                <Button
                  variant="secondary"
                  size="sm"
                  control
                  onClick={onDismiss}
                  title="Hide these results and return to the workspace. Your job, models and settings are kept."
                >
                  Hide results
                </Button>
              ) : <span />}
              <Button variant="secondary" size="sm" control onClick={onNewJob}>
                New Job
              </Button>
            </div>
          </div>
        )}

        {(isFailed || isCancelled) && (
          <div className="mt-2 flex flex-wrap items-center justify-center gap-2">
            <Button variant="primary" size="sm" control onClick={onRetry} disabled={retryDisabled} loading={retryDisabled}>
              Retry
            </Button>
            <Button variant="secondary" size="sm" control onClick={onNewJob}>
              New Job
            </Button>
          </div>
        )}

        {/* Job ID */}
        <span className="text-[10px] text-white/30 font-mono mt-2">
          {jobId.substring(0, 8)}
        </span>
      </div>

      {/* Post-slice modals */}
      <GcodePreviewModal
        isOpen={previewOpen}
        onClose={() => setPreviewOpen(false)}
        artifactsRoute={progress.artifactsRoute ?? ''}
      />
      {printArtifactId && (
        <SendToPrinterModal
          isOpen={sendOpen}
          onClose={() => {
            setSendOpen(false);
            setPrintArtifactId(null);
          }}
          jobId={jobId}
          artifactId={printArtifactId}
          defaultPrinterId={defaultPrinterId}
          selectedSpoolId={selectedSpoolId}
          requiredPrinterModel={requiredPrinterModel}
          requiredMaterialType={requiredMaterialType}
          requiredNozzleDiameter={requiredNozzleDiameter}
        />
      )}
    </div>
  );
}
