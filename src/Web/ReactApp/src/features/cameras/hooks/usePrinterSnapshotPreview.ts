import { useEffect, useRef, useState } from 'react';
import { apiClient } from '@/services/api';

const SNAPSHOT_ERROR_BACKOFF_MULTIPLIER = 3;

interface SnapshotPreviewState {
  printerId: string;
  src: string | null;
  failed: boolean;
}

interface DirectSnapshotState {
  sourceUrl: string;
  src: string | null;
}

function getIsDocumentVisible(): boolean {
  return typeof document === 'undefined' || document.visibilityState === 'visible';
}

function getCacheBustedSnapshotUrl(snapshotUrl: string): string {
  const separator = snapshotUrl.includes('?') ? '&' : '?';
  return `${snapshotUrl}${separator}_=${Date.now()}`;
}

export function usePrinterSnapshotPreview(
  printerId: string | undefined,
  proxyEnabled: boolean,
  refreshIntervalMs: number,
  directSnapshotUrl?: string | null,
  directEnabled = false
) {
  const previewContainerRef = useRef<HTMLDivElement | null>(null);
  const [snapshotState, setSnapshotState] = useState<SnapshotPreviewState>({
    printerId: '',
    src: null,
    failed: false,
  });
  const [directSnapshotState, setDirectSnapshotState] = useState<DirectSnapshotState>({
    sourceUrl: '',
    src: null,
  });
  const [isDocumentVisible, setIsDocumentVisible] = useState(getIsDocumentVisible);
  const [isIntersectingViewport, setIsIntersectingViewport] = useState(
    () => typeof IntersectionObserver === 'undefined'
  );
  const objectUrlRef = useRef<string | null>(null);

  const effectiveDirectSnapshotUrl = directSnapshotUrl ?? null;
  const isPreviewVisible = isDocumentVisible && isIntersectingViewport;

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
    if (typeof IntersectionObserver === 'undefined') {
      return;
    }

    const element = previewContainerRef.current;
    if (!element) {
      return;
    }

    const observer = new IntersectionObserver((entries) => {
      setIsIntersectingViewport(entries.some((entry) => entry.isIntersecting));
    });
    observer.observe(element);

    return () => {
      observer.disconnect();
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

    if (!proxyEnabled || !printerId) {
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

    if (!isPreviewVisible) {
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
  }, [isPreviewVisible, printerId, proxyEnabled, refreshIntervalMs]);

  useEffect(() => {
    if (!directEnabled || !effectiveDirectSnapshotUrl) {
      const resetTimeoutId = window.setTimeout(() => {
        setDirectSnapshotState({
          sourceUrl: '',
          src: null,
        });
      }, 0);
      return () => {
        window.clearTimeout(resetTimeoutId);
      };
    }

    if (!isPreviewVisible) {
      return;
    }

    const sourceUrl = effectiveDirectSnapshotUrl;
    let cancelled = false;
    let timeoutId: number | undefined;

    function refreshDirectSnapshot() {
      if (cancelled) {
        return;
      }

      setDirectSnapshotState({
        sourceUrl,
        src: getCacheBustedSnapshotUrl(sourceUrl),
      });
      timeoutId = window.setTimeout(refreshDirectSnapshot, refreshIntervalMs);
    }

    timeoutId = window.setTimeout(refreshDirectSnapshot, 0);

    return () => {
      cancelled = true;
      if (timeoutId !== undefined) {
        window.clearTimeout(timeoutId);
      }
    };
  }, [directEnabled, effectiveDirectSnapshotUrl, isPreviewVisible, refreshIntervalMs]);

  useEffect(() => {
    return () => {
      if (objectUrlRef.current) {
        URL.revokeObjectURL(objectUrlRef.current);
      }
    };
  }, []);

  const hasCurrentProxySnapshot = proxyEnabled && snapshotState.printerId === printerId;
  const hasCurrentDirectSnapshot =
    directEnabled && directSnapshotState.sourceUrl === effectiveDirectSnapshotUrl;

  return {
    previewContainerRef,
    snapshotSrc: hasCurrentProxySnapshot
      ? snapshotState.src
      : hasCurrentDirectSnapshot
      ? directSnapshotState.src
      : null,
    snapshotFailed: hasCurrentProxySnapshot ? snapshotState.failed : false,
    isPollingPaused: (proxyEnabled || directEnabled) && !isPreviewVisible,
  };
}
