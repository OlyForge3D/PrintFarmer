import { useEffect, useRef, useState } from 'react';
import { apiClient } from '@/services/api';

const SNAPSHOT_ERROR_BACKOFF_MULTIPLIER = 3;

interface SnapshotPreviewState {
  printerId: string;
  src: string | null;
  failed: boolean;
}

function getIsDocumentVisible(): boolean {
  return typeof document === 'undefined' || document.visibilityState === 'visible';
}

export function usePrinterSnapshotPreview(
  printerId: string | undefined,
  enabled: boolean,
  refreshIntervalMs: number
) {
  const [snapshotState, setSnapshotState] = useState<SnapshotPreviewState>({
    printerId: '',
    src: null,
    failed: false,
  });
  const [isDocumentVisible, setIsDocumentVisible] = useState(getIsDocumentVisible);
  const objectUrlRef = useRef<string | null>(null);

  useEffect(() => {
    if (typeof document === 'undefined') {
      return;
    }

    const handleVisibilityChange = () => {
      setIsDocumentVisible(getIsDocumentVisible());
    };

    document.addEventListener('visibilitychange', handleVisibilityChange);
    return () => {
      document.removeEventListener('visibilitychange', handleVisibilityChange);
    };
  }, []);

  useEffect(() => {
    const revokeCurrentObjectUrl = () => {
      if (!objectUrlRef.current) {
        return;
      }

      URL.revokeObjectURL(objectUrlRef.current);
      objectUrlRef.current = null;
    };

    if (!enabled || !printerId) {
      revokeCurrentObjectUrl();
      const resetTimeoutId = window.setTimeout(() => {
        setSnapshotState({
          printerId: '',
          src: null,
          failed: false,
        });
      }, 0);
      return () => {
        window.clearTimeout(resetTimeoutId);
      };
    }

    if (!isDocumentVisible) {
      return;
    }

    const currentPrinterId = printerId;
    let cancelled = false;
    let timeoutId: number | undefined;

    function scheduleNextLoad(intervalMs: number) {
      timeoutId = window.setTimeout(() => {
        void loadSnapshot();
      }, intervalMs);
    }

    async function loadSnapshot() {
      try {
        const blob = await apiClient.getPrinterSnapshot(currentPrinterId);
        if (cancelled) {
          return;
        }

        const nextObjectUrl = URL.createObjectURL(blob);
        revokeCurrentObjectUrl();
        objectUrlRef.current = nextObjectUrl;
        setSnapshotState({
          printerId: currentPrinterId,
          src: nextObjectUrl,
          failed: false,
        });
        scheduleNextLoad(refreshIntervalMs);
      } catch {
        if (cancelled) {
          return;
        }

        setSnapshotState((current) => ({
          printerId: currentPrinterId,
          src: current.printerId === currentPrinterId ? current.src : null,
          failed: true,
        }));
        scheduleNextLoad(refreshIntervalMs * SNAPSHOT_ERROR_BACKOFF_MULTIPLIER);
      }
    }

    void loadSnapshot();

    return () => {
      cancelled = true;
      if (timeoutId !== undefined) {
        window.clearTimeout(timeoutId);
      }
    };
  }, [enabled, isDocumentVisible, printerId, refreshIntervalMs]);

  useEffect(() => {
    return () => {
      if (objectUrlRef.current) {
        URL.revokeObjectURL(objectUrlRef.current);
      }
    };
  }, []);

  const hasCurrentSnapshot = enabled && snapshotState.printerId === printerId;

  return {
    snapshotSrc: hasCurrentSnapshot ? snapshotState.src : null,
    snapshotFailed: hasCurrentSnapshot ? snapshotState.failed : false,
    isPollingPaused: enabled && !isDocumentVisible,
  };
}
