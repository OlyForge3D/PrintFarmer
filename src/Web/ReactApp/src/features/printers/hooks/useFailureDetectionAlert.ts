import { useCallback, useEffect, useRef, useState } from 'react';
import { printerSignalRService } from '@/services/printer-signalr';
import type { FailureDetectionEvent } from '@/types/api';
import { getFailureDetectionIncidentKey } from '@/features/printers/utils/failure-detection-incidents';

const ALERT_LIFETIME_MS = 60_000;
const MAX_RECENT_EVENTS = 5;

export function useFailureDetectionAlert(printerId: string) {
  const [event, setEvent] = useState<FailureDetectionEvent | null>(null);
  const [recentEvents, setRecentEvents] = useState<FailureDetectionEvent[]>([]);
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
      setRecentEvents((currentEvents) => {
        const nextKey = getFailureDetectionIncidentKey(nextEvent);
        const dedupedEvents = currentEvents.filter(
          (currentEvent) => getFailureDetectionIncidentKey(currentEvent) !== nextKey
        );

        return [nextEvent, ...dedupedEvents].slice(0, MAX_RECENT_EVENTS);
      });
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

  return { event, recentEvents, clearEvent };
}
