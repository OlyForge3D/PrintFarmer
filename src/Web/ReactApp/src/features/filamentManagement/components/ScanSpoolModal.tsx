import { useState, useEffect, useRef, useCallback } from 'react';
import { Html5Qrcode, Html5QrcodeScannerState } from 'html5-qrcode';
import { Modal } from '@/common/components/modals/Modal';
import { Button, Alert, Spinner } from '@/common/components/ui';
import { CameraIcon, RefreshIcon, CloseIcon } from '@/common/components/icons/MdiIcons';
import { apiClient } from '@/services/api';
import { toast } from 'sonner';
import type { SpoolmanSpool } from '@/types/api';
import clsx from 'clsx';

interface ScanSpoolModalProps {
  isOpen: boolean;
  onClose: () => void;
  /** Called when a spool is found and the user wants to view it. */
  onSpoolFound?: (spool: SpoolmanSpool) => void;
}

type ScanState =
  | { status: 'initializing' }
  | { status: 'scanning' }
  | { status: 'permission-denied' }
  | { status: 'no-camera' }
  | { status: 'error'; message: string }
  | { status: 'looking-up'; scannedValue: string }
  | { status: 'found'; spool: SpoolmanSpool; scannedValue: string }
  | { status: 'not-found'; scannedValue: string };

const READER_ELEMENT_ID = 'pf-qr-reader';
const SCAN_TIMEOUT_MS = 60_000;

/**
 * Extracts a spool ID from a scanned value.
 * Supports plain numeric IDs, Spoolman URLs (e.g. `.../spool/123`), and raw strings.
 */
function extractSpoolId(value: string): number | null {
  const trimmed = value.trim();

  // Plain numeric
  const asNumber = Number(trimmed);
  if (Number.isFinite(asNumber) && asNumber > 0) return Math.floor(asNumber);

  // Spoolman URL pattern: .../spool/123 or .../spools/123
  const urlMatch = trimmed.match(/\/spools?\/(\d+)/i);
  if (urlMatch) return Number(urlMatch[1]);

  return null;
}

/**
 * ScanSpoolModal — Opens a camera viewfinder for QR/barcode scanning.
 * On decode, looks up the scanned value against the Spoolman inventory.
 *
 * Built with accessibility in mind — manual testing recommended.
 */
export function ScanSpoolModal({ isOpen, onClose, onSpoolFound }: ScanSpoolModalProps) {
  const [scanState, setScanState] = useState<ScanState>({ status: 'initializing' });
  const [cameras, setCameras] = useState<Array<{ id: string; label: string }>>([]);
  const [activeCameraIdx, setActiveCameraIdx] = useState(0);
  const scannerRef = useRef<Html5Qrcode | null>(null);
  const timeoutRef = useRef<ReturnType<typeof setTimeout>>();
  const hasDecodedRef = useRef(false);

  const cleanup = useCallback(async () => {
    if (timeoutRef.current) {
      clearTimeout(timeoutRef.current);
      timeoutRef.current = undefined;
    }
    const scanner = scannerRef.current;
    if (scanner) {
      try {
        const state = scanner.getState();
        if (state === Html5QrcodeScannerState.SCANNING || state === Html5QrcodeScannerState.PAUSED) {
          await scanner.stop();
        }
      } catch {
        // scanner may already be stopped
      }
      try { scanner.clear(); } catch { /* noop */ }
      scannerRef.current = null;
    }
  }, []);

  const lookUpSpool = useCallback(async (scannedValue: string) => {
    setScanState({ status: 'looking-up', scannedValue });

    try {
      const spoolId = extractSpoolId(scannedValue);
      let matchedSpool: SpoolmanSpool | undefined;

      if (spoolId !== null) {
        // Search by ID
        const result = await apiClient.getSpools({ search: String(spoolId), limit: 50 });
        matchedSpool = result.items.find(s => s.id === spoolId);
      }

      if (!matchedSpool) {
        // Fallback: broad text search
        const result = await apiClient.getSpools({ search: scannedValue, limit: 20 });
        matchedSpool = result.items[0];
      }

      if (matchedSpool) {
        setScanState({ status: 'found', spool: matchedSpool, scannedValue });
        toast.success(`Found spool: ${matchedSpool.filamentName ?? matchedSpool.name ?? `#${matchedSpool.id}`}`);
      } else {
        setScanState({ status: 'not-found', scannedValue });
      }
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Unknown error during lookup';
      setScanState({ status: 'error', message: `Lookup failed: ${message}` });
    }
  }, []);

  const startScanning = useCallback(async (cameraId?: string) => {
    await cleanup();
    hasDecodedRef.current = false;
    setScanState({ status: 'initializing' });

    try {
      const devices = await Html5Qrcode.getCameras();
      if (devices.length === 0) {
        setScanState({ status: 'no-camera' });
        return;
      }

      setCameras(devices);
      const selectedDevice = cameraId ?? devices[activeCameraIdx]?.id ?? devices[0].id;

      const scanner = new Html5Qrcode(READER_ELEMENT_ID);
      scannerRef.current = scanner;
      setScanState({ status: 'scanning' });

      await scanner.start(
        selectedDevice,
        {
          fps: 10,
          qrbox: { width: 250, height: 250 },
          aspectRatio: 1,
        },
        (decodedText) => {
          if (hasDecodedRef.current) return;
          hasDecodedRef.current = true;
          scanner.stop().catch(() => { /* noop */ });
          lookUpSpool(decodedText);
        },
        // error callback — not actionable per-frame, silently ignored
        () => {},
      );

      // Auto-timeout
      timeoutRef.current = setTimeout(() => {
        if (!hasDecodedRef.current) {
          scanner.stop().catch(() => { /* noop */ });
          setScanState({ status: 'error', message: 'Scan timed out. No code detected — try repositioning the code or enter the ID manually.' });
        }
      }, SCAN_TIMEOUT_MS);
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      if (message.includes('NotAllowedError') || message.includes('Permission')) {
        setScanState({ status: 'permission-denied' });
      } else if (message.includes('NotFoundError') || message.includes('Requested device not found')) {
        setScanState({ status: 'no-camera' });
      } else {
        setScanState({ status: 'error', message });
      }
    }
  }, [activeCameraIdx, cleanup, lookUpSpool]);

  // Start/stop scanner when modal opens/closes
  useEffect(() => {
    if (isOpen) {
      // Small delay lets the DOM element render before Html5Qrcode binds
      const timer = setTimeout(() => startScanning(), 150);
      return () => clearTimeout(timer);
    }
    cleanup();
    setScanState({ status: 'initializing' });
    return undefined;
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isOpen]);

  // Cleanup on unmount
  useEffect(() => () => { cleanup(); }, [cleanup]);

  const switchCamera = useCallback(() => {
    if (cameras.length < 2) return;
    const nextIdx = (activeCameraIdx + 1) % cameras.length;
    setActiveCameraIdx(nextIdx);
    startScanning(cameras[nextIdx].id);
  }, [cameras, activeCameraIdx, startScanning]);

  const handleRetry = useCallback(() => {
    startScanning();
  }, [startScanning]);

  const handleViewSpool = useCallback(() => {
    if (scanState.status === 'found') {
      onSpoolFound?.(scanState.spool);
      onClose();
    }
  }, [scanState, onSpoolFound, onClose]);

  const isActive = scanState.status === 'scanning' || scanState.status === 'initializing';

  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title="Scan Spool"
      size="md"
      closeOnEscape
      titleIcon={<CameraIcon className="w-5 h-5 text-pf-accent" />}
      footer={
        <div className="flex items-center justify-between w-full">
          <div className="flex gap-2">
            {cameras.length > 1 && isActive && (
              <Button variant="subtle" size="sm" onClick={switchCamera} iconLeft={<RefreshIcon className="w-4 h-4" />}>
                Switch Camera
              </Button>
            )}
          </div>
          <div className="flex gap-2">
            {scanState.status === 'found' && (
              <Button variant="primary" onClick={handleViewSpool}>
                View Spool
              </Button>
            )}
            <Button variant="secondary" onClick={onClose} iconLeft={<CloseIcon className="w-4 h-4" />}>
              Close
            </Button>
          </div>
        </div>
      }
    >
      <div className="flex flex-col items-center gap-4">
        {/* Camera viewfinder */}
        <div
          className={clsx(
            'relative w-full rounded-lg overflow-hidden bg-black',
            !isActive && 'hidden',
          )}
        >
          <div id={READER_ELEMENT_ID} className="w-full" />
          {scanState.status === 'initializing' && (
            <div className="absolute inset-0 flex items-center justify-center bg-pf-bg-0/80">
              <Spinner size="lg" />
            </div>
          )}
        </div>

        {/* Permission denied */}
        {scanState.status === 'permission-denied' && (
          <Alert type="warning" title="Camera access denied">
            <p className="mb-2">
              PrintFarmer needs camera access to scan QR codes and barcodes.
            </p>
            <ol className="list-decimal list-inside space-y-1 text-sm text-pf-text-secondary">
              <li>Open your browser settings</li>
              <li>Find &quot;Site Settings&quot; or &quot;Permissions&quot;</li>
              <li>Allow camera access for this site</li>
              <li>Reload the page and try again</li>
            </ol>
            <Button variant="subtle" size="sm" className="mt-3" onClick={handleRetry}>
              Try Again
            </Button>
          </Alert>
        )}

        {/* No camera */}
        {scanState.status === 'no-camera' && (
          <Alert type="info" title="No camera detected">
            A camera is required to scan QR codes and barcodes.
            Connect a webcam or use a device with a built-in camera.
          </Alert>
        )}

        {/* Error / timeout */}
        {scanState.status === 'error' && (
          <Alert type="error" title="Scan error">
            <p className="mb-2">{scanState.message}</p>
            <Button variant="subtle" size="sm" onClick={handleRetry}>
              Retry
            </Button>
          </Alert>
        )}

        {/* Looking up */}
        {scanState.status === 'looking-up' && (
          <div className="flex flex-col items-center gap-3 py-6">
            <Spinner size="lg" />
            <p className="text-pf-text-secondary text-sm">
              Looking up: <span className="font-mono text-pf-text-primary">{scanState.scannedValue}</span>
            </p>
          </div>
        )}

        {/* Spool found */}
        {scanState.status === 'found' && (
          <div className="w-full rounded-lg border border-pf-success bg-pf-success-bg p-4">
            <h3 className="font-semibold text-pf-text-primary mb-2">Spool Found</h3>
            <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-sm">
              <dt className="text-pf-text-secondary">ID</dt>
              <dd className="font-mono">{scanState.spool.id}</dd>
              {scanState.spool.filamentName && (
                <>
                  <dt className="text-pf-text-secondary">Filament</dt>
                  <dd>{scanState.spool.filamentName}</dd>
                </>
              )}
              {scanState.spool.material && (
                <>
                  <dt className="text-pf-text-secondary">Material</dt>
                  <dd>{scanState.spool.material}</dd>
                </>
              )}
              {scanState.spool.vendor && (
                <>
                  <dt className="text-pf-text-secondary">Vendor</dt>
                  <dd>{scanState.spool.vendor}</dd>
                </>
              )}
              {scanState.spool.remainingWeightG != null && (
                <>
                  <dt className="text-pf-text-secondary">Remaining</dt>
                  <dd>{Math.round(scanState.spool.remainingWeightG)}g</dd>
                </>
              )}
            </dl>
          </div>
        )}

        {/* Not found */}
        {scanState.status === 'not-found' && (
          <div className="w-full">
            <Alert type="warning" title="Spool not found">
              <p className="mb-2">
                No spool matched the scanned value: <span className="font-mono">{scanState.scannedValue}</span>
              </p>
            </Alert>
            <div className="flex gap-2 mt-3 justify-center">
              <Button variant="subtle" size="sm" onClick={handleRetry}>
                Scan Again
              </Button>
            </div>
          </div>
        )}
      </div>
    </Modal>
  );
}
