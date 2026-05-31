import { useEffect, useRef, useState } from 'react';
import { toast } from 'sonner';
import { nfcSignalRService } from '@/services/nfc-signalr';
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
    nfcSignalRService.connect();

    const unsubRead = nfcSignalRService.onTagRead((event) => {
      setLastTagRead(event);
      onTagReadRef.current?.(event);

      const spoolLabel = event.spoolId ? `Spool detected` : 'Tag scanned';
      toast.success(`${spoolLabel} on printer`, {
        description: event.spoolId
          ? `Spool ${event.spoolId} seen at printer ${event.printerId}`
          : `Tag ${event.tagUid} read at printer ${event.printerId}`,
        duration: 4000,
      });
    });

    const unsubUnknown = nfcSignalRService.onTagUnknown((event) => {
      setLastUnknownTag(event);
      onTagUnknownRef.current?.(event);

      toast.warning('Unknown NFC tag scanned', {
        description: `Tag ${event.tagUid} at printer ${event.printerId}`,
        action: {
          label: 'Bind it',
          onClick: () => onBindRequestedRef.current?.(event),
        },
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
