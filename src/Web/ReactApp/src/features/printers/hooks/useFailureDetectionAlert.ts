import { useCallback, useEffect, useRef, useState } from 'react';
import { printerSignalRService } from '@/services/printer-signalr';
import type { FailureDetectionEvent } from '@/types/api';

const ALERT_LIFETIME_MS = 60_000;

export function useFailureDetectionAlert(printerId: string) {
  const [event, setEvent] = useState<FailureDetectionEvent | null>(null);
  const timeoutRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);

  const clearEvent = useCallback(() => {
    if (timeoutRef.current) {
      clearTimeout(timeoutRef.current);
      timeoutRef.current = undefined;
    }
    setEvent(null);
  }, []);

  useEffect(() => {
    const unsubscribe = printerSignalRService.onFailureDetected((nextEvent) => {
      if (nextEvent.printerId !== printerId) {
        return;
      }

      if (timeoutRef.current) {
        clearTimeout(timeoutRef.current);
      }

      setEvent(nextEvent);
      timeoutRef.current = setTimeout(() => {
        setEvent(null);
        timeoutRef.current = undefined;
      }, ALERT_LIFETIME_MS);
    });

    return () => {
      unsubscribe();
      if (timeoutRef.current) {
        clearTimeout(timeoutRef.current);
        timeoutRef.current = undefined;
      }
    };
  }, [printerId]);

  return { event, clearEvent };
}
