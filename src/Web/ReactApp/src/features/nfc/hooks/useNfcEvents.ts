import { useEffect, useRef, useState } from 'react';
import { toast } from 'sonner';
import { nfcHubService } from '@/services/nfcHubService';
import type { NfcTagReadEvent, NfcTagUnknownEvent } from '@/features/nfc/types';

interface UseNfcEventsOptions {
  onTagRead?: (event: NfcTagReadEvent) => void;
  onTagUnknown?: (event: NfcTagUnknownEvent) => void;
  onBindRequested?: (event: NfcTagUnknownEvent) => void;
}

export function useNfcEvents(options: UseNfcEventsOptions = {}) {
  const { onTagRead, onTagUnknown, onBindRequested } = options;
  const [lastTagRead, setLastTagRead] = useState<NfcTagReadEvent | null>(null);
  const [lastUnknownTag, setLastUnknownTag] = useState<NfcTagUnknownEvent | null>(null);

  const onTagReadRef = useRef(onTagRead);
  const onTagUnknownRef = useRef(onTagUnknown);
  const onBindRequestedRef = useRef(onBindRequested);

  useEffect(() => { onTagReadRef.current = onTagRead; }, [onTagRead]);
  useEffect(() => { onTagUnknownRef.current = onTagUnknown; }, [onTagUnknown]);
  useEffect(() => { onBindRequestedRef.current = onBindRequested; }, [onBindRequested]);

  useEffect(() => {
    void nfcHubService.ensureConnected();

    const unsubRead = nfcHubService.onTagRead((event) => {
      setLastTagRead(event);
      onTagReadRef.current?.(event);

      toast.success(`Spool recognized: ${event.spoolName ?? `#${event.spoolId}`}`);
    });

    const unsubUnknown = nfcHubService.onTagUnknown((event) => {
      setLastUnknownTag(event);
      onTagUnknownRef.current?.(event);

      toast.warning('Unknown NFC tag scanned', {
        description: `Tag ${event.tagUid} at printer ${event.printerId}`,
        action: onBindRequestedRef.current
          ? {
              label: 'Bind it',
              onClick: () => onBindRequestedRef.current?.(event),
            }
          : undefined,
        duration: 8000,
      });
    });

    return () => {
      unsubRead();
      unsubUnknown();
    };
  }, []);

  return { lastTagRead, lastUnknownTag };
}
