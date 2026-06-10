import { useMemo } from 'react';
import type { Printer, PrinterStatusUpdate, MmuStatus } from '@/types/api';
import { usePrinterStatusUpdates } from '@/common/hooks/useSignalR';

/**
 * Consolidated printer display data that merges API printer data with real-time SignalR status.
 * This is the single source of truth for what state a printer should display.
 *
 * Rules:
 * - Always prefer SignalR status when available (it's real-time)
 * - Fall back to API printer data when SignalR hasn't sent an update yet
 * - Never show undefined values - always provide sensible defaults
 */
export interface PrinterDisplay extends Printer {
  /** Whether this printer's real-time status came from SignalR (vs. stale API data) */
  isRealtimeStatus: boolean;
  /** Raw SignalR update if available, for debugging/inspection */
  signalRStatus: PrinterStatusUpdate | undefined;
  /** MMU/ERCF status from real-time updates (only present when an MMU is detected) */
  mmuStatus?: MmuStatus;
}

/** Merge API printer data with a real-time SignalR status, preferring the SignalR values. */
function mergePrinterDisplay(printer: Printer, signalRStatus: PrinterStatusUpdate | undefined): PrinterDisplay {
  const isRealtimeStatus = signalRStatus !== undefined;

  return {
    ...printer,
    // Always prefer SignalR values when available; provide sensible defaults if neither source has the value
    isOnline: signalRStatus?.isOnline !== undefined ? signalRStatus.isOnline : (printer.isOnline ?? false),
    state: signalRStatus?.state !== undefined ? signalRStatus.state : (printer.state ?? 'Unknown'),
    progress: signalRStatus?.progress !== undefined ? signalRStatus.progress : printer.progress,
    jobName: signalRStatus?.jobName !== undefined ? signalRStatus.jobName : printer.jobName,
    fileName: signalRStatus?.fileName !== undefined ? signalRStatus.fileName : printer.fileName,
    thumbnailUrl: signalRStatus?.thumbnailUrl !== undefined ? signalRStatus.thumbnailUrl : printer.thumbnailUrl,
    cameraStreamUrl: signalRStatus?.cameraStreamUrl !== undefined ? signalRStatus.cameraStreamUrl : printer.cameraStreamUrl,
    cameraSnapshotUrl: signalRStatus?.cameraSnapshotUrl !== undefined ? signalRStatus.cameraSnapshotUrl : printer.cameraSnapshotUrl,
    homedAxes: signalRStatus?.homedAxes !== undefined ? signalRStatus.homedAxes : printer.homedAxes,
    x: signalRStatus?.x !== undefined ? signalRStatus.x : printer.x,
    y: signalRStatus?.y !== undefined ? signalRStatus.y : printer.y,
    z: signalRStatus?.z !== undefined ? signalRStatus.z : printer.z,
    hotendTemp: signalRStatus?.hotendTemp !== undefined ? signalRStatus.hotendTemp : printer.hotendTemp,
    bedTemp: signalRStatus?.bedTemp !== undefined ? signalRStatus.bedTemp : printer.bedTemp,
    hotendTarget: signalRStatus?.hotendTarget !== undefined ? signalRStatus.hotendTarget : printer.hotendTarget,
    bedTarget: signalRStatus?.bedTarget !== undefined ? signalRStatus.bedTarget : printer.bedTarget,
    spoolInfo: signalRStatus?.spoolInfo !== undefined ? signalRStatus.spoolInfo : printer.spoolInfo,
    // MMU status (only available from SignalR)
    mmuStatus: signalRStatus?.mmuStatus,
    // Pass through printTimeLeftSeconds from SignalR for ETA derivation
    printTimeLeftSeconds: signalRStatus?.printTimeLeftSeconds ?? printer.printTimeLeftSeconds,
    // Prefer API-computed ETA (more accurate); SignalR provides printTimeLeftSeconds for live updates
    estimatedCompletionTimeUtc: signalRStatus?.estimatedCompletionTimeUtc ?? printer.estimatedCompletionTimeUtc,
    // Expose for debugging/inspection
    isRealtimeStatus,
    signalRStatus,
  } as PrinterDisplay;
}

/**
 * Result cache for merges, keyed by the API printer object. Reusing the previous
 * merged object when both inputs are reference-equal lets memoized cards skip
 * re-rendering when another printer updates. WeakMap entries are dropped
 * automatically when react-query replaces the printer objects.
 */
const mergeCache = new WeakMap<Printer, { signalRStatus: PrinterStatusUpdate | undefined; result: PrinterDisplay }>();

function mergePrinterDisplayCached(printer: Printer, signalRStatus: PrinterStatusUpdate | undefined): PrinterDisplay {
  const cached = mergeCache.get(printer);
  if (cached && cached.signalRStatus === signalRStatus) {
    return cached.result;
  }
  const result = mergePrinterDisplay(printer, signalRStatus);
  mergeCache.set(printer, { signalRStatus, result });
  return result;
}

/**
 * Hook to get display-ready printer data that intelligently merges API and SignalR sources.
 *
 * Usage:
 * ```tsx
 * const displayPrinter = usePrinterDisplay(apiPrinter);
 * const isOnline = displayPrinter.isOnline;  // Prefers SignalR, falls back to API
 * const isRealtimeStatus = displayPrinter.isRealtimeStatus;  // true if from SignalR
 * ```
 *
 * @param printer - The base printer data from the API
 * @returns PrinterDisplay object with merged state, ready for UI rendering
 */
export function usePrinterDisplay(printer: Printer): PrinterDisplay {
  const { printerStatuses } = usePrinterStatusUpdates();
  const signalRStatus = printerStatuses.get(printer.id);

  // Compute display values - prefer SignalR, fall back to API
  return useMemo(
    () => mergePrinterDisplay(printer, signalRStatus),
    [printer, signalRStatus]
  );
}

/**
 * Hook for bulk display of multiple printers.
 * More efficient than calling usePrinterDisplay N times.
 *
 * Reuses the previous merged object for any printer whose inputs are unchanged,
 * so memoized cards can skip re-rendering when another printer updates.
 *
 * @param printers - Array of API printer objects
 * @returns Array of PrinterDisplay objects
 */
export function usePrinterDisplays(printers: Printer[]): PrinterDisplay[] {
  const { printerStatuses } = usePrinterStatusUpdates();

  return useMemo(
    () => printers.map((printer) => mergePrinterDisplayCached(printer, printerStatuses.get(printer.id))),
    [printers, printerStatuses]
  );
}
