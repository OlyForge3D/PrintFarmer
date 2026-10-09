import { fireEvent, render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { CameraCard } from '@/features/printers/components/CameraCard';
import type { Printer } from '@/types/api';

const { camerasMock } = vi.hoisted(() => ({
  camerasMock: { usePrinterCameras: vi.fn() },
}));

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
    expect(screen.getByText('Probe Healthy')).toBeInTheDocument();
    expect(screen.queryByTitle('Live stream active')).not.toBeInTheDocument();
  });
});
