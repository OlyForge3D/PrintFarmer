import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { SliceProgressOverlay } from '@/features/slicer/components/SliceProgressOverlay';
import type { SliceJobProgressState } from '@/features/slicer/hooks/useSliceJobProgress';

const downloadGcodeArtifact = vi.fn();

vi.mock('@/features/slicer/utils/sliceArtifactActions', () => ({
  downloadGcodeArtifact: (...args: unknown[]) => downloadGcodeArtifact(...args),
}));

vi.mock('@/features/slicer/components/GcodePreviewModal', () => ({
  GcodePreviewModal: ({
    isOpen,
    onClose,
    artifactsRoute,
  }: {
    isOpen: boolean;
    onClose: () => void;
    artifactsRoute: string;
  }) => isOpen ? (
    <div>
      <span>Preview modal for {artifactsRoute}</span>
      <button type="button" onClick={onClose}>Close preview</button>
    </div>
  ) : null,
}));

vi.mock('@/features/slicer/components/SendToPrinterModal', () => ({
  SendToPrinterModal: () => null,
}));

const processingProgress: SliceJobProgressState = {
  progressPercent: 80,
  progressMessage: 'Slicing',
  status: 'Processing',
  estimatedPrintTimeSeconds: null,
  filamentUsedGrams: null,
  artifactsRoute: null,
  error: null,
  isConnected: true,
};

describe('SliceProgressOverlay completion actions', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    downloadGcodeArtifact.mockResolvedValue(undefined);
  });

  it('reveals persistent actions from a live completion and auto-opens preview only once', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    const windowOpen = vi.spyOn(window, 'open');

    try {
      const { rerender } = render(
        <SliceProgressOverlay
          jobId="job-1"
          progress={processingProgress}
          onNewJob={vi.fn()}
          onRetry={vi.fn()}
        />,
      );

      const completedProgress: SliceJobProgressState = {
        ...processingProgress,
        status: 'Completed',
        progressPercent: 100,
        artifactsRoute: '/api/artifacts/job/job-1',
      };
      rerender(
        <SliceProgressOverlay
          jobId="job-1"
          progress={completedProgress}
          onNewJob={vi.fn()}
          onRetry={vi.fn()}
        />,
      );

      expect(await screen.findByText(/Preview modal for/)).toBeInTheDocument();
      expect(screen.getByRole('button', { name: 'Preview' })).toBeInTheDocument();
      const downloadButton = screen.getByRole('button', { name: 'Download G-code' });

      await user.click(downloadButton);
      expect(downloadGcodeArtifact).toHaveBeenCalledWith('/api/artifacts/job/job-1');
      expect(windowOpen).not.toHaveBeenCalled();

      await user.click(screen.getByRole('button', { name: 'Close preview' }));
      rerender(
        <SliceProgressOverlay
          jobId="job-1"
          progress={{ ...completedProgress }}
          onNewJob={vi.fn()}
          onRetry={vi.fn()}
        />,
      );
      expect(screen.queryByText(/Preview modal for/)).not.toBeInTheDocument();

      await act(async () => { await vi.advanceTimersByTimeAsync(5000); });
      expect(screen.getByRole('button', { name: 'Preview' })).toBeInTheDocument();
      expect(screen.getByRole('button', { name: 'Download G-code' })).toBeInTheDocument();
    } finally {
      windowOpen.mockRestore();
      vi.useRealTimers();
    }
  });
});
