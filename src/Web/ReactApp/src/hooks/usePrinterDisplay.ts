import { useMemo } from 'react';
import type { Printer, PrinterStatusUpdate } from '@/types/api';
import { usePrinterStatusUpdates } from '@/hooks/useSignalR';

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
  return useMemo(() => {
    const isRealtimeStatus = signalRStatus !== undefined;

    return {
      ...printer,
      // Always prefer SignalR values when available; provide sensible defaults if neither source has the value
      isOnline: signalRStatus?.isOnline !== undefined ? signalRStatus.isOnline : (printer.isOnline ?? false),
      state: signalRStatus?.state !== undefined ? signalRStatus.state : (printer.state ?? 'Unknown'),
      progress: signalRStatus?.progress !== undefined ? signalRStatus.progress : printer.progress,
      jobName: signalRStatus?.jobName !== undefined ? signalRStatus.jobName : printer.jobName,
      thumbnailUrl: signalRStatus?.thumbnailUrl !== undefined ? signalRStatus.thumbnailUrl : printer.thumbnailUrl,
      cameraStreamUrl: signalRStatus?.cameraStreamUrl !== undefined ? signalRStatus.cameraStreamUrl : printer.cameraStreamUrl,
      cameraSnapshotUrl: signalRStatus?.cameraSnapshotUrl !== undefined ? signalRStatus.cameraSnapshotUrl : printer.cameraSnapshotUrl,
      x: signalRStatus?.x !== undefined ? signalRStatus.x : printer.x,
      y: signalRStatus?.y !== undefined ? signalRStatus.y : printer.y,
      z: signalRStatus?.z !== undefined ? signalRStatus.z : printer.z,
      hotendTemp: signalRStatus?.hotendTemp !== undefined ? signalRStatus.hotendTemp : printer.hotendTemp,
      bedTemp: signalRStatus?.bedTemp !== undefined ? signalRStatus.bedTemp : printer.bedTemp,
      hotendTarget: signalRStatus?.hotendTarget !== undefined ? signalRStatus.hotendTarget : printer.hotendTarget,
      bedTarget: signalRStatus?.bedTarget !== undefined ? signalRStatus.bedTarget : printer.bedTarget,
      // Expose for debugging/inspection
      isRealtimeStatus,
      signalRStatus,
    } as PrinterDisplay;
  }, [
    printer,
    signalRStatus,
  ]);
}

/**
 * Hook for bulk display of multiple printers.
 * More efficient than calling usePrinterDisplay N times.
 * 
 * @param printers - Array of API printer objects
 * @returns Array of PrinterDisplay objects
 */
export function usePrinterDisplays(printers: Printer[]): PrinterDisplay[] {
  const { printerStatuses } = usePrinterStatusUpdates();

  return useMemo(() => {
    return printers.map((printer) => {
      const signalRStatus = printerStatuses.get(printer.id);
      const isRealtimeStatus = signalRStatus !== undefined;

      return {
        ...printer,
        isOnline: signalRStatus?.isOnline !== undefined ? signalRStatus.isOnline : (printer.isOnline ?? false),
        state: signalRStatus?.state !== undefined ? signalRStatus.state : (printer.state ?? 'Unknown'),
        progress: signalRStatus?.progress !== undefined ? signalRStatus.progress : printer.progress,
        jobName: signalRStatus?.jobName !== undefined ? signalRStatus.jobName : printer.jobName,
        thumbnailUrl: signalRStatus?.thumbnailUrl !== undefined ? signalRStatus.thumbnailUrl : printer.thumbnailUrl,
        cameraStreamUrl: signalRStatus?.cameraStreamUrl !== undefined ? signalRStatus.cameraStreamUrl : printer.cameraStreamUrl,
        cameraSnapshotUrl: signalRStatus?.cameraSnapshotUrl !== undefined ? signalRStatus.cameraSnapshotUrl : printer.cameraSnapshotUrl,
        x: signalRStatus?.x !== undefined ? signalRStatus.x : printer.x,
        y: signalRStatus?.y !== undefined ? signalRStatus.y : printer.y,
        z: signalRStatus?.z !== undefined ? signalRStatus.z : printer.z,
        hotendTemp: signalRStatus?.hotendTemp !== undefined ? signalRStatus.hotendTemp : printer.hotendTemp,
        bedTemp: signalRStatus?.bedTemp !== undefined ? signalRStatus.bedTemp : printer.bedTemp,
        hotendTarget: signalRStatus?.hotendTarget !== undefined ? signalRStatus.hotendTarget : printer.hotendTarget,
        bedTarget: signalRStatus?.bedTarget !== undefined ? signalRStatus.bedTarget : printer.bedTarget,
        isRealtimeStatus,
        signalRStatus,
      } as PrinterDisplay;
    });
  }, [printers, printerStatuses]);
}
