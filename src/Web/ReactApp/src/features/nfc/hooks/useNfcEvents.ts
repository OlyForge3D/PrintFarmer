import { useEffect, useRef, useCallback, useState } from 'react';
import { toast } from 'sonner';
import { nfcHubService } from '@/services/nfcHubService';
import type { NfcTagUnknownEvent, NfcTagReadEvent } from '@/features/nfc/types';

interface UseNfcEventsOptions {
  onTagUnknown?: (event: NfcTagUnknownEvent) => void;
}

/**
 * Subscribes to NFC SignalR events on /hubs/nfc (PR #383 contract).
 * - `nfctagunknown` → triggers onTagUnknown callback (opens pairing modal)
 * - `nfctagread`    → silent toast confirmation for known tags
 */
export function useNfcEvents({ onTagUnknown }: UseNfcEventsOptions = {}) {
  const onTagUnknownRef = useRef(onTagUnknown);
  const [lastUnknownTag, setLastUnknownTag] = useState<NfcTagUnknownEvent | null>(null);

  useEffect(() => { onTagUnknownRef.current = onTagUnknown; }, [onTagUnknown]);

  useEffect(() => {
    nfcHubService.ensureConnected();

    const unsubUnknown = nfcHubService.onTagUnknown((event: NfcTagUnknownEvent) => {
      setLastUnknownTag(event);
      onTagUnknownRef.current?.(event);
    });

    const unsubRead = nfcHubService.onTagRead((event: NfcTagReadEvent) => {
      toast.success(`Spool recognized: ${event.spoolName ?? `#${event.spoolId}`}`);
    });

    return () => {
      unsubUnknown();
      unsubRead();
    };
  }, []);

  const clearLastUnknownTag = useCallback(() => setLastUnknownTag(null), []);

  return { lastUnknownTag, clearLastUnknownTag };
}
