import { useState, useEffect, useCallback, useRef } from 'react';
import { nfcHubService } from '@/services/nfcHubService';
import type { NfcTagUnknownEvent } from '@/features/nfc/types';

export interface NfcPairingSession {
  /** True when the pairing modal should be shown (scanning or tag detected). */
  isOpen: boolean;
  /** Set when an nfctagunknown event arrives. Null while scanning. */
  tagEvent: NfcTagUnknownEvent | null;
  /** True while the /hubs/nfc connection is established. */
  isConnected: boolean;
  /** True when the hub drops while the modal is open — shows unavailable UI. */
  isUnavailable: boolean;
  /** Open the modal in "waiting for tag" mode (manual trigger). */
  startScanning: () => void;
  /** Close the modal and reset session state. */
  close: () => void;
}

/**
 * Manages an NFC pairing session backed by /hubs/nfc SignalR events.
 *
 * Passive flow: nfctagunknown fires → modal auto-opens with the captured tag.
 * Active flow: caller invokes startScanning() → modal opens in "scanning" step,
 *              waiting for the next nfctagunknown event.
 *
 * Connection drop while open → isUnavailable flips true (modal shows error step).
 */
export function useNfcPairingSession(): NfcPairingSession {
  const [isOpen, setIsOpen] = useState(false);
  const [tagEvent, setTagEvent] = useState<NfcTagUnknownEvent | null>(null);
  const [isConnected, setIsConnected] = useState(nfcHubService.isConnected());
  const [isUnavailable, setIsUnavailable] = useState(false);

  // Keep a ref so the connection-change handler can read current isOpen
  // without capturing a stale closure.
  const isOpenRef = useRef(false);
  useEffect(() => {
    isOpenRef.current = isOpen;
  });

  useEffect(() => {
    nfcHubService.ensureConnected();

    const unsubUnknown = nfcHubService.onTagUnknown((event) => {
      setTagEvent(event);
      setIsOpen(true);
      setIsUnavailable(false);
    });

    const unsubConnection = nfcHubService.onConnectionChanged((connected) => {
      setIsConnected(connected);
      if (!connected && isOpenRef.current) {
        setIsUnavailable(true);
      }
    });

    return () => {
      unsubUnknown();
      unsubConnection();
    };
  }, []);

  const startScanning = useCallback(() => {
    setIsOpen(true);
    setTagEvent(null);
    setIsUnavailable(false);
  }, []);

  const close = useCallback(() => {
    setIsOpen(false);
    setTagEvent(null);
    setIsUnavailable(false);
  }, []);

  return { isOpen, tagEvent, isConnected, isUnavailable, startScanning, close };
}
