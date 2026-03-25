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

  it('embeds live streams with an iframe control when a stream URL is available', () => {
    vi.stubGlobal('fetch', vi.fn(() => new Promise(() => {})));

    render(
      <PrinterCameraPreview
        printerId="printer-1"
        printerName="Printer One"
        cameraStreamUrl="http://printer.local/webcam/?action=stream"
        cameraSnapshotUrl="http://printer.local/webcam/?action=snapshot"
      />
    );

    const iframe = screen.getByTitle('Printer One live camera feed');
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
    const iframe = screen.getByTitle('Printer Rotate live camera feed');
    expect(iframe.getAttribute('style')).toContain('rotate(90deg)');
    expect(iframe.getAttribute('style')).toContain('scale(0.5625)');

    unmount();

    render(
      <PrinterCameraPreview
        printerId="printer-rotate"
        printerName="Printer Rotate"
        cameraStreamUrl="http://printer.local/stream"
        cameraSnapshotUrl="http://printer.local/snapshot"
      />
    );

    const rerenderedIframe = screen.getByTitle('Printer Rotate live camera feed');
    expect(rerenderedIframe.getAttribute('style')).toContain('rotate(90deg)');
    expect(rerenderedIframe.getAttribute('style')).toContain('scale(0.5625)');
  });
});
