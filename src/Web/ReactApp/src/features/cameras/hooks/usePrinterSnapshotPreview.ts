import { useEffect, useRef, useState } from 'react';
import { apiClient } from '@/services/api';
import { getAuthenticatedCameraProxyRoute } from '@/common/auth/authenticatedCameraRoutes';
import {
  useCameraPreviewVisibility,
  type CameraPreviewVisibility,
} from '@/features/cameras/hooks/useCameraPreviewVisibility';

const SNAPSHOT_ERROR_BACKOFF_MULTIPLIER = 3;

interface SnapshotPreviewState {
  sourceKey: string;
  demandSession: number;
  src: string | null;
  failed: boolean;
}

interface DirectSnapshotState {
  sourceUrl: string;
  src: string | null;
}

interface SnapshotPreviewOptions {
  /** False when the snapshot would not be displayed, e.g. while a healthy live stream is shown. */
  active?: boolean;
  /** Shared visibility so the stream and snapshot pause on the same signal. */
  visibility?: CameraPreviewVisibility;
}

const EMPTY_SNAPSHOT_STATE: SnapshotPreviewState = { sourceKey: '', demandSession: 0, src: null, failed: false };

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
  { active = true, visibility }: SnapshotPreviewOptions = {},
) {
  const ownVisibility = useCameraPreviewVisibility();
  const { previewContainerRef, isPreviewVisible } = visibility ?? ownVisibility;
  const [snapshotState, setSnapshotState] = useState<SnapshotPreviewState>(EMPTY_SNAPSHOT_STATE);
  const [directSnapshotState, setDirectSnapshotState] = useState<DirectSnapshotState>({
    sourceUrl: '',
    src: null,
  });
  // Each inactive-to-active transition starts a new session so frames cached before the pause are never shown.
  const [demandSession, setDemandSession] = useState(0);
  const [wasActive, setWasActive] = useState(active);
  if (wasActive !== active) {
    setWasActive(active);
    if (active) setDemandSession((session) => session + 1);
  }
  const objectUrlRef = useRef<string | null>(null);
  const [authRevision, setAuthRevision] = useState(0);

  const effectiveDirectSnapshotUrl = directSnapshotUrl ?? null;
  const selectedProxyRoute = proxySnapshotRoute ?? getAuthenticatedCameraProxyRoute(effectiveDirectSnapshotUrl);
  const effectiveProxyRoute = selectedProxyRoute
    ?? (proxyEnabled && printerId ? `/api/printers/${printerId}/snapshot` : null);

  useEffect(() => {
    const onStorage = (event: StorageEvent) => {
      if (event.key === 'auth-token' || event.key === 'auth-user-id') setAuthRevision((revision) => revision + 1);
    };
    window.addEventListener('storage', onStorage);
    return () => window.removeEventListener('storage', onStorage);
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
    if (!useProxy || !active || !effectiveProxyRoute) {
      revokeCurrentObjectUrl();
      return;
    }
    if (!isPreviewVisible) return;

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
        setSnapshotState({ sourceKey, demandSession, src: nextObjectUrl, failed: false });
        scheduleNextLoad(refreshIntervalMs);
      } catch {
        if (cancelled) return;
        if (!controller.signal.aborted) {
          setSnapshotState((current) => ({
            sourceKey,
            demandSession,
            src: current.sourceKey === sourceKey && current.demandSession === demandSession ? current.src : null,
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
  }, [active, authRevision, demandSession, directEnabled, effectiveProxyRoute, isPreviewVisible, proxyEnabled, proxySnapshotRoute, refreshIntervalMs, selectedProxyRoute, printerId]);

  useEffect(() => {
    if (!directEnabled || !effectiveDirectSnapshotUrl || getAuthenticatedCameraProxyRoute(effectiveDirectSnapshotUrl)) {
      return;
    }
    if (!active || !isPreviewVisible) return;
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
  }, [active, directEnabled, effectiveDirectSnapshotUrl, isPreviewVisible, refreshIntervalMs]);

  const hasCurrentProxySnapshot = active
    && !!effectiveProxyRoute
    && snapshotState.sourceKey === effectiveProxyRoute
    && snapshotState.demandSession === demandSession;
  const hasCurrentDirectSnapshot = active && directEnabled && directSnapshotState.sourceUrl === effectiveDirectSnapshotUrl;
  return {
    previewContainerRef,
    snapshotSrc: hasCurrentProxySnapshot
      ? snapshotState.src
      : hasCurrentDirectSnapshot
        ? directSnapshotState.src
        : null,
    snapshotFailed: hasCurrentProxySnapshot ? snapshotState.failed : false,
    isPollingPaused: (proxyEnabled || directEnabled || !!proxySnapshotRoute) && !isPreviewVisible,
    isPreviewVisible,
  };
}
