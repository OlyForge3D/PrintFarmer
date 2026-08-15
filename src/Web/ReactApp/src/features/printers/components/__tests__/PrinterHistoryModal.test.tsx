import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { PrinterHistoryModal } from '@/features/printers/components/PrinterHistoryModal';
import { apiClient } from '@/services/api';
import type { HistoryJob, Printer } from '@/types/api';

const usePrinterHistory = vi.fn();
const usePrinterHistoryTotals = vi.fn();

vi.mock('@/common/hooks/useApi', () => ({
  usePrinterHistory: (...args: unknown[]) => usePrinterHistory(...args),
  usePrinterHistoryTotals: (...args: unknown[]) => usePrinterHistoryTotals(...args),
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    getPrinterHistoryThumbnail: vi.fn(),
  },
}));

const printer = {
  id: 'printer-1',
  name: 'Prusa MK4',
} as Printer;

function createJob(thumbnailUrl?: string): HistoryJob {
  return {
    jobId: 'job-1',
    filename: 'calibration.gcode',
    status: 'completed',
    startTime: 1_700_000_000,
    printDuration: 60,
    filamentUsed: 0,
    thumbnailUrl,
  } as HistoryJob;
}

function renderHistory(job: HistoryJob) {
  usePrinterHistory.mockReturnValue({
    data: { count: 1, jobs: [job] },
    isLoading: false,
    error: null,
    refetch: vi.fn(),
  });
  usePrinterHistoryTotals.mockReturnValue({
    data: null,
    isLoading: false,
  });

  return render(
    <PrinterHistoryModal
      isOpen
      onClose={vi.fn()}
      printer={printer}
    />,
  );
}

describe('PrinterHistoryModal thumbnails', () => {
  const getPrinterHistoryThumbnailMock = vi.mocked(
    apiClient.getPrinterHistoryThumbnail
  );

  beforeEach(() => {
    vi.clearAllMocks();
    Object.defineProperty(URL, 'createObjectURL', {
      configurable: true,
      value: vi.fn(() => 'blob:history-thumbnail'),
    });
    Object.defineProperty(URL, 'revokeObjectURL', {
      configurable: true,
      value: vi.fn(),
    });
  });

  it('renders the established placeholder when a thumbnail is missing', () => {
    renderHistory(createJob());

    expect(
      screen.getByRole('img', {
        name: 'Thumbnail unavailable for calibration.gcode',
      }),
    ).toBeInTheDocument();
    expect(getPrinterHistoryThumbnailMock).not.toHaveBeenCalled();
  });

  it('fetches an authenticated blob and renders its object URL', async () => {
    const blob = new Blob(['png'], { type: 'image/png' });
    getPrinterHistoryThumbnailMock.mockResolvedValue(blob);

    renderHistory(createJob('/api/printers/printer-1/history/job-1/thumbnail'));

    await waitFor(() =>
      expect(getPrinterHistoryThumbnailMock).toHaveBeenCalledWith(
        'printer-1',
        'job-1',
        expect.any(AbortSignal)
      )
    );
    expect(URL.createObjectURL).toHaveBeenCalledWith(blob);
    expect(
      await screen.findByRole('img', { name: 'calibration.gcode thumbnail' })
    ).toHaveAttribute('src', 'blob:history-thumbnail');
  });

  it('revokes the object URL when the thumbnail unmounts', async () => {
    getPrinterHistoryThumbnailMock.mockResolvedValue(
      new Blob(['png'], { type: 'image/png' })
    );
    const { unmount } = renderHistory(
      createJob('/api/printers/printer-1/history/job-1/thumbnail')
    );

    await screen.findByRole('img', { name: 'calibration.gcode thumbnail' });
    unmount();

    expect(URL.revokeObjectURL).toHaveBeenCalledWith(
      'blob:history-thumbnail'
    );
  });

  it('revokes and replaces a blob that the browser cannot render', async () => {
    getPrinterHistoryThumbnailMock.mockResolvedValue(
      new Blob(['png'], { type: 'image/png' })
    );
    renderHistory(createJob('/api/printers/printer-1/history/job-1/thumbnail'));
    const image = await screen.findByRole('img', {
      name: 'calibration.gcode thumbnail',
    });

    fireEvent.error(image);

    expect(URL.revokeObjectURL).toHaveBeenCalledWith(
      'blob:history-thumbnail'
    );
    expect(
      screen.getByRole('img', {
        name: 'Thumbnail unavailable for calibration.gcode',
      })
    ).toBeInTheDocument();
  });

  it('renders the unavailable placeholder when authenticated fetch fails', async () => {
    getPrinterHistoryThumbnailMock.mockRejectedValue({
      response: { status: 401 },
    });

    renderHistory(createJob('/api/printers/printer-1/history/job-1/thumbnail'));

    expect(
      await screen.findByRole('img', {
        name: 'Thumbnail unavailable for calibration.gcode',
      })
    ).toBeInTheDocument();
    await waitFor(() =>
      expect(getPrinterHistoryThumbnailMock).toHaveBeenCalledTimes(1)
    );
  });

  it('renders a non-proxied backend thumbnail as a plain <img> without hitting the authenticated proxy (#1584 blocker 3)', () => {
    // Moonraker/OctoPrint/SDCP return direct thumbnails from the printer's own
    // web server, not `/api/...`. The authenticated proxy is Prusa-only —
    // routing everyone through it turned every non-Prusa thumbnail into a
    // permanent placeholder.
    renderHistory(createJob('http://printer.local/thumbs/job-1.png'));

    const image = screen.getByRole('img', {
      name: 'calibration.gcode thumbnail',
    });
    expect(image).toHaveAttribute('src', 'http://printer.local/thumbs/job-1.png');
    expect(getPrinterHistoryThumbnailMock).not.toHaveBeenCalled();
  });

  it('falls back to the established placeholder when a direct backend thumbnail fails to load', () => {
    renderHistory(createJob('http://printer.local/thumbs/job-1.png'));
    const image = screen.getByRole('img', {
      name: 'calibration.gcode thumbnail',
    });

    fireEvent.error(image);

    expect(
      screen.getByRole('img', {
        name: 'Thumbnail unavailable for calibration.gcode',
      })
    ).toBeInTheDocument();
    expect(getPrinterHistoryThumbnailMock).not.toHaveBeenCalled();
  });

  it('does not treat a Prusa upstream URL as same-origin proxy (case-insensitive same-origin check only)', () => {
    // Guard against the Prusa upstream (e.g. `http://prusa/api/v1/job/…`) or
    // any other backend that happens to include `/api/` mid-path from being
    // exposed to the browser via the auth-blob loader. Only exact
    // same-origin `/api/…` refs should be proxied.
    renderHistory(createJob('http://prusa-mk4.local/api/v1/job/1/thumbnail'));

    const image = screen.getByRole('img', {
      name: 'calibration.gcode thumbnail',
    });
    expect(image).toHaveAttribute('src', 'http://prusa-mk4.local/api/v1/job/1/thumbnail');
    expect(getPrinterHistoryThumbnailMock).not.toHaveBeenCalled();
  });
});
