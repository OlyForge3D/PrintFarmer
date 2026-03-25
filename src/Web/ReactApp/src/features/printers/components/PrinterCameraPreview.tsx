import type { ReactNode } from 'react';
import { useEffect, useRef, useState } from 'react';
import clsx from 'clsx';
import { RotateCw } from 'lucide-react';
import { Button } from '@/common/components/ui';
import { CameraIcon, ExternalLinkIcon, ImageIcon, VideoIcon } from '@/common/components/icons/MdiIcons';
import { getApiBaseUrl, getAuthHeaders } from '@/common/utils/apiUrlHelpers';
import {
  getCameraMediaTransformStyle,
  useCameraViewPreferences,
} from '@/features/cameras/hooks/useCameraViewPreferences';

const ACTIVE_PREVIEW_REFRESH_MS = 4_000;
const IDLE_PREVIEW_REFRESH_MS = 12_000;

interface PrinterCameraPreviewProps {
  printerId: string;
  printerName: string;
  cameraStreamUrl?: string | null;
  cameraSnapshotUrl?: string | null;
  isPrinting?: boolean;
  className?: string;
  overlay?: ReactNode;
}

function usePrinterSnapshotPreview(printerId: string, enabled: boolean, refreshIntervalMs: number) {
  const [snapshotSrc, setSnapshotSrc] = useState<string | null>(null);
  const [proxyFailed, setProxyFailed] = useState(false);
  const objectUrlRef = useRef<string | null>(null);

  useEffect(() => {
    const revokeCurrentObjectUrl = () => {
      if (!objectUrlRef.current) {
        return;
      }

      URL.revokeObjectURL(objectUrlRef.current);
      objectUrlRef.current = null;
    };

    if (!enabled) {
      revokeCurrentObjectUrl();
      setSnapshotSrc(null);
      setProxyFailed(false);
      return;
    }

    let cancelled = false;
    const apiBaseUrl = getApiBaseUrl().replace(/\/$/, '');
    const snapshotEndpoint = `${apiBaseUrl}/printers/${encodeURIComponent(printerId)}/snapshot`;

    const loadSnapshot = async () => {
      try {
        const response = await fetch(snapshotEndpoint, {
          headers: getAuthHeaders() as Record<string, string>,
          cache: 'no-store',
        });

        if (!response.ok) {
          throw new Error(`Snapshot request failed with ${response.status}`);
        }

        const blob = await response.blob();
        if (cancelled) {
          return;
        }

        const nextObjectUrl = URL.createObjectURL(blob);
        revokeCurrentObjectUrl();
        objectUrlRef.current = nextObjectUrl;
        setSnapshotSrc(nextObjectUrl);
        setProxyFailed(false);
      } catch {
        if (cancelled) {
          return;
        }

        revokeCurrentObjectUrl();
        setSnapshotSrc(null);
        setProxyFailed(true);
      }
    };

    void loadSnapshot();
    const intervalId = window.setInterval(() => {
      void loadSnapshot();
    }, refreshIntervalMs);

    return () => {
      cancelled = true;
      window.clearInterval(intervalId);
      revokeCurrentObjectUrl();
    };
  }, [enabled, printerId, refreshIntervalMs]);

  return {
    snapshotSrc,
    proxyFailed,
  };
}

export function PrinterCameraPreview({
  printerId,
  printerName,
  cameraStreamUrl,
  cameraSnapshotUrl,
  isPrinting = false,
  className,
  overlay,
}: PrinterCameraPreviewProps) {
  const hasCameraSource = !!(cameraStreamUrl || cameraSnapshotUrl);
  const refreshIntervalMs = isPrinting ? ACTIVE_PREVIEW_REFRESH_MS : IDLE_PREVIEW_REFRESH_MS;
  const { snapshotSrc, proxyFailed } = usePrinterSnapshotPreview(
    printerId,
    hasCameraSource,
    refreshIntervalMs
  );
  const rawSnapshotKey = `${printerId}:${cameraSnapshotUrl ?? ''}`;
  const [failedRawSnapshotKey, setFailedRawSnapshotKey] = useState<string | null>(null);
  const fallbackSnapshotSrc =
    failedRawSnapshotKey === rawSnapshotKey ? null : (cameraSnapshotUrl ?? null);
  const previewSrc = snapshotSrc ?? fallbackSnapshotSrc;
  const hasStream = !!cameraStreamUrl;
  const hasSnapshot = !!previewSrc;
  const {
    cameraMode,
    setCameraMode,
    rotation,
    rotateClockwise,
    hasModeToggle,
    hasMedia,
  } = useCameraViewPreferences({
    preferenceKey: `printer:${printerId}`,
    defaultMode: hasStream ? 'stream' : 'snapshot',
    hasStream,
    hasSnapshot,
  });
  const externalUrl = cameraStreamUrl ?? cameraSnapshotUrl ?? null;
  const mediaStyle = getCameraMediaTransformStyle(rotation);
  const showLiveStream = cameraMode === 'stream' && hasStream;

  return (
    <div
      className={clsx(
        'relative overflow-hidden rounded-xl border border-pf-border bg-pf-bg-2/40',
        className
      )}
    >
      <div className="relative aspect-video w-full overflow-hidden bg-pf-bg-0">
        {showLiveStream && cameraStreamUrl ? (
          <iframe
            src={cameraStreamUrl}
            title={`${printerName} live camera feed`}
            className="border-0 bg-black"
            style={mediaStyle}
            loading="lazy"
            referrerPolicy="no-referrer"
          />
        ) : previewSrc ? (
          <img
            src={previewSrc}
            alt={`${printerName} camera preview`}
            className="object-cover"
            style={mediaStyle}
            loading="lazy"
            onError={() => {
              if (snapshotSrc) {
                return;
              }

              setFailedRawSnapshotKey(rawSnapshotKey);
            }}
          />
        ) : (
          <div className="absolute inset-0 flex flex-col items-center justify-center px-4 text-center text-pf-text-secondary">
            <CameraIcon className="mb-2 h-8 w-8 opacity-45" />
            <p className="text-sm font-medium">
              {hasCameraSource
                ? showLiveStream ? 'Live stream unavailable' : 'Camera preview unavailable'
                : 'No camera configured'}
            </p>
            {hasCameraSource && (
              <p className="mt-1 text-xs text-pf-text-tertiary">
                {proxyFailed
                  ? 'The embedded preview fell back to snapshots. You can still switch to live mode or open the feed in a new tab.'
                  : 'Use live mode for the embedded stream or open the feed in a new tab.'}
              </p>
            )}
          </div>
        )}

        <div className="pointer-events-none absolute inset-0 bg-gradient-to-t from-slate-950/72 via-transparent to-slate-950/28" />

        {overlay && (
          <div className="absolute left-3 top-3 z-10 max-w-[calc(100%-6rem)]">
            {overlay}
          </div>
        )}

        {externalUrl && (
          <a
            href={externalUrl}
            target="_blank"
            rel="noopener noreferrer"
            className="absolute right-3 top-3 z-10 inline-flex items-center gap-1 rounded-full border border-white/10 bg-slate-950/70 px-2.5 py-1 text-[11px] font-semibold text-white shadow-lg shadow-black/30 backdrop-blur-sm transition hover:border-white/20 hover:bg-slate-950/82"
            title="Open camera feed in a new tab"
            aria-label={`Open ${printerName} camera feed in a new tab`}
          >
            <ExternalLinkIcon className="h-3.5 w-3.5" />
            <span>Open live</span>
          </a>
        )}

        {(showLiveStream || previewSrc) && (
          <div className="absolute bottom-3 left-3 z-10 flex items-center gap-2">
            <div className="inline-flex items-center gap-2 rounded-full border border-white/10 bg-slate-950/70 px-2.5 py-1 text-[11px] text-white/78 shadow-lg shadow-black/25 backdrop-blur-sm">
              <span
                className={clsx(
                  'h-1.5 w-1.5 rounded-full',
                  showLiveStream ? 'bg-pf-success' : 'bg-pf-accent'
                )}
                aria-hidden="true"
              />
              <span>{showLiveStream ? 'Embedded live stream' : 'Auto-refreshing preview'}</span>
            </div>
            {hasMedia && (
              <Button
                type="button"
                variant="ghost"
                size="sm"
                onClick={rotateClockwise}
                className="h-8 w-8 rounded-full border border-white/10 bg-slate-950/70 p-0 text-white shadow-lg shadow-black/25 backdrop-blur-sm enabled:hover:bg-slate-950/82"
                title="Rotate camera clockwise"
                aria-label="Rotate camera clockwise"
                iconCenter={<RotateCw className="h-3.5 w-3.5" />}
              />
            )}
          </div>
        )}

        {hasModeToggle && (
          <div className="absolute bottom-3 right-3 z-10 flex gap-1 rounded-full border border-white/10 bg-slate-950/70 p-1 shadow-lg shadow-black/25 backdrop-blur-sm">
            <Button
              type="button"
              variant={cameraMode === 'snapshot' ? 'primary' : 'ghost'}
              size="sm"
              onClick={() => setCameraMode('snapshot')}
              className="h-8 w-8 rounded-full p-0 text-white enabled:hover:bg-slate-900/80"
              title="Show snapshot preview"
              aria-label="Show snapshot preview"
              iconCenter={<ImageIcon className="h-3.5 w-3.5" />}
            />
            <Button
              type="button"
              variant={cameraMode === 'stream' ? 'primary' : 'ghost'}
              size="sm"
              onClick={() => setCameraMode('stream')}
              className="h-8 w-8 rounded-full p-0 text-white enabled:hover:bg-slate-900/80"
              title="Show live stream"
              aria-label="Show live stream"
              iconCenter={<VideoIcon className="h-3.5 w-3.5" />}
            />
          </div>
        )}
      </div>
    </div>
  );
}
