import { useState, useCallback } from 'react';
import { useNfcEvents } from '@/features/nfc/hooks/useNfcEvents';
import { NfcBindingModal } from '@/features/nfc/components/NfcBindingModal';
import type { NfcTagUnknownEvent } from '@/features/nfc/types';

/**
 * App-level component that subscribes to NFC SignalR events,
 * shows toast notifications, and opens the binding modal on unknown tags.
 * Mount once inside the authenticated layout.
 */
export function NfcEventListener() {
  const [bindEvent, setBindEvent] = useState<NfcTagUnknownEvent | null>(null);
  const [modalOpen, setModalOpen] = useState(false);

  const handleBindRequested = useCallback((event: NfcTagUnknownEvent) => {
    setBindEvent(event);
    setModalOpen(true);
  }, []);

  useNfcEvents({ onBindRequested: handleBindRequested });

  return (
    <NfcBindingModal
      isOpen={modalOpen}
      onClose={() => setModalOpen(false)}
      event={bindEvent}
    />
  );
}
