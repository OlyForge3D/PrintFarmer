import { fireEvent, render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { CameraCard } from '@/features/printers/components/CameraCard';
import type { Printer } from '@/types/api';

const { camerasMock, apiMock } = vi.hoisted(() => ({
  camerasMock: { usePrinterCameras: vi.fn() },
  apiMock: { getPrinterSnapshot: vi.fn(), getSnapshotPreview: vi.fn() },
}));

vi.mock('@/services/api', () => ({ apiClient: apiMock }));
vi.mock('@/features/cameras/hooks/usePrinterCameras', () => ({
  usePrinterCameras: camerasMock.usePrinterCameras,
}));

const printer = {
  id: 'printer-card-stream-snapshot',
  name: 'Stream Snapshot Printer',
  isOnline: true,
  state: 'Idle',
  cameraStreamUrl: 'http://printer.local/stream',
  cameraSnapshotUrl: 'http://printer.local/snapshot.jpg',
} as Printer;

describe('CameraCard preview fallback', () => {
  beforeEach(() => {
    localStorage.clear();
    localStorage.setItem(`printfarmer-camera-mode:printer:${printer.id}`, 'snapshot');
    camerasMock.usePrinterCameras.mockReset().mockReturnValue({
      data: [{ healthStatus: 'Healthy' }],
    });
    apiMock.getPrinterSnapshot.mockReset();
    apiMock.getSnapshotPreview.mockReset();
  });

  it('keeps a stream-only camera in stream mode without fetching or falling back to snapshots after failure', () => {
    render(<CameraCard printer={{ ...printer, cameraSnapshotUrl: undefined }} />);

    fireEvent.error(screen.getByAltText('Stream Snapshot Printer live camera feed'));

    expect(screen.getByRole('status', { name: 'Stream Snapshot Printer: Live stream unavailable' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Snapshot mode' })).not.toBeInTheDocument();
    expect(screen.queryByAltText('Stream Snapshot Printer camera preview')).not.toBeInTheDocument();
    expect(screen.queryByText(/showing snapshot/)).not.toBeInTheDocument();
    expect(screen.queryByTitle('Live stream active')).not.toBeInTheDocument();
    expect(apiMock.getPrinterSnapshot).not.toHaveBeenCalled();
    expect(apiMock.getSnapshotPreview).not.toHaveBeenCalled();
  });

  it('selects the displayed snapshot and reports stream failure separately from probe health', async () => {
    render(<CameraCard printer={printer} />);

    fireEvent.click(screen.getByRole('button', { name: 'Stream mode' }));
    fireEvent.error(screen.getByAltText('Stream Snapshot Printer live camera feed'));

    const snapshot = await screen.findByAltText('Stream Snapshot Printer camera preview');
    expect(snapshot.getAttribute('src')).toMatch(/^http:\/\/printer\.local\/snapshot\.jpg(?:\?_=\d+)?$/);
    expect(screen.getByRole('button', { name: 'Snapshot mode' })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('button', { name: 'Stream mode' })).toHaveAttribute('aria-pressed', 'false');
    expect(screen.getAllByText('Live stream unavailable · showing snapshot')).toHaveLength(2);
    expect(screen.getByText('Preview failed')).not.toHaveClass('sr-only');
    expect(screen.getByRole('status', { name: 'Preview failed · probe healthy' })).toBeInTheDocument();
    expect(screen.queryByTitle('Live stream active')).not.toBeInTheDocument();
  });

  it('uses compact accessible icons without routine probe or capability chips', () => {
    render(<CameraCard printer={printer} />);

    expect(screen.getByRole('button', { name: 'Snapshot mode' }).textContent).toBe('');
    expect(screen.getByRole('button', { name: 'Stream mode' }).textContent).toBe('');
    expect(screen.getByText('Probe healthy')).toHaveClass('sr-only');
  });
});
