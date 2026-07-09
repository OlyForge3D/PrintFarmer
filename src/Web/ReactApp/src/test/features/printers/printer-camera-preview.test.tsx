import { fireEvent, render, screen, waitFor } from '@testing-library/react';
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

describe('PrinterCameraPreview', () => {
  const getPrinterSnapshotMock = vi.mocked(apiClient.getPrinterSnapshot);

  beforeEach(() => {
    vi.useRealTimers();
    localStorage.clear();
    vi.clearAllMocks();
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
        cameraSnapshotUrl="http://printer.local/webcam/?action=snapshot"
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

    await waitFor(() => expect(getPrinterSnapshotMock).toHaveBeenCalledTimes(1));
    expect(getPrinterSnapshotMock).toHaveBeenCalledWith('printer-u1');
    expect(screen.queryByAltText('Snapmaker U1 live camera feed')).toBeNull();

    const snapshot = await screen.findByAltText('Snapmaker U1 camera preview');
    expect(snapshot).toHaveAttribute('src', 'blob:printer-snapshot');
    expect(screen.queryByRole('img', { name: 'Snapmaker U1 live camera feed' })).toBeNull();
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
