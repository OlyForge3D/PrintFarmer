import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactElement, ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { SliceProgressOverlay } from '@/features/slicer/components/SliceProgressOverlay';
import type { SliceJobProgressState } from '@/features/slicer/hooks/useSliceJobProgress';

const downloadGcodeArtifact = vi.fn();
const saveGcodeArtifactToLibrary = vi.fn();
const resolveGcodeArtifactForAction = vi.fn();

vi.mock('@/features/slicer/utils/sliceArtifactActions', () => ({
  downloadGcodeArtifact: (...args: unknown[]) => downloadGcodeArtifact(...args),
  saveGcodeArtifactToLibrary: (...args: unknown[]) => saveGcodeArtifactToLibrary(...args),
  resolveGcodeArtifactForAction: (...args: unknown[]) => resolveGcodeArtifactForAction(...args),
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
  SendToPrinterModal: ({
    isOpen,
    jobId,
    artifactId,
    defaultPrinterId,
  }: {
    isOpen: boolean;
    jobId: string;
    artifactId: string;
    defaultPrinterId?: string | null;
  }) => isOpen
    ? <div data-testid="print-modal" data-printer-id={defaultPrinterId}>{jobId}:{artifactId}</div>
    : null,
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

function renderOverlay(ui: ReactElement) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  const Wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  );
  return { queryClient, ...render(ui, { wrapper: Wrapper }) };
}

describe('SliceProgressOverlay completion actions', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    downloadGcodeArtifact.mockResolvedValue(undefined);
    resolveGcodeArtifactForAction.mockReset();
  });

  it('reveals persistent actions from a live completion without auto-opening preview', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    const windowOpen = vi.spyOn(window, 'open');

    try {
      const { rerender } = renderOverlay(
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

      expect(await screen.findByRole('button', { name: 'Preview' })).toBeInTheDocument();
      expect(screen.queryByText(/Preview modal for/)).not.toBeInTheDocument();
      expect(screen.getByRole('button', { name: 'Save to Library' })).toBeInTheDocument();
      expect(screen.getByRole('button', { name: 'Print' })).toBeInTheDocument();
      expect(saveGcodeArtifactToLibrary).not.toHaveBeenCalled();
      const downloadButton = screen.getByRole('button', { name: 'Download G-code' });

      await user.click(downloadButton);
      expect(downloadGcodeArtifact).toHaveBeenCalledWith('/api/artifacts/job/job-1');
      expect(windowOpen).not.toHaveBeenCalled();
      expect(screen.queryByText(/Preview modal for/)).not.toBeInTheDocument();

      await user.click(screen.getByRole('button', { name: 'Preview' }));
      expect(screen.getByText('Preview modal for /api/artifacts/job/job-1')).toBeInTheDocument();

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
      expect(screen.queryByText(/Preview modal for/)).not.toBeInTheDocument();
      expect(screen.getByRole('button', { name: 'Preview' })).toBeInTheDocument();
      expect(screen.getByRole('button', { name: 'Download G-code' })).toBeInTheDocument();
      expect(screen.getByRole('button', { name: 'Save to Library' })).toBeInTheDocument();
      expect(screen.getByRole('button', { name: 'Print' })).toBeInTheDocument();
    } finally {
      windowOpen.mockRestore();
      vi.useRealTimers();
    }
  });

  it('does not open preview when a completed job remounts or receives repeated completed updates', () => {
    const completedProgress: SliceJobProgressState = {
      ...processingProgress,
      status: 'Completed',
      progressPercent: 100,
      artifactsRoute: '/api/artifacts/job/job-1',
    };
    const { rerender, unmount } = renderOverlay(
      <SliceProgressOverlay jobId="job-1" progress={completedProgress} onNewJob={vi.fn()} onRetry={vi.fn()} />,
    );
    for (let i = 0; i < 3; i++) {
      rerender(
        <SliceProgressOverlay
          jobId="job-1"
          progress={{ ...completedProgress, filamentUsedGrams: 10 + i }}
          onNewJob={vi.fn()}
          onRetry={vi.fn()}
        />,
      );
    }
    expect(screen.queryByText(/Preview modal for/)).not.toBeInTheDocument();
    unmount();

    renderOverlay(
      <SliceProgressOverlay jobId="job-1" progress={completedProgress} onNewJob={vi.fn()} onRetry={vi.fn()} />,
    );
    expect(screen.queryByText(/Preview modal for/)).not.toBeInTheDocument();
  });

  it('offers dismissal only for a completed job, separate from New Job', async () => {
    const user = userEvent.setup();
    const onDismiss = vi.fn();
    const onNewJob = vi.fn();
    const completedProgress: SliceJobProgressState = {
      ...processingProgress,
      status: 'Completed',
      progressPercent: 100,
      artifactsRoute: '/api/artifacts/job/job-1',
    };
    const { rerender } = renderOverlay(
      <SliceProgressOverlay jobId="job-1" progress={processingProgress} onNewJob={onNewJob} onRetry={vi.fn()} onDismiss={onDismiss} />,
    );
    expect(screen.queryByRole('button', { name: 'Hide slice results' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Hide results' })).not.toBeInTheDocument();

    rerender(
      <SliceProgressOverlay
        jobId="job-1"
        progress={{ ...processingProgress, status: 'Failed', error: 'boom' }}
        onNewJob={onNewJob}
        onRetry={vi.fn()}
        onDismiss={onDismiss}
      />,
    );
    expect(screen.queryByRole('button', { name: 'Hide slice results' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Hide results' })).not.toBeInTheDocument();

    rerender(
      <SliceProgressOverlay jobId="job-1" progress={completedProgress} onNewJob={onNewJob} onRetry={vi.fn()} onDismiss={onDismiss} />,
    );
    await user.click(screen.getByRole('button', { name: 'Hide slice results' }));
    await user.click(screen.getByRole('button', { name: 'Hide results' }));
    expect(onDismiss).toHaveBeenCalledTimes(2);
    expect(onNewJob).not.toHaveBeenCalled();
  });

  it('does not dismiss the overlay when Escape is pressed inside the nested preview', async () => {
    const user = userEvent.setup();
    const onDismiss = vi.fn();
    const onNewJob = vi.fn();
    renderOverlay(
      <SliceProgressOverlay
        jobId="job-1"
        progress={{ ...processingProgress, status: 'Completed', progressPercent: 100, artifactsRoute: '/api/artifacts/job/job-1' }}
        onNewJob={onNewJob}
        onRetry={vi.fn()}
        onDismiss={onDismiss}
      />,
    );
    await user.click(screen.getByRole('button', { name: 'Preview' }));
    await user.keyboard('{Escape}');
    expect(onDismiss).not.toHaveBeenCalled();
    expect(onNewJob).not.toHaveBeenCalled();
  });

  it('moves focus to the dismiss control only when explicitly reopened', () => {
    const completedProgress: SliceJobProgressState = {
      ...processingProgress,
      status: 'Completed',
      progressPercent: 100,
      artifactsRoute: '/api/artifacts/job/job-1',
    };
    const { unmount } = renderOverlay(
      <SliceProgressOverlay jobId="job-1" progress={completedProgress} onNewJob={vi.fn()} onRetry={vi.fn()} onDismiss={vi.fn()} />,
    );
    expect(screen.getByRole('button', { name: 'Hide slice results' })).not.toHaveFocus();
    unmount();

    renderOverlay(
      <SliceProgressOverlay
        jobId="job-1"
        progress={completedProgress}
        onNewJob={vi.fn()}
        onRetry={vi.fn()}
        onDismiss={vi.fn()}
        focusDismissOnMount
      />,
    );
    expect(screen.getByRole('button', { name: 'Hide slice results' })).toHaveFocus();
  });

  it('saves explicitly with single-flight protection and invalidates a newly created file', async () => {
    let resolveSave!: (value: { createdNew: boolean; gcodeFileId: string }) => void;
    saveGcodeArtifactToLibrary.mockImplementation(
      () => new Promise(resolve => {
        resolveSave = resolve;
      }),
    );

    const completedProgress: SliceJobProgressState = {
      ...processingProgress,
      status: 'Completed',
      progressPercent: 100,
      artifactsRoute: '/api/artifacts/job/job-1',
    };
    const { queryClient } = renderOverlay(
      <SliceProgressOverlay
        jobId="job-1"
        progress={completedProgress}
        onNewJob={vi.fn()}
        onRetry={vi.fn()}
      />,
    );
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
    const saveButton = screen.getByRole('button', { name: 'Save to Library' });

    fireEvent.click(saveButton);
    fireEvent.click(saveButton);

    expect(saveGcodeArtifactToLibrary).toHaveBeenCalledOnce();
    expect(saveGcodeArtifactToLibrary).toHaveBeenCalledWith(
      '/api/artifacts/job/job-1',
      'job-1',
    );

    await act(async () => {
      resolveSave({ createdNew: true, gcodeFileId: 'file-1' });
    });

    expect(invalidate).toHaveBeenCalledOnce();
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['file-browser'] });
    expect(await screen.findByRole('status')).toHaveTextContent('Saved to Library');
  });

  it('surfaces save errors and allows retry without invalidating an existing file', async () => {
    saveGcodeArtifactToLibrary
      .mockRejectedValueOnce(new Error('Promotion failed'))
      .mockResolvedValueOnce({ createdNew: false, gcodeFileId: 'file-1' });

    const completedProgress: SliceJobProgressState = {
      ...processingProgress,
      status: 'Completed',
      progressPercent: 100,
      artifactsRoute: '/api/artifacts/job/job-1',
    };
    const { queryClient } = renderOverlay(
      <SliceProgressOverlay
        jobId="job-1"
        progress={completedProgress}
        onNewJob={vi.fn()}
        onRetry={vi.fn()}
      />,
    );
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');

    fireEvent.click(screen.getByRole('button', { name: 'Save to Library' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Promotion failed');

    fireEvent.click(screen.getByRole('button', { name: 'Save to Library' }));
    await waitFor(() => expect(saveGcodeArtifactToLibrary).toHaveBeenCalledTimes(2));
    expect(await screen.findByRole('status')).toHaveTextContent('Already in File Library');
    expect(invalidate).not.toHaveBeenCalled();
  });

  it('opens Print with the exact selected artifact when multiple artifacts exist', async () => {
    const artifacts = [
      { id: 'newest-artifact', fileName: 'newest.gcode', isPrimary: false },
      { id: 'primary-artifact', fileName: 'primary.gcode', isPrimary: true },
    ];
    resolveGcodeArtifactForAction.mockResolvedValue(artifacts[1]);
    const completedProgress: SliceJobProgressState = {
      ...processingProgress,
      status: 'Completed',
      progressPercent: 100,
      artifactsRoute: '/api/artifacts/job/job-1',
    };
    renderOverlay(
      <SliceProgressOverlay
        jobId="job-1"
        defaultPrinterId="sliced-for-printer"
        progress={completedProgress}
        onNewJob={vi.fn()}
        onRetry={vi.fn()}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Print' }));

    expect(await screen.findByTestId('print-modal')).toHaveTextContent(
      'job-1:primary-artifact',
    );
    expect(resolveGcodeArtifactForAction).toHaveBeenCalledWith(
      '/api/artifacts/job/job-1',
    );
    expect(screen.getByTestId('print-modal')).not.toHaveTextContent('newest-artifact');
    expect(screen.getByTestId('print-modal')).toHaveAttribute('data-printer-id', 'sliced-for-printer');
  });
});
