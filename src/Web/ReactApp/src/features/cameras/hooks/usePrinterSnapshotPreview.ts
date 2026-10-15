import { useEffect, useRef, useState } from 'react';
import { apiClient } from '@/services/api';
import { getAuthenticatedCameraProxyRoute } from '@/common/auth/authenticatedCameraRoutes';

const SNAPSHOT_ERROR_BACKOFF_MULTIPLIER = 3;

interface SnapshotPreviewState {
  sourceKey: string;
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
  directEnabled = false,
  proxySnapshotRoute?: string | null,
) {
  const previewContainerRef = useRef<HTMLDivElement | null>(null);
  const [snapshotState, setSnapshotState] = useState<SnapshotPreviewState>({
    sourceKey: '',
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
  const [authRevision, setAuthRevision] = useState(0);

  const effectiveDirectSnapshotUrl = directSnapshotUrl ?? null;
  const selectedProxyRoute = proxySnapshotRoute ?? getAuthenticatedCameraProxyRoute(effectiveDirectSnapshotUrl);
  const effectiveProxyRoute = selectedProxyRoute
    ?? (proxyEnabled && printerId ? `/api/printers/${printerId}/snapshot` : null);
  const isPreviewVisible = isDocumentVisible && isIntersectingViewport;

  useEffect(() => {
    const onStorage = (event: StorageEvent) => {
      if (event.key === 'auth-token' || event.key === 'auth-user-id') setAuthRevision((revision) => revision + 1);
    };
    window.addEventListener('storage', onStorage);
    return () => window.removeEventListener('storage', onStorage);
  }, []);

  useEffect(() => {
    if (typeof document === 'undefined') return;
    const handleVisibilityChange = () => setIsDocumentVisible(getIsDocumentVisible());
    document.addEventListener('visibilitychange', handleVisibilityChange);
    return () => document.removeEventListener('visibilitychange', handleVisibilityChange);
  }, []);

  useEffect(() => {
    if (typeof IntersectionObserver === 'undefined') return;
    const element = previewContainerRef.current;
    if (!element) return;
    const observer = new IntersectionObserver((entries) => {
      setIsIntersectingViewport(entries.some((entry) => entry.isIntersecting));
    });
    observer.observe(element);
    return () => observer.disconnect();
  }, []);

  useEffect(() => {
    return () => {
      if (objectUrlRef.current) URL.revokeObjectURL(objectUrlRef.current);
      objectUrlRef.current = null;
    };
  }, [effectiveProxyRoute]);

  useEffect(() => {
    const revokeCurrentObjectUrl = () => {
      if (!objectUrlRef.current) return;
      URL.revokeObjectURL(objectUrlRef.current);
      objectUrlRef.current = null;
    };

    const useProxy = !!effectiveProxyRoute && (proxyEnabled || directEnabled || !!proxySnapshotRoute || !!selectedProxyRoute);
    if (!useProxy || !effectiveProxyRoute || !isPreviewVisible) {
      if (!useProxy) revokeCurrentObjectUrl();
      return;
    }

    const sourceKey = effectiveProxyRoute;
    const controller = new AbortController();
    let cancelled = false;
    let timeoutId: number | undefined;

    const scheduleNextLoad = (intervalMs: number) => {
      timeoutId = window.setTimeout(() => { void loadSnapshot(); }, intervalMs);
    };
    const loadSnapshot = async () => {
      try {
        const isDefaultPrinterRoute = !!printerId && sourceKey === `/api/printers/${printerId}/snapshot`;
        const blob = isDefaultPrinterRoute
          ? await apiClient.getPrinterSnapshot(printerId, controller.signal)
          : await apiClient.getSnapshotPreview(sourceKey, controller.signal);
        if (cancelled) return;
        const nextObjectUrl = URL.createObjectURL(blob);
        revokeCurrentObjectUrl();
        objectUrlRef.current = nextObjectUrl;
        setSnapshotState({ sourceKey, src: nextObjectUrl, failed: false });
        scheduleNextLoad(refreshIntervalMs);
      } catch {
        if (cancelled) return;
        if (!controller.signal.aborted) {
          setSnapshotState((current) => ({
            sourceKey,
            src: current.sourceKey === sourceKey ? current.src : null,
            failed: true,
          }));
          scheduleNextLoad(refreshIntervalMs * SNAPSHOT_ERROR_BACKOFF_MULTIPLIER);
        }
      }
    };

    void loadSnapshot();
    return () => {
      cancelled = true;
      controller.abort();
      if (timeoutId !== undefined) window.clearTimeout(timeoutId);
    };
  }, [authRevision, directEnabled, effectiveProxyRoute, isPreviewVisible, proxyEnabled, proxySnapshotRoute, refreshIntervalMs, selectedProxyRoute, printerId]);

  useEffect(() => {
    if (!directEnabled || !effectiveDirectSnapshotUrl || getAuthenticatedCameraProxyRoute(effectiveDirectSnapshotUrl)) {
      return;
    }
    if (!isPreviewVisible) return;
    const sourceUrl = effectiveDirectSnapshotUrl;
    let cancelled = false;
    let timeoutId: number | undefined;
    const refreshDirectSnapshot = () => {
      if (cancelled) return;
      setDirectSnapshotState({ sourceUrl, src: getCacheBustedSnapshotUrl(sourceUrl) });
      timeoutId = window.setTimeout(refreshDirectSnapshot, refreshIntervalMs);
    };
    timeoutId = window.setTimeout(refreshDirectSnapshot, 0);
    return () => {
      cancelled = true;
      if (timeoutId !== undefined) window.clearTimeout(timeoutId);
    };
  }, [directEnabled, effectiveDirectSnapshotUrl, isPreviewVisible, refreshIntervalMs]);

  const hasCurrentProxySnapshot = !!effectiveProxyRoute && snapshotState.sourceKey === effectiveProxyRoute;
  const hasCurrentDirectSnapshot = directEnabled && directSnapshotState.sourceUrl === effectiveDirectSnapshotUrl;
  return {
    previewContainerRef,
    snapshotSrc: hasCurrentProxySnapshot
      ? snapshotState.src
      : hasCurrentDirectSnapshot
        ? directSnapshotState.src
        : null,
    snapshotFailed: hasCurrentProxySnapshot ? snapshotState.failed : false,
    isPollingPaused: (proxyEnabled || directEnabled || !!proxySnapshotRoute) && !isPreviewVisible,
  };
}
