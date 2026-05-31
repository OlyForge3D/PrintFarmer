import { useEffect, useRef, useCallback, useState } from 'react';
import { toast } from 'sonner';
import { printerSignalRService } from '@/services/printer-signalr';
import type {
  NfcTagUnknownEvent,
  NfcTagKnownEvent,
  NfcTagMismatchEvent,
  NfcReaderOfflineEvent,
} from '@/features/nfc/types';

interface UseNfcEventsOptions {
  onTagUnknown?: (event: NfcTagUnknownEvent) => void;
  onTagMismatch?: (event: NfcTagMismatchEvent) => void;
}

/**
 * Subscribes to NFC SignalR events on the printer hub.
 * - `nfctagunknown` → triggers onTagUnknown callback (opens pairing modal)
 * - `nfctagknown` → silent toast confirmation
 * - `nfctagmismatch` → triggers onTagMismatch callback (warning modal)
 * - `nfcreaderoffline` → toast warning
 */
export function useNfcEvents({ onTagUnknown, onTagMismatch }: UseNfcEventsOptions = {}) {
  const onTagUnknownRef = useRef(onTagUnknown);
  const onTagMismatchRef = useRef(onTagMismatch);
  const [lastUnknownTag, setLastUnknownTag] = useState<NfcTagUnknownEvent | null>(null);
  const [lastMismatch, setLastMismatch] = useState<NfcTagMismatchEvent | null>(null);

  useEffect(() => { onTagUnknownRef.current = onTagUnknown; }, [onTagUnknown]);
  useEffect(() => { onTagMismatchRef.current = onTagMismatch; }, [onTagMismatch]);

  useEffect(() => {
    printerSignalRService.connect();

    const unsubUnknown = printerSignalRService.onNfcTagUnknown((event: NfcTagUnknownEvent) => {
      setLastUnknownTag(event);
      onTagUnknownRef.current?.(event);
    });

    const unsubKnown = printerSignalRService.onNfcTagKnown((event: NfcTagKnownEvent) => {
      toast.success(`Spool recognized: ${event.spoolName ?? event.tagUid}`);
    });

    const unsubMismatch = printerSignalRService.onNfcTagMismatch((event: NfcTagMismatchEvent) => {
      setLastMismatch(event);
      onTagMismatchRef.current?.(event);
    });

    const unsubOffline = printerSignalRService.onNfcReaderOffline((event: NfcReaderOfflineEvent) => {
      toast.warning(`Tag reader offline${event.deviceName ? `: ${event.deviceName}` : ''}`);
    });

    return () => {
      unsubUnknown();
      unsubKnown();
      unsubMismatch();
      unsubOffline();
    };
  }, []);

  const clearLastUnknownTag = useCallback(() => setLastUnknownTag(null), []);
  const clearLastMismatch = useCallback(() => setLastMismatch(null), []);

  return { lastUnknownTag, lastMismatch, clearLastUnknownTag, clearLastMismatch };
}
