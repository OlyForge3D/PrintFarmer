import { useCallback, useEffect, useRef, useState } from 'react';
import { apiClient } from '@/services/api';
import { printerSignalRService } from '@/services/printer-signalr';
import { mutationErrorMessage } from '@/common/utils/mutationError';
import type { PrinterStatusUpdate } from '@/types/api';

const fields = ['speedFactorPercent', 'flowFactorPercent'] as const;
type Factors = Record<typeof fields[number], number | null>;
const unknownFactors: Factors = { speedFactorPercent: null, flowFactorPercent: null };

function observation(value: number | null | undefined): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

/** Fresh observations only: never seed a newly selected printer from an old UI/cache snapshot. */
export function useRuntimeFactors(printerId: string) {
  const [factors, setFactors] = useState<Factors>(unknownFactors);
  const [connected, setConnected] = useState(printerSignalRService.isConnected);
  const [epoch, setEpoch] = useState(0);
  const [error, setError] = useState('');
  const [status, setStatus] = useState<{ isOnline: boolean; state?: string } | null>(null);
  const refreshRef = useRef<() => void>(() => {});

  useEffect(() => {
    let active = true;
    let generation = 0;
    let request = 0;
    let onlineRevision = 0;
    let stateRevision = 0;
    let liveConnected = printerSignalRService.isConnected;
    const revisions = { speedFactorPercent: 0, flowFactorPercent: 0 };
    const refresh = () => {
      const readGeneration = generation;
      const readRequest = ++request;
      const baseline = { ...revisions };
      const onlineBaseline = onlineRevision;
      const stateBaseline = stateRevision;
      void apiClient.getPrinterStatus(printerId).then(status => {
        if (!active || generation !== readGeneration || request !== readRequest) return;
        setError('');
        setStatus(previous => ({
          isOnline: onlineRevision === onlineBaseline ? status.isOnline : previous?.isOnline ?? false,
          state: stateRevision === stateBaseline ? status.state : previous?.state,
        }));
        setFactors(previous => {
          const next = { ...previous };
          for (const field of fields) {
            // An older HTTP response cannot replace a newer independent observation.
            if (baseline[field] === revisions[field]) next[field] = status.isOnline ? observation(status[field]) : null;
          }
          return next;
        });
      }).catch(cause => {
        if (active && generation === readGeneration && request === readRequest) {
          setError(mutationErrorMessage(cause, 'Failed to refresh runtime percentages'));
        }
      });
    };
    refreshRef.current = refresh;
    const reset = (isConnected: boolean) => {
      generation++;
      liveConnected = isConnected;
      setEpoch(value => value + 1);
      setConnected(isConnected);
      setFactors(unknownFactors);
      setStatus(null);
      setError('');
      if (isConnected) {
        // The service restores its desired printer subscriptions after reconnect.
        void printerSignalRService.subscribeToPrinter(printerId);
        refresh();
      }
    };
    const unsubscribeStatus = printerSignalRService.onPrinterStatusUpdate((status: PrinterStatusUpdate) => {
      if (!active || !liveConnected || status.id !== printerId) return;
      if (status.isOnline !== undefined) onlineRevision++;
      if (status.state !== undefined) stateRevision++;
      setStatus(previous => ({
        isOnline: status.isOnline ?? previous?.isOnline ?? false,
        state: status.state !== undefined ? status.state : previous?.state,
      }));
      if (status.isOnline === false) {
        generation++;
        setFactors(unknownFactors);
        setEpoch(value => value + 1);
        return;
      }
      for (const field of fields) {
        if (Object.hasOwn(status, field)) revisions[field]++;
      }
      setFactors(previous => {
        const next = { ...previous };
        for (const field of fields) {
          if (Object.hasOwn(status, field)) next[field] = observation(status[field]);
        }
        return next;
      });
    });
    const unsubscribeConnection = printerSignalRService.onConnectionStateChange(reset);
    reset(printerSignalRService.isConnected);
    void printerSignalRService.connect();
    return () => {
      active = false;
      refreshRef.current = () => {};
      unsubscribeStatus();
      unsubscribeConnection();
    };
  }, [printerId]);

  const refresh = useCallback(() => refreshRef.current(), []);
  return { ...factors, connected, epoch, error, refresh, status };
}
