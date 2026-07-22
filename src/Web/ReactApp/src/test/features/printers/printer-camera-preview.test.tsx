import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { PrinterCameraPreview } from '@/features/printers/components/PrinterCameraPreview';
import { apiClient } from '@/services/api';
import {
  CameraAccessMode,
  CameraSnapshotStrategy,
  CameraStreamFormat,
} from '@/types/api';

vi.mock('@/services/api', () => ({
  apiClient: {
    getPrinterSnapshot: vi.fn(),
  },
}));

interface MockIntersectionObserverRecord {
  callback: IntersectionObserverCallback;
  observer: IntersectionObserver;
}

let mockIntersectionObservers: MockIntersectionObserverRecord[] = [];

function setPreviewIntersecting(isIntersecting: boolean, observerIndex = 0) {
  const mock = mockIntersectionObservers[observerIndex];
  if (!mock) {
    throw new Error('No mocked IntersectionObserver was registered.');
  }

  act(() => {
    mock.callback(
      [{ isIntersecting } as IntersectionObserverEntry],
      mock.observer
    );
  });
}

describe('PrinterCameraPreview', () => {
  const getPrinterSnapshotMock = vi.mocked(apiClient.getPrinterSnapshot);

  beforeEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
    mockIntersectionObservers = [];
    localStorage.clear();
    vi.clearAllMocks();
    const MockIntersectionObserver = vi.fn(function (
      this: IntersectionObserver,
      callback: IntersectionObserverCallback
    ) {
      const observer = this as IntersectionObserver & {
        observe: ReturnType<typeof vi.fn>;
        unobserve: ReturnType<typeof vi.fn>;
        disconnect: ReturnType<typeof vi.fn>;
        takeRecords: ReturnType<typeof vi.fn>;
        root: null;
        rootMargin: string;
        thresholds: number[];
      };
      observer.observe = vi.fn();
      observer.unobserve = vi.fn();
      observer.disconnect = vi.fn();
      observer.takeRecords = vi.fn(() => []);
      observer.root = null;
      observer.rootMargin = '';
      observer.thresholds = [];
      mockIntersectionObservers.push({ callback, observer });
    });
    vi.stubGlobal('IntersectionObserver', MockIntersectionObserver);
    Object.defineProperty(URL, 'createObjectURL', {
      configurable: true,
      value: vi.fn(() => 'blob:printer-snapshot'),
    });
    Object.defineProperty(URL, 'revokeObjectURL', {
      configurable: true,
      value: vi.fn(),
    });
  });

  it('embeds live streams as an image first when a stream URL is available', () => {
    render(
      <PrinterCameraPreview
        printerId="printer-1"
        printerName="Printer One"
        cameraStreamUrl="http://printer.local/webcam/?action=stream"
        cameraSnapshotUrl="http://printer.local/webcam/?action=snapshot"
      />
    );

    const stream = screen.getByAltText('Printer One live camera feed');
    expect(stream.tagName).toBe('IMG');
  });

  it('uses the MJPEG stream without polling snapshots when the contract supports streaming', () => {
    render(
      <PrinterCameraPreview
        printerId="printer-mjpeg"
        printerName="Printer MJPEG"
        cameraStreamUrl="http://printer.local/webcam/?action=stream"
        cameraSnapshotUrl="http://printer.local/webcam/?action=snapshot"
        cameraAccessMode={CameraAccessMode.StreamAndSnapshot}
        cameraStreamFormat={CameraStreamFormat.Mjpeg}
        cameraSnapshotStrategy={CameraSnapshotStrategy.DirectUrl}
      />
    );

    const stream = screen.getByAltText('Printer MJPEG live camera feed');
    expect(stream).toHaveAttribute('src', 'http://printer.local/webcam/?action=stream');
    expect(getPrinterSnapshotMock).not.toHaveBeenCalled();
  });
  it('falls back to an iframe when the live stream cannot be embedded as an image', () => {
    render(
      <PrinterCameraPreview
        printerId="printer-iframe"
        printerName="Printer Fallback"
        cameraStreamUrl="http://printer.local/webcam/?action=stream"
        cameraSnapshotUrl={null}
      />
    );

    fireEvent.error(screen.getByAltText('Printer Fallback live camera feed'));

    const iframe = screen.getByTitle('Printer Fallback live camera feed');
    expect(iframe.tagName).toBe('IFRAME');
  });

  it('polls the printer snapshot endpoint for snapshot-only cameras without using the stream URL', async () => {
    getPrinterSnapshotMock.mockResolvedValue(new Blob(['jpeg'], { type: 'image/jpeg' }));

    render(
      <PrinterCameraPreview
        printerId="printer-u1"
        printerName="Snapmaker U1"
        cameraStreamUrl="http://printer.local/broken-stream"
        cameraSnapshotUrl={null}
        cameraAccessMode={CameraAccessMode.SnapshotOnly}
        cameraStreamFormat={CameraStreamFormat.Unsupported}
        cameraSnapshotStrategy={CameraSnapshotStrategy.SnapmakerU1MonitorJpeg}
        isPrinting
      />
    );

    setPreviewIntersecting(true);

    await waitFor(() => expect(getPrinterSnapshotMock).toHaveBeenCalledTimes(1));
    expect(getPrinterSnapshotMock).toHaveBeenCalledWith('printer-u1');
    expect(screen.queryByAltText('Snapmaker U1 live camera feed')).toBeNull();

    const snapshot = await screen.findByAltText('Snapmaker U1 camera preview');
    expect(snapshot).toHaveAttribute('src', 'blob:printer-snapshot');
    expect(screen.queryByRole('img', { name: 'Snapmaker U1 live camera feed' })).toBeNull();
  });

  it('pauses snapshot polling when the preview leaves the viewport', async () => {
    vi.useFakeTimers();
    getPrinterSnapshotMock.mockResolvedValue(new Blob(['jpeg'], { type: 'image/jpeg' }));

    render(
      <PrinterCameraPreview
        printerId="printer-offscreen"
        printerName="Offscreen U1"
        cameraStreamUrl={null}
        cameraSnapshotUrl={null}
        cameraAccessMode={CameraAccessMode.SnapshotOnly}
        cameraSnapshotStrategy={CameraSnapshotStrategy.SnapmakerU1MonitorJpeg}
        isPrinting
      />
    );

    setPreviewIntersecting(true);
    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
    });
    expect(getPrinterSnapshotMock).toHaveBeenCalledTimes(1);

    setPreviewIntersecting(false);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(20_000);
    });

    expect(getPrinterSnapshotMock).toHaveBeenCalledTimes(1);
    vi.useRealTimers();
  });

  it('renders SnapshotOnly DirectUrl snapshots directly without polling the backend proxy', async () => {
    render(
      <PrinterCameraPreview
        printerId="printer-direct"
        printerName="Direct Snapshot"
        cameraStreamUrl={null}
        cameraSnapshotUrl="http://printer.local/snapshot.jpg"
        cameraAccessMode={CameraAccessMode.SnapshotOnly}
        cameraSnapshotStrategy={CameraSnapshotStrategy.DirectUrl}
      />
    );

    setPreviewIntersecting(true);

    const snapshot = await screen.findByAltText('Direct Snapshot camera preview');
    expect(snapshot.getAttribute('src')).toMatch(/^http:\/\/printer\.local\/snapshot\.jpg(?:\?_=\d+)?$/);
    expect(getPrinterSnapshotMock).not.toHaveBeenCalled();
  });

  it('falls back from a failed MJPEG stream image to the snapshot when available', async () => {
    render(
      <PrinterCameraPreview
        printerId="printer-stream-snapshot"
        printerName="Stream Snapshot"
        cameraStreamUrl="http://printer.local/stream"
        cameraSnapshotUrl="http://printer.local/snapshot.jpg"
        cameraAccessMode={CameraAccessMode.StreamAndSnapshot}
        cameraStreamFormat={CameraStreamFormat.Mjpeg}
        cameraSnapshotStrategy={CameraSnapshotStrategy.DirectUrl}
      />
    );

    setPreviewIntersecting(true);
    fireEvent.error(screen.getByAltText('Stream Snapshot live camera feed'));

    const snapshot = await screen.findByAltText('Stream Snapshot camera preview');
    expect(snapshot.getAttribute('src')).toMatch(/^http:\/\/printer\.local\/snapshot\.jpg(?:\?_=\d+)?$/);
    expect(screen.queryByTitle('Stream Snapshot live camera feed')).toBeNull();
  });

  it('defensively renders a snapshot for UnsupportedStream when a snapshot URL is present', async () => {
    render(
      <PrinterCameraPreview
        printerId="printer-unsupported-snapshot"
        printerName="Unsupported Snapshot"
        cameraStreamUrl="rtsp://printer.local/live"
        cameraSnapshotUrl="http://printer.local/snapshot.jpg"
        cameraAccessMode={CameraAccessMode.UnsupportedStream}
        cameraStreamFormat={CameraStreamFormat.Unsupported}
        cameraSnapshotStrategy={CameraSnapshotStrategy.DirectUrl}
      />
    );

    setPreviewIntersecting(true);

    const snapshot = await screen.findByAltText('Unsupported Snapshot camera preview');
    expect(snapshot.getAttribute('src')).toMatch(/^http:\/\/printer\.local\/snapshot\.jpg(?:\?_=\d+)?$/);
    expect(screen.queryByText('No live preview available')).toBeNull();
    expect(getPrinterSnapshotMock).not.toHaveBeenCalled();
  });

  it('shows a placeholder for unsupported live streams instead of rendering a broken image', () => {
    render(
      <PrinterCameraPreview
        printerId="printer-unsupported"
        printerName="Printer Unsupported"
        cameraStreamUrl="rtsp://printer.local/live"
        cameraSnapshotUrl={null}
        cameraAccessMode={CameraAccessMode.UnsupportedStream}
        cameraStreamFormat={CameraStreamFormat.Unsupported}
        cameraSnapshotStrategy={CameraSnapshotStrategy.None}
      />
    );

    expect(screen.getByText('No live preview available')).toBeTruthy();
    expect(screen.queryByAltText('Printer Unsupported live camera feed')).toBeNull();
    expect(getPrinterSnapshotMock).not.toHaveBeenCalled();
  });

  it('remembers camera rotation changes for the same printer preview', () => {
    const { unmount } = render(
      <PrinterCameraPreview
        printerId="printer-rotate"
        printerName="Printer Rotate"
        cameraStreamUrl="http://printer.local/stream"
        cameraSnapshotUrl="http://printer.local/snapshot"
      />
    );

    fireEvent.click(screen.getByLabelText('Rotate camera clockwise'));
    const stream = screen.getByAltText('Printer Rotate live camera feed');
    expect(stream.className).toContain('rotate-90');
    expect(stream.className).toContain('scale-[0.5625]');

    unmount();

    render(
      <PrinterCameraPreview
        printerId="printer-rotate"
        printerName="Printer Rotate"
        cameraStreamUrl="http://printer.local/stream"
        cameraSnapshotUrl="http://printer.local/snapshot"
      />
    );

    const rerenderedStream = screen.getByAltText('Printer Rotate live camera feed');
    expect(rerenderedStream.className).toContain('rotate-90');
    expect(rerenderedStream.className).toContain('scale-[0.5625]');
  });
});
