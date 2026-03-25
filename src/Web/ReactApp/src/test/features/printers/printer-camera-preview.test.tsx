import { fireEvent, render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { PrinterCameraPreview } from '@/features/printers/components/PrinterCameraPreview';

vi.mock('@/common/utils/apiUrlHelpers', () => ({
  getApiBaseUrl: () => 'http://localhost:5245/api',
  getAuthHeaders: () => ({}),
}));

describe('PrinterCameraPreview', () => {
  beforeEach(() => {
    localStorage.clear();
    vi.restoreAllMocks();
  });

  it('embeds live streams as an image first when a stream URL is available', () => {
    vi.stubGlobal('fetch', vi.fn(() => new Promise(() => {})));

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

  it('falls back to an iframe when the live stream cannot be embedded as an image', () => {
    vi.stubGlobal('fetch', vi.fn(() => new Promise(() => {})));

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

  it('remembers camera rotation changes for the same printer preview', () => {
    vi.stubGlobal('fetch', vi.fn(() => new Promise(() => {})));

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
